// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing.GethStyle.Custom.JavaScript;
using Nethermind.Logging;
using NonBlocking;

namespace Nethermind.Network.Discovery.Kademlia;

/// Single array of kbucket kademlia implementation.
/// Not even the splitting variant.
/// With a proper splitting variant, the closest kbucket will be full and less sparse, so the findNeighbour query
/// is more accurate without having to spill over to other kbucket to fill the query.
/// This is even more so with tree based kbucket where bucket without currentid can also be splitted down (to a predefined
/// limit) which makes the lookup even more accurate.
///
/// TODO: Switch to tree based kademlia implementation.
public class Kademlia<TNode, TContentKey, TContent> : IKademlia<TNode, TContentKey, TContent> where TNode : notnull
{
    private IKademlia<TNode, TContentKey, TContent>.IStore _store;
    private readonly static TimeSpan FindNeighbourHardTimeout = TimeSpan.FromSeconds(5);
    private readonly INodeHashProvider<TNode, TContentKey> _nodeHashProvider;
    private readonly ConcurrentDictionary<TNode, bool> _isRefreshing = new ConcurrentDictionary<TNode, bool>();

    private readonly KBucket<TNode>[] _buckets;
    private readonly KBucketTree<TNode, TContentKey> _bucketTree;
    private readonly TNode _currentNodeId;
    private readonly ValueHash256 _currentNodeIdAsHash;
    private readonly int _kSize;
    private readonly int _alpha;
    private readonly IMessageSender<TNode, TContentKey, TContent> _messageSender;
    private readonly LruCache<TNode, int> _peerFailures;
    private readonly TimeSpan _refreshInterval;
    private readonly ILogger _logger;

    private bool _useTreeImplementation = true;

    public Kademlia(
        INodeHashProvider<TNode, TContentKey> nodeHashProvider,
        IKademlia<TNode, TContentKey, TContent>.IStore store,
        IMessageSender<TNode, TContentKey, TContent> sender,
        ILogManager logManager,
        TNode currentNodeId,
        int kSize,
        int alpha,
        TimeSpan refreshInterval
    )
    {
        _nodeHashProvider = nodeHashProvider;
        _store = store;
        _messageSender = new MessageSenderMonitor(sender, this);
        _logger = logManager.GetClassLogger<Kademlia<TNode, TContentKey, TContent>>();

        _currentNodeId = currentNodeId;
        _currentNodeIdAsHash = _nodeHashProvider.GetHash(_currentNodeId);
        _kSize = kSize;
        _alpha = alpha;
        _refreshInterval = refreshInterval;

        _peerFailures = new LruCache<TNode, int>(1024, "peer failure");
        _buckets = new KBucket<TNode>[Hash256XORUtils.MaxDistance + 1];
        for (int i = 0; i < Hash256XORUtils.MaxDistance + 1; i++)
        {
            _buckets[i] = new KBucket<TNode>(kSize);
        }
        _bucketTree = new KBucketTree<TNode, TContentKey>(kSize, 256, _nodeHashProvider);
    }

    public void UseTreeImplementation(bool useTree)
    {
        _useTreeImplementation = useTree;
        _logger.Info($"Switched to {(useTree ? "tree-based" : "array-based")} implementation");
        if (useTree)
        {
            // Initialize the tree-based implementation
            for (int i = 0; i < Hash256XORUtils.MaxDistance + 1; i++)
            {
                foreach (var node in _buckets[i].GetAll())
                {
                    _bucketTree.TryAddOrRefresh(node, out _);
                }
            }
        }
        else
        {
            // Initialize the array-based implementation
            for (int i = 0; i < Hash256XORUtils.MaxDistance + 1; i++)
            {
                foreach (var node in _bucketTree.GetAllAtDistance(i))
                {
                    _buckets[i].TryAddOrRefresh(node, out _);
                }
            }
        }
    }

    public void AddOrRefresh(TNode node)
    {
        if (SameAsSelf(node)) return;

        _isRefreshing.TryRemove(node, out _);

        if (_useTreeImplementation)
        {   
            _logger.Debug($"Adding/refreshing node {node} in tree-based implementation");
            if (!_bucketTree.TryAddOrRefresh(node, out TNode? toRefresh))
            {
                if (toRefresh != null) TryRefresh(toRefresh);
            }
        }
        else
        {
            var bucket = GetBucket(node);
            if (!bucket.TryAddOrRefresh(node, out TNode? toRefresh))
            {
                if (toRefresh != null) TryRefresh(toRefresh);
            }
        }
    }

