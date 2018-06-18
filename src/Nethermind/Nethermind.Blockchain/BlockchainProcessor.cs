/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Difficulty;
using Nethermind.Blockchain.Validators;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Store;

namespace Nethermind.Blockchain
{
    public class BlockchainProcessor : IBlockchainProcessor
    {
        private static readonly BigInteger MinGasPriceForMining = 1;
        private readonly IBlockProcessor _blockProcessor;
        private readonly IEthereumSigner _signer;
        private readonly IBlockTree _blockTree;
        private readonly IDifficultyCalculator _difficultyCalculator;
        private readonly ILogger _logger;
        private readonly ISealEngine _sealEngine;

        private readonly BlockingCollection<Block> _recoveryQueue = new BlockingCollection<Block>(new ConcurrentQueue<Block>());
        private readonly BlockingCollection<Block> _blockQueue = new BlockingCollection<Block>(new ConcurrentQueue<Block>());
        private readonly ITransactionStore _transactionStore;

        public BlockchainProcessor(
            IBlockTree blockTree,
            ISealEngine sealEngine,
            ITransactionStore transactionStore,
            IDifficultyCalculator difficultyCalculator,
            IBlockProcessor blockProcessor,
            IEthereumSigner signer,
            ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _blockTree = blockTree;
            _blockTree.NewBestSuggestedBlock += OnNewBestBlock;

            _transactionStore = transactionStore;
            _difficultyCalculator = difficultyCalculator;
            _sealEngine = sealEngine;
            _blockProcessor = blockProcessor;
            _signer = signer;
        }

        private void OnNewBestBlock(object sender, BlockEventArgs blockEventArgs)
        {
            if (blockEventArgs.Block == null)
            {
                throw new InvalidOperationException("New best block is null");
            }

            _miningCancellation?.Cancel();
            EnqueueForProcessing(blockEventArgs.Block);
        }

        private CancellationTokenSource _loopCancellationSource;
        private CancellationTokenSource _miningCancellation;

        private Task _recoveryTask;
        private Task _processorTask;

        public async Task StopAsync(bool processRamainingBlocks)
        {
            if (processRamainingBlocks)
            {
                _recoveryQueue.CompleteAdding();
                await _recoveryTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger.Error("Transaction sender recovery task failure.", t.Exception);
                    }
                });

