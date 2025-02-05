// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Kademlia;

public class KBucket<TNode>(int k) where TNode : notnull
{
    private DoubleEndedLru<TNode> _items = new(k);
    private DoubleEndedLru<TNode> _replacement = new(k); // Well, the replacement does not have to be k. Could be much lower.

    public int Count => _items.Count;

    /// <summary>
    /// Add or refresh a node entry.
    /// Used when any traffic is received, or when seeding a node.
    /// Return the last entry in a bucket to refresh when bucket is full.
    /// </summary>
    /// <param name="item"></param>
    /// <returns></returns>
    public bool TryAddOrRefresh(TNode item, out TNode? toRefresh)
    {
        if (_items.AddOrRefresh(item))
        {
            toRefresh = default;
            return true;
        }

        _replacement.AddOrRefresh(item);
        _items.TryGetLast(out toRefresh);
        return false;
    }

    public TNode[] GetAll()
    {
        // TODO: Seems like a good candidate to cache
        return _items.GetAll();
    }

    public void RemoveAndReplace(TNode node)
    {
        if (_items.Remove(node))
        {
            if (_replacement.TryPopHead(out TNode? replacement))
            {
                _items.AddOrRefresh(replacement!);
            }
        }
    }

    public void Remove(TNode node)
    {
        _items.Remove(node);
        _replacement.Remove(node);
    }
}