    private void TryRefresh(TNode toRefresh)
    {
        if (_isRefreshing.TryAdd(toRefresh, true))
        {
            Task.Run(async () =>
            {
                using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                try
                {
                    await _messageSender.Ping(toRefresh, cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    if (_logger.IsDebug) _logger.Debug($"Error while refreshing node {toRefresh}, {e}");
                }

                if (_isRefreshing.TryRemove(toRefresh, out _))
                {
                    if (_useTreeImplementation)
                    {
                        _bucketTree.Remove(toRefresh);
                    }
                    else
                    {
                        GetBucket(toRefresh).RemoveAndReplace(toRefresh);
                    }
                }
            });
        }
    }
    
    public TNode[] GetAllAtDistance(int i)
    {
        return _useTreeImplementation ? _bucketTree.GetAllAtDistance(i) : _buckets[i].GetAll();
    }

    private bool SameAsSelf(TNode node)
    {
        return EqualityComparer<TNode>.Default.Equals(node, _currentNodeId);
    }


    public async Task<TContent?> LookupValue(TContentKey contentKey, CancellationToken token)
    {
        TContent? result = default(TContent);
        bool resultWasFound = false;

        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        // TODO: Timeout?

        ValueHash256 targetHash = _nodeHashProvider.GetHash(contentKey);

        try
        {
            await LookupNodesClosest(
                targetHash, async (nextNode, token) =>
                {
                    FindValueResponse<TNode, TContent> valueResponse = await _messageSender.FindValue(nextNode, contentKey, token);
                    if (valueResponse.hasValue)
                    {
                        _logger.Info($"Value response has value {valueResponse.value}");
                        resultWasFound = true;
                        result = valueResponse.value; // Shortcut so that once it find the value, it should stop.
                        await cts.CancelAsync();
                    }

                    _logger.Info($"Value response has no value. Returning {valueResponse.neighbours.Length} neighbours");
                    return valueResponse.neighbours;
                },
                token
            );
        }
        catch (OperationCanceledException)
        {
            if (!resultWasFound) throw;
        }

        return result;
    }

    private async Task<TNode[]> LookupNodesClosest(ValueHash256 targetHash, CancellationToken token)
    {
        return await LookupNodesClosest(
            targetHash,
            async (nextNode, token) => await _messageSender.FindNeighbours(nextNode, targetHash, token),
            token
        );
    }

    /// <summary>
    /// Main find closest-k node within the network. See the kademlia paper, 2.3.
    /// Since find value is basically the same also just with a shortcut, this allow changing the find neighbour op.
    /// Find closest-k is also used to determine which node should store a particular value which is used by
    /// store RPC (not implemented).
    /// </summary>
    /// <param name="targetHash"></param>
    /// <param name="findNeighbourOp"></param>
    /// <param name="token"></param>
    /// <returns></returns>
    private async Task<TNode[]> LookupNodesClosest(
        ValueHash256 targetHash,
        Func<TNode, CancellationToken, Task<TNode[]?>> findNeighbourOp,
        CancellationToken token
    ) {
        _logger.Info($"Initiate lookup for hash {targetHash}");

        Func<TNode, Task<(TNode target, TNode[]? retVal)>> wrappedFindNeighbourHop = async (node) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(FindNeighbourHardTimeout);

            try
            {
                // targetHash is implied in findNeighbourOp
                return (node, await findNeighbourOp(node, cts.Token));
            }
            catch (OperationCanceledException)
            {
                return (node, null);
            }
            catch (Exception e)
            {
                _logger.Error($"Find neighbour op failed. {e}");
                return (node, null);
            }
        };

        HashSet<TNode> queried = new HashSet<TNode>();
        HashSet<TNode> queriedAndResponded = new HashSet<TNode>();
        HashSet<TNode> seen = new HashSet<TNode>();

        IComparer<TNode> comparer = Comparer<TNode>.Create((h1, h2) =>
            Hash256XORUtils.Compare(_nodeHashProvider.GetHash(h1), _nodeHashProvider.GetHash(h2), targetHash));

        // Ordered by lowest distance. Will get popped for next round.
        PriorityQueue<TNode, TNode> bestSeen = new PriorityQueue<TNode, TNode>(comparer);

        // Ordered by lowest distance. Will not get popped for next round, but will at final collection.
        PriorityQueue<TNode, TNode> bestSeenAllTime = new PriorityQueue<TNode, TNode>(comparer);

        TNode closestNode = _currentNodeId;
        TNode[] roundQuery = IterateNeighbour(targetHash).Take(_alpha).ToArray();
        foreach (TNode hash in roundQuery)
        {
            seen.AddRange(hash);
            bestSeen.Enqueue(hash, hash);
            bestSeenAllTime.Enqueue(hash, hash);
        }

        while (roundQuery.Length > 0)
        {
            // TODO: The paper mentioned that the next round can start immediately while waiting
            // for the result of previous round.
            token.ThrowIfCancellationRequested();

            queried.AddRange(roundQuery);
            (TNode NodeId, TNode[]? Neighbours)[] currentRoundResponse = await Task.WhenAll(roundQuery.Select(wrappedFindNeighbourHop));

            bool hasCloserThanClosest = false;
            foreach ((TNode NodeId, TNode[]? Neighbours) response in currentRoundResponse)
            {
                if (response.Neighbours == null) continue; // Timeout or failed to get response
                _logger.Info($"Received {response.Neighbours.Length} from {response.NodeId}");

                queriedAndResponded.Add(response.NodeId);

                foreach (TNode neighbour in response.Neighbours)
                {
                    if (SameAsSelf(neighbour)) continue;

                    // Already queried, we ignore
                    if (queried.Contains(neighbour)) continue;

                    // When seen already dont record
                    if (!seen.Add(neighbour)) continue;
                    bestSeen.Enqueue(neighbour, neighbour);
                    bestSeenAllTime.Enqueue(neighbour, neighbour);

                    if (comparer.Compare(neighbour, closestNode) < 0)
                    {
                        hasCloserThanClosest = true;
                        closestNode = neighbour;
                    }
                }
            }

            if (!hasCloserThanClosest)
            {
                // end condition it seems
                break;
            }

            int toTake = Math.Min(_alpha, bestSeen.Count);
            roundQuery = Enumerable.Range(0, toTake).Select((_) => bestSeen.Dequeue()).ToArray();
        }

        _logger.Debug($"first phase done");

        // At this point need to query for the maxNode.
        List<TNode> result = [];
        while (result.Count < _kSize && bestSeenAllTime.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            TNode nextLowest = bestSeenAllTime.Dequeue();
            if (queriedAndResponded.Contains(nextLowest))
            {
                result.Add(nextLowest);
                continue;
            }

            if (queried.Contains(nextLowest))
            {
                // Queried but not responded
                continue;
            }

            // TODO: In parallel?
            // So the paper mentioned that node that it need to query findnode for node that was not queried.
            (_, TNode[]? nextCandidate) = await wrappedFindNeighbourHop(nextLowest);
            if (nextCandidate != null)
            {
                result.AddRange(nextLowest);
            }
        }

        return result.ToArray();
    }

