// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Network.Discovery.Portal.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Discovery.Portal.History;

// Does encode/decode of payload along with verification
// TODO: Do verification here also.
public class HistoryNetworkEncoderDecoder
{
    private HeaderDecoder _headerDecoder = new HeaderDecoder();
    private TxDecoder<Transaction> _txDecoder = new TxDecoder();

    public BlockHeader DecodeHeader(byte[] payload)
    {
        byte[] headerBytes = SlowSSZ.Deserialize<PortalBlockHeaderWithProof>(payload).Header!;
        BlockHeader header = _headerDecoder.Decode(new RlpStream(headerBytes))!;
        return header;
    }

    public BlockBody DecodeBody(byte[] payload)
    {
        // TODO: Need to know if post or pre shanghai.
        // And for that need to get the header first.
        PortalBlockBodyPostShanghai body = SlowSSZ.Deserialize<PortalBlockBodyPostShanghai>(payload);
        byte[][] transactionBytes = body.Transactions;
        Transaction[] transactions = transactionBytes.Select((bytes) => _txDecoder.Decode(new RlpStream(bytes))!).ToArray();
        // Does not work. Dont know why.
        // BlockHeader[] uncles = Rlp.Decode<BlockHeader[]>(body.Uncles!);

        // TODO: Widthrawals
        return new BlockBody(transactions, Array.Empty<BlockHeader>());
    }

    public byte[]? EncodeHeader(BlockHeader header)
    {
        byte[]? headerBytes = _headerDecoder.Encode(header).Bytes;

        // TODO: Proof
        return SlowSSZ.Serialize(new PortalBlockHeaderWithProof()
        {
            Header = headerBytes
        });
    }

    public byte[]? EncodeBlockBody(BlockBody blockBody)
    {
        byte[][] transactionBytes = blockBody.Transactions.Select((tx) => _txDecoder.Encode(tx).Bytes).ToArray();

        // TODO: Uncles widthrawals
        return SlowSSZ.Serialize(new PortalBlockBodyPostShanghai()
        {
            Transactions = transactionBytes
        });
    }

    /*
    public ContentContent Decode(ContentKey contentKey, byte[] payload)
    {
        ContentContent decodedContent = new ContentContent();
        if (contentKey.HeaderKey != null)
        {
            byte[] headerBytes = SlowSSZ.Deserialize<PortalBlockHeaderWithProof>(payload).Header!;
            BlockHeader header = _headerDecoder.Decode(new RlpStream(headerBytes))!;
            decodedContent.Header = header;
        }
        else if (contentKey.BodyKey != null)
        {
            // TODO: Need to know if post or pre shanghai.
            // And for that need to get the header first.
            PortalBlockBodyPostShanghai body = SlowSSZ.Deserialize<PortalBlockBodyPostShanghai>(payload);
            byte[][] transactionBytes = body.Transactions;
            Transaction[] transactions = transactionBytes.Select((bytes) => _txDecoder.Decode(new RlpStream(bytes))!).ToArray();
            BlockHeader[] uncles = Rlp.Decode<BlockHeader[]>(body.Uncles!);

            // TODO: Widthrawals
            decodedContent.Body = new BlockBody(transactions, uncles);
        }
        else if (contentKey.ReceiptKey != null)
        {
            throw new NotImplementedException("receipt decoding not implemented");
        }
        else
        {
            throw new InvalidOperationException("Unknown decoding");
        }

        return decodedContent;
    }

    public byte[] Encode(ContentContent value)
    {
        throw new NotImplementedException();
    }
    */
}