                _blockQueue.CompleteAdding();
            }
            else
            {
                _loopCancellationSource.Cancel();
            }

            await Task.WhenAll(_recoveryTask, _processorTask);
        }

        public void Start()
        {
            _loopCancellationSource = new CancellationTokenSource();
            _recoveryTask = Task.Factory.StartNew(
                RunRecoveryLoop,
                _loopCancellationSource.Token,
                TaskCreationOptions.None,
                TaskScheduler.Default).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsErrorEnabled)
                    {
                        _logger.Error("Sender address recovery encountered an exception.", t.Exception);
                    }
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info("Sender address recovery stopped.");
                    }
                }
                else if (t.IsCompleted)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info("Sender address recovery complete.");
                    }
                }
            });

            _processorTask = Task.Factory.StartNew(
                RunProcessingLoop,
                _loopCancellationSource.Token,
                TaskCreationOptions.None,
                TaskScheduler.Default).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    if (_logger.IsErrorEnabled)
                    {
                        _logger.Error($"{nameof(BlockchainProcessor)} encountered an exception.", t.Exception);
                    }
                }
                else if (t.IsCanceled)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"{nameof(BlockchainProcessor)} stopped.");
                    }
                }
                else if (t.IsCompleted)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Info($"{nameof(BlockchainProcessor)} complete.");
                    }
                }
            });
        }

        private void RunRecoveryLoop()
        {
            if (_logger.IsInfoEnabled)
            {
                _processingWatch.Start();
                _logger.Info($"Starting recovery loop - {_blockQueue.Count} blocks waiting in the queue.");
            }

            foreach (Block block in _recoveryQueue.GetConsumingEnumerable(_loopCancellationSource.Token))
            {
                if (_logger.IsInfoEnabled)
                {
                    _logger.Debug($"Recovering addresses for block {block.ToString(Block.Format.Short)}).");
                }

                _signer.RecoverAddresses(block);
                _blockQueue.Add(block);
            }
        }

        private void RunProcessingLoop()
        {
            if (_logger.IsInfoEnabled)
            {
                _processingWatch.Start();
                _logger.Info($"Starting block processor - {_blockQueue.Count} blocks waiting in the queue.");
            }

            if (_blockQueue.Count == 0 && _sealEngine.IsMining)
            {
                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug("Nothing in the queue so I mine my own.");
                }

                BuildAndSeal();

                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug("Will go and wait for another block now...");
                }
            }

            foreach (Block block in _blockQueue.GetConsumingEnumerable(_loopCancellationSource.Token))
            {
                if (_logger.IsInfoEnabled)
                {
                    _logger.Debug($"Processing block {block.ToString(Block.Format.Short)}).");
                }

                if (_blockQueue.Count == 0)
                {
                    _wasQueueEmptied = true;
                }

                Process(block);

                if (_logger.IsDebugEnabled) // TODO: different levels depending on the queue size?
                {
                    _logger.Debug($"Now {_blockQueue.Count} blocks waiting in the queue.");
                }

                if (_blockQueue.Count == 0 && _sealEngine.IsMining)
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug("Nothing in the queue so I mine my own.");
                    }

                    BuildAndSeal();

                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug("Will go and wait for another block now...");
                    }
                }
            }
        }

        private void EnqueueForProcessing(Block block)
        {
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Enqueuing a new block {block.ToString(Block.Format.Short)} for processing.");
            }

            _recoveryQueue.Add(block);

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"A new block {block.ToString(Block.Format.Short)} enqueued for processing.");
            }
        }

        // TODO: there will be a need for cancellation of current mining whenever a new block arrives
        private void BuildAndSeal()
        {
            BlockHeader parentHeader = _blockTree.Head;
            if (parentHeader == null)
            {
                return; // TODO: review
            }

            Block parent = _blockTree.FindBlock(parentHeader.Hash, false);
            BigInteger timestamp = Timestamp.UnixUtcUntilNowSecs;

            BigInteger difficulty = _difficultyCalculator.Calculate(parent.Difficulty, parent.Timestamp, Timestamp.UnixUtcUntilNowSecs, parent.Number + 1, parent.Ommers.Length > 0);
            BlockHeader header = new BlockHeader(
                parent.Hash,
                Keccak.OfAnEmptySequenceRlp,
                Address.Zero,
                difficulty,
                parent.Number + 1,
                parent.GasLimit,
                timestamp > parent.Timestamp ? timestamp : parent.Timestamp + 1,
                Encoding.UTF8.GetBytes("Nethermind"));

            header.TotalDifficulty = parent.TotalDifficulty + difficulty;
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Setting total difficulty to {parent.TotalDifficulty} + {difficulty}.");
            }

            var transactions = _transactionStore.GetAllPending().OrderBy(t => t?.Nonce); // by nonce in case there are two transactions for the same account, TODO: test it

            List<Transaction> selected = new List<Transaction>();
            BigInteger gasRemaining = header.GasLimit;

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Collecting pending transactions at min gas price {MinGasPriceForMining} and block gas limit {gasRemaining}.");
            }

            int total = 0;
            foreach (Transaction transaction in transactions)
            {
                total++;
                if (transaction == null)
                {
                    throw new InvalidOperationException("Block transaction is null");
                }

                if (transaction.GasPrice < MinGasPriceForMining)
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"Rejecting transaction - gas price ({transaction.GasPrice}) too low (min gas price: {MinGasPriceForMining}.");
                    }

                    continue;
                }

                if (transaction.GasLimit > gasRemaining)
                {
                    if (_logger.IsInfoEnabled)
                    {
                        _logger.Debug($"Rejecting transaction - gas limit ({transaction.GasPrice}) more than remaining gas ({gasRemaining}).");
                    }

                    break;
                }

                selected.Add(transaction);
                gasRemaining -= transaction.GasLimit;
            }

            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Collected {selected.Count} out of {total} pending transactions.");
            }

            header.TransactionsRoot = GetTransactionsRoot(selected);

            Block block = new Block(header, selected, new BlockHeader[0]);
            Process(block, true);
        }

        private Keccak GetTransactionsRoot(List<Transaction> transactions)
        {
            if (transactions.Count == 0)
            {
                return PatriciaTree.EmptyTreeHash;
            }

            PatriciaTree txTree = new PatriciaTree();
            for (int i = 0; i < transactions.Count; i++)
            {
                Rlp transactionRlp = Rlp.Encode(transactions[i]);
                txTree.Set(Rlp.Encode(i).Bytes, transactionRlp);
            }

            txTree.UpdateRootHash();
            return txTree.RootHash;
        }

        // TODO: move these stats into a separate class
        private readonly Stopwatch _processingWatch = new Stopwatch(); // TODO: TEMP?
        private long _lastElapsedTicks;
        private decimal _lastTotalMGas;
        private long _lastTotalTx;
        private decimal _currentTotalMGas;
        private long _currentTotalTx;
        private long _lastStateDbReads;
        private long _lastStateDbWrites;
        private long _lastGen0;
        private long _lastGen1;
        private long _lastGen2;
        private long _lastTreeNodeRlp;
        private long _lastEvmExceptions;
        private long _lastSelfDestructs;
        private long _maxMemory;
        private bool _wasQueueEmptied;

        public void Process(Block suggestedBlock)
        {
            Process(suggestedBlock, false);
            _currentTotalMGas += suggestedBlock.GasUsed / 1_000_000m;
            _currentTotalTx += suggestedBlock.Transactions.Length;
            //            
            long currentTicks = _processingWatch.ElapsedTicks;
            decimal chunkMicroseconds = (_processingWatch.ElapsedTicks - _lastElapsedTicks) * (1_000_000m / Stopwatch.Frequency);
            if (chunkMicroseconds > 10* 1000 * 1000 || _wasQueueEmptied) // 10s
            {
                _wasQueueEmptied = false;
                long currentGen0 = GC.CollectionCount(0);
                long currentGen1 = GC.CollectionCount(1);
                long currentGen2 = GC.CollectionCount(2);
                long currentMemory = GC.GetTotalMemory(false);
                _maxMemory = Math.Max(_maxMemory, currentMemory);
                long currentStateDbReads = Metrics.StateDbReads;
                long currentStateDbWrites = Metrics.StateDbWrites;
                long currentTreeNodeRlp = Metrics.TreeNodeRlpEncodings + Metrics.TreeNodeRlpDecodings;
                long evmExceptions = Metrics.EvmExceptions;
                long currentSelfDestructs = Metrics.SelfDestructs;

                long chunkTx = _currentTotalTx - _lastTotalTx;
                decimal chunkMGas = _currentTotalMGas - _lastTotalMGas;
                decimal mgasPerSecond = chunkMGas / chunkMicroseconds * 1000 * 1000;
                decimal totalMgasPerSecond = _currentTotalMGas / currentTicks * 1000;
                decimal txps = chunkTx / chunkMicroseconds * 1000m * 1000m;
                if(_logger.IsInfoEnabled) _logger.Info($"Processed blocks up to {suggestedBlock.Number,9} in {chunkMicroseconds / 1000,7:N0}ms, tx={chunkTx,5} mgas={chunkMGas,8:F2}, mgasps={mgasPerSecond,7:F2}, txps={txps,7:F2}, total mgasps={totalMgasPerSecond,7:F2}, queue={_blockQueue.Count}");
                if(_logger.IsDebugEnabled) _logger.Debug($"Gen0: {currentGen0 - _lastGen0,6}, Gen1: {currentGen1 - _lastGen1,6}, Gen2: {currentGen2 - _lastGen2,6}, maxmem: {_maxMemory / 1000000,5}, mem: {currentMemory / 1000000,5}, reads: {currentStateDbReads - _lastStateDbReads,9}, writes: {currentStateDbWrites - _lastStateDbWrites,9}, rlp: {currentTreeNodeRlp - _lastTreeNodeRlp,9}, exceptions:{evmExceptions - _lastEvmExceptions}, selfdstrcs={currentSelfDestructs - _lastSelfDestructs}");
                _lastTotalMGas = _currentTotalMGas;
                _lastElapsedTicks = currentTicks;
                _lastTotalTx = _currentTotalTx;
                _lastGen0 = currentGen0;
                _lastGen1 = currentGen1;
                _lastGen2 = currentGen2;
                _lastStateDbReads = currentStateDbReads;
                _lastStateDbWrites = currentStateDbWrites;
                _lastTreeNodeRlp = currentTreeNodeRlp;
                _lastEvmExceptions = evmExceptions;
                _lastSelfDestructs = currentSelfDestructs;
            }
        }

        private void Process(Block suggestedBlock, bool forMining)
        {
            if (suggestedBlock.Number != 0 && _blockTree.FindParent(suggestedBlock) == null)
            {
                throw new InvalidOperationException("Got an orphaned block for porcessing.");
            }

            if (suggestedBlock.Header.TotalDifficulty == null)
            {
                throw new InvalidOperationException("block without total difficulty calculated was suggested for processing");
            }

            if (!forMining && suggestedBlock.Hash == null)
            {
                throw new InvalidOperationException("block hash should be known at this stage if the block is not mining");
            }

            foreach (BlockHeader ommerHeader in suggestedBlock.Ommers)
            {
                if (ommerHeader.Hash == null)
                {
                    throw new InvalidOperationException("ommer's hash is null when processing block");
                }
            }

            BigInteger totalDifficulty = suggestedBlock.TotalDifficulty ?? 0;
            BigInteger totalTransactions = suggestedBlock.TotalTransactions ?? 0;
            if (_logger.IsDebugEnabled)
            {
                _logger.Debug($"Total difficulty of block {suggestedBlock.ToString(Block.Format.Short)} is {totalDifficulty}");
                _logger.Debug($"Total transactions of block {suggestedBlock.ToString(Block.Format.Short)} is {totalTransactions}");
            }

            if (totalDifficulty > (_blockTree.Head?.TotalDifficulty ?? 0))
            {
                List<Block> blocksToBeAddedToMain = new List<Block>();
                Block toBeProcessed = suggestedBlock;
                do
                {
                    blocksToBeAddedToMain.Add(toBeProcessed);
                    toBeProcessed = toBeProcessed.Number == 0 ? null : _blockTree.FindParent(toBeProcessed);
                    // TODO: need to remove the hardcoded head block store at keccak zero as it would be referenced by the genesis... 

                    if (toBeProcessed == null)
                    {
                        break;
                    }
                } while (!_blockTree.IsMainChain(toBeProcessed.Hash));

                BlockHeader branchingPoint = toBeProcessed?.Header;
                if (branchingPoint != null && branchingPoint.Hash != _blockTree.Head?.Hash)
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"Head block was: {_blockTree.Head?.ToString(BlockHeader.Format.Short)}");
                        _logger.Debug($"Branching from: {branchingPoint.ToString(BlockHeader.Format.Short)}");
                    }
                }
                else
                {
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug(branchingPoint == null ? "Setting as genesis block" : $"Adding on top of {branchingPoint.ToString(BlockHeader.Format.Short)}");
                    }
                }

                Keccak stateRoot = branchingPoint?.StateRoot;
                if (_logger.IsTraceEnabled)
                {
                    _logger.Trace($"State root lookup: {stateRoot}");
                }

                List<Block> unprocessedBlocksToBeAddedToMain = new List<Block>();

                foreach (Block block in blocksToBeAddedToMain)
                {
                    if (!forMining && _blockTree.WasProcessed(block.Hash))
                    {
                        stateRoot = block.Header.StateRoot;
                        if (_logger.IsTraceEnabled)
                        {
                            _logger.Trace($"State root lookup: {stateRoot}");
                        }

                        break;
                    }

                    unprocessedBlocksToBeAddedToMain.Add(block);
                }

                Block[] blocks = new Block[unprocessedBlocksToBeAddedToMain.Count];
                for (int i = 0; i < unprocessedBlocksToBeAddedToMain.Count; i++)
                {
                    blocks[blocks.Length - i - 1] = unprocessedBlocksToBeAddedToMain[i];
                }

                if (_logger.IsDebugEnabled)
                {
                    _logger.Debug($"Processing {blocks.Length} blocks from state root {stateRoot}");
                }

                //TODO: process blocks one by one here, refactor this, test
                for (int i = 0; i < blocks.Length; i++)
                {
                    if (blocks[i].Transactions.Length > 0 && blocks[i].Transactions[0].SenderAddress == null)
                    {
                        _signer.RecoverAddresses(blocks[i]);
                    }
                }
                Block[] processedBlocks = _blockProcessor.Process(stateRoot, blocks, forMining);

                // TODO: lots of unnecessary loading and decoding here, review after adding support for loading headers only
                List<BlockHeader> blocksToBeRemovedFromMain = new List<BlockHeader>();
                if (_blockTree.Head?.Hash != branchingPoint?.Hash && _blockTree.Head != null)
                {
                    blocksToBeRemovedFromMain.Add(_blockTree.Head);
                    BlockHeader teBeRemovedFromMain = _blockTree.FindHeader(_blockTree.Head.ParentHash);
                    while (teBeRemovedFromMain != null && teBeRemovedFromMain.Hash != branchingPoint?.Hash)
                    {
                        blocksToBeRemovedFromMain.Add(teBeRemovedFromMain);
                        teBeRemovedFromMain = _blockTree.FindHeader(teBeRemovedFromMain.ParentHash);
                    }
                }

                if (!forMining)
                {
                    foreach (Block processedBlock in processedBlocks)
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"Marking {processedBlock.ToString(Block.Format.Short)} as processed");
                        }

                        // TODO: review storage and retrieval of receipts since we removed them from the block class
                        _blockTree.MarkAsProcessed(processedBlock.Hash);
                    }

                    Block newHeadBlock = processedBlocks[processedBlocks.Length - 1];
                    newHeadBlock.Header.TotalDifficulty = suggestedBlock.TotalDifficulty; // TODO: cleanup total difficulty
                    if (_logger.IsDebugEnabled)
                    {
                        _logger.Debug($"Setting head block to {newHeadBlock.ToString(Block.Format.Short)}");
                    }

                    foreach (BlockHeader blockHeader in blocksToBeRemovedFromMain)
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"Moving {blockHeader.ToString(BlockHeader.Format.Short)} to branch");
                        }

                        _blockTree.MoveToBranch(blockHeader.Hash);
                        // TODO: only for miners
                        //foreach (Transaction transaction in block.Transactions)
                        //{
                        //    _transactionStore.AddPending(transaction);
                        //}

                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"Block {blockHeader.ToString(BlockHeader.Format.Short)} moved to branch");
                        }
                    }

                    foreach (Block block in blocksToBeAddedToMain)
                    {
                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"Moving {block.ToString(Block.Format.Short)} to main");
                        }

                        _blockTree.MoveToMain(block);
                        // TODO: only for miners
                        //foreach (Transaction transaction in block.Transactions)
                        //{
                        //    _transactionStore.RemovePending(transaction);
                        //}

                        if (_logger.IsDebugEnabled)
                        {
                            _logger.Debug($"Block {block.ToString(Block.Format.Short)} added to main chain");
                        }
                    }

                    if (_logger.IsDebugEnabled) _logger.Debug($"Updating total difficulty of the main chain to {totalDifficulty}");
                    if (_logger.IsDebugEnabled) _logger.Debug($"Updating total transactions of the main chain to {totalTransactions}");
                }
                else
                {
                    Block blockToBeMined = processedBlocks[processedBlocks.Length - 1];
                    _miningCancellation = new CancellationTokenSource();
                    CancellationTokenSource anyCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(_miningCancellation.Token, _loopCancellationSource.Token);
                    _sealEngine.MineAsync(blockToBeMined, anyCancellation.Token).ContinueWith(t =>
                    {
                        anyCancellation.Dispose();

                        if (_logger.IsInfoEnabled) _logger.Info($"Mined a block {t.Result.ToString(Block.Format.Short)} with parent {t.Result.Header.ParentHash}");

                        Block minedBlock = t.Result;

                        if (minedBlock.Hash == null)
                        {
                            throw new InvalidOperationException("Mined a block with null hash");
                        }

                        _blockTree.SuggestBlock(minedBlock);
                    }, _miningCancellation.Token);
                }
            }
        }
    }
}