    public async Task Run(CancellationToken token)
    {
        await LookupNodesClosest(_currentNodeIdAsHash, token);

        while (true)
        {
            await Bootstrap(token);
            


            await Task.Delay(_refreshInterval, token);
        }
    }

    public async Task Bootstrap(CancellationToken token)
    {
        Stopwatch sw = Stopwatch.StartNew();
        await LookupNodesClosest(_currentNodeIdAsHash, token);

        token.ThrowIfCancellationRequested();

        if (_useTreeImplementation)
        {
            for (var i = 0; i < Hash256XORUtils.MaxDistance + 1; i++)
            {
                token.ThrowIfCancellationRequested();

                if (_bucketTree.GetAllAtDistance(i).Length > 0)
                {
                    ValueHash256 nodeToLookup = Hash256XORUtils.GetRandomHashAtDistance(_currentNodeIdAsHash, i);
                    await LookupNodesClosest(nodeToLookup, token);
                }
            }
        }
        else
        {
            for (var i = 0; i < _buckets.Length; i++)
            {
                token.ThrowIfCancellationRequested();

                if (_buckets[i].Count > 0)
                {
                    ValueHash256 nodeToLookup = Hash256XORUtils.GetRandomHashAtDistance(_currentNodeIdAsHash, i);
                    await LookupNodesClosest(nodeToLookup, token);
                }
            }
        }

        _logger.Info($"Bootstrap completed. Took {sw}. Bucket sizes (from 230) {string.Join(",", Enumerable.Range(200, 56).Select(i => GetAllAtDistance(i).Length))}");
    }

