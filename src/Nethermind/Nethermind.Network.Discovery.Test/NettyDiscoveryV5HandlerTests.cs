// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Embedded;
using DotNetty.Transport.Channels.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Nethermind.Serialization.Rlp;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NettyDiscoveryV5HandlerTests
    {
        private EmbeddedChannel _channel;
        private NettyDiscoveryV5Handler _handler;

        [SetUp]
        public void Initialize()
        {
            _channel = new();
            _handler = new(new LoggerFactory());
            _handler.InitializeChannel(_channel);
        }

        [TearDown]
        public async Task CleanUp()
        {
            await _channel.CloseAsync();
        }

        [Test]
        public async Task ForwardsSentMessageToChannel()
        {
            byte[] data = [1, 2, 3];
            var to = IPEndPoint.Parse("127.0.0.1:10001");

            await _handler.SendAsync(data, to);

            DatagramPacket packet = _channel.ReadOutbound<DatagramPacket>();
            packet.Should().NotBeNull();
            packet.Content.ReadAllBytesAsArray().Should().BeEquivalentTo(data);
            packet.Recipient.Should().Be(to);
        }

        [Test]
        public async Task ForwardsReceivedMessageToReader()
        {
            byte[] data = [1, 2, 3];
            var from = IPEndPoint.Parse("127.0.0.1:10000");
            var to = IPEndPoint.Parse("127.0.0.1:10001");

            IAsyncEnumerator<UdpReceiveResult> enumerator = _handler.ReadMessagesAsync().GetAsyncEnumerator();

            var ctx = Substitute.For<IChannelHandlerContext>();
            var packet = new DatagramPacket(Unpooled.WrappedBuffer(data), from, to);

            _handler.ChannelRead(ctx, packet);

            using var cancellationSource = new CancellationTokenSource(10_000);
            (await enumerator.MoveNextAsync(cancellationSource.Token)).Should().BeTrue();
            UdpReceiveResult forwardedPacket = enumerator.Current;

            forwardedPacket.Should().NotBeNull();
            forwardedPacket.Buffer.Should().BeEquivalentTo(data);
            forwardedPacket.RemoteEndPoint.Should().Be(from);
        }
    }
}
