// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Discovery.Portal.Messages;

public class Nodes
{
    public byte Total { get; set; } = 1;
    public byte[][] Enrs { get; set; } = null!;
}