    public IEnumerable<TNode> IterateNeighbour(ValueHash256 hash)
    {
        int startingDistance = Hash256XORUtils.CalculateDistance(_currentNodeIdAsHash, hash);
        foreach (var bucketToGet in EnumerateBucket(startingDistance))
        {
            foreach (TNode bucketContent in GetAllAtDistance(bucketToGet))
            {
                yield return bucketContent;
            }
        }
    }


    private IEnumerable<int> EnumerateBucket(int startingDistance)
    {
        // Note, without a tree based routing table, we don't exactly know
        // which way (left or right) is the right way to go. So this is all approximate.
        // Well, even with a full tree, it would still be approximate, just that it would
        // be a bit more accurate.
        yield return startingDistance;
        int left = startingDistance - 1;
        int right = startingDistance + 1;
        while (left > 0 || right <= Hash256XORUtils.MaxDistance)
        {
            if (left > 0)
            {
                yield return left;
            }

            if (right <= Hash256XORUtils.MaxDistance)
            {
                yield return right;
            }

            left -= 1;
            right += 1;
        }
    }

    private KBucket<TNode> GetBucket(TNode node)
    {
        int idx = Hash256XORUtils.CalculateDistance(_nodeHashProvider.GetHash(node), _currentNodeIdAsHash);
        return _buckets[idx];
    }

    private void OnIncomingMessageFrom(TNode sender)
    {
        AddOrRefresh(sender);
        _peerFailures.Delete(sender);
    }

    private void OnRequestFailed(TNode receiver)
    {
        if (!_peerFailures.TryGet(receiver, out var currentFailure))
        {
            _peerFailures.Set(receiver, 1);
            return;
        }

        if (currentFailure >= 5)
        {
            if (_useTreeImplementation)
            {
                _bucketTree.Remove(receiver);
            }
            else
            {
                GetBucket(receiver).Remove(receiver);
            }
            _peerFailures.Delete(receiver);
        }

        _peerFailures.Set(receiver, currentFailure + 1);
    }

    public Task Ping(TNode sender, CancellationToken token)
    {
        OnIncomingMessageFrom(sender);
        return Task.CompletedTask;
    }

    public Task<TNode[]> FindNeighbours(TNode sender, ValueHash256 hash, CancellationToken token)
    {
        OnIncomingMessageFrom(sender);
        return Task.FromResult(IterateNeighbour(hash).Take(_kSize).ToArray());
    }

    public Task<FindValueResponse<TNode, TContent>> FindValue(TNode sender, TContentKey contentKey, CancellationToken token)
    {
        OnIncomingMessageFrom(sender);

        if (_store.TryGetValue(contentKey, out TContent? value))
        {
            return Task.FromResult(new FindValueResponse<TNode, TContent>(true, value!, Array.Empty<TNode>()));
        }

        return Task.FromResult(
            new FindValueResponse<TNode, TContent>(
                false,
                default,
                IterateNeighbour(_nodeHashProvider.GetHash(contentKey)).Take(_kSize).ToArray()
            ));
    }

    /// <summary>
    /// Monitor requests for success or failure.
    /// </summary>
    /// <param name="implementation"></param>
    /// <param name="kademlia"></param>
    private class MessageSenderMonitor(IMessageSender<TNode, TContentKey, TContent> implementation, Kademlia<TNode, TContentKey, TContent> kademlia) : IMessageSender<TNode, TContentKey, TContent>
    {
        public async Task Ping(TNode receiver, CancellationToken token)
        {
            try
            {
                await implementation.Ping(receiver, token);
                kademlia.OnIncomingMessageFrom(receiver);
            }
            catch (OperationCanceledException)
            {
                kademlia.OnRequestFailed(receiver);
                throw;
            }
        }

        public async Task<TNode[]> FindNeighbours(TNode receiver, ValueHash256 hash, CancellationToken token)
        {
            try
            {
                TNode[] res = await implementation.FindNeighbours(receiver, hash, token);
                kademlia.OnIncomingMessageFrom(receiver);
                return res;
            }
            catch (OperationCanceledException)
            {
                kademlia.OnRequestFailed(receiver);
                throw;
            }
        }

        public Task<FindValueResponse<TNode, TContent>> FindValue(TNode receiver, TContentKey contentKey, CancellationToken token)
        {
            try
            {
                Task<FindValueResponse<TNode, TContent>> res = implementation.FindValue(receiver, contentKey, token);
                kademlia.OnIncomingMessageFrom(receiver);
                return res;
            }
            catch (OperationCanceledException)
            {
                kademlia.OnRequestFailed(receiver);
                throw;
            }
        }
    }
}
