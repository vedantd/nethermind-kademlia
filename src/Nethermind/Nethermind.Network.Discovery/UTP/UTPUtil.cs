// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nethermind.Network.Discovery;

public class UTPUtil
{
    public static ushort WrappedAddOne(ushort num)
    {
        // Why do I think there is  a better way of doing this?
        return (ushort)(num + 1);
    }

    public static bool IsLessOrEqual(ushort num1, ushort num2)
    {
        return IsLess(num1, WrappedAddOne(num2));
    }

    public static bool IsLess(ushort num1, ushort num2)
    {
        // Why do I think there is  a better way of doing this?
        return (num1 + 32768) % 65536 < (num2 + 32768) % 65536;
    }

    public static uint GetTimestamp()
    {
        long ticks = Stopwatch.GetTimestamp();
        long microseconds = (ticks * 1_000_000) / Stopwatch.Frequency;
        return (uint)microseconds;
    }

    public static byte[] CompileSelectiveAckBitset(ushort curAck, ConcurrentDictionary<ushort, Memory<byte>?> receiveBuffer)
    {
        byte[] selectiveAck;
        // Fixed 64 bit.
        // TODO: use long
        // TODO: no need to encode trailing zeros
        selectiveAck = new byte[8];

        // Shortcut the loop if all buffer was iterated
        int counted = 0;
        int maxCounted = receiveBuffer.Count;

        for (int i = 0; i < 64 && counted < maxCounted; i++)
        {
            ushort theAck = (ushort)(curAck + 2 + i);
            if (receiveBuffer.ContainsKey(theAck))
            {
                int iIdx = i / 8;
                int iOffset = i % 8;
                selectiveAck[iIdx] = (byte)(selectiveAck[iIdx] | 1 << iOffset);
                counted++;
            }
        }
        return selectiveAck;
    }

}
