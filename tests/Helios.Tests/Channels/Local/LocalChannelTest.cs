﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Helios.Buffers;
using Helios.Channels;
using Helios.Channels.Local;
using Helios.Channels.Bootstrap;
using Helios.Logging;
using Xunit;

namespace Helios.Tests.Channels.Local
{
    public class LocalChannelTest : IDisposable
    {
        private static readonly ILogger Logger = LoggingFactory.GetLogger<LocalChannelTest>();
        private static readonly LocalAddress TEST_ADDRESS = new LocalAddress("test.id");

        private IEventLoopGroup _group1;
        private IEventLoopGroup _group2;
        private IEventLoopGroup _sharedGroup;

        public LocalChannelTest()
        {
            _group1 = new MultithreadEventLoopGroup(2);
            _group2 = new MultithreadEventLoopGroup(2);
            _sharedGroup = new MultithreadEventLoopGroup(1);
        }

        [Fact]
        public void LocalChannel_should_reuse_LocalAddress()
        {
            for (var i = 0; i < 2; i++)
            {
                var cb = new ClientBootstrap();
                var sb = new ServerBootstrap();

                cb.Group(_group1).Channel<LocalChannel>().Handler(new TestHandler());

                sb.Group(_group2).Channel<LocalServerChannel>().ChildHandler(new ActionChannelInitializer<LocalChannel>(
                    channel =>
                    {
                        channel.Pipeline.AddLast(new TestHandler());
                    }));

                IChannel sc = null;
                IChannel cc = null;

                try
                {
                    // Start server
                    sc = sb.BindAsync(TEST_ADDRESS).Result;
                    var latch = new CountdownEvent(1);

                    // Connect to the server
                    cc = cb.ConnectAsync(sc.LocalAddress).Result;
                    var cCpy = cc;
                    cc.EventLoop.Execute(o =>
                    {
                        var c = (LocalChannel) o;
                        c.Pipeline.FireChannelRead("Hello, World");
                        latch.Signal();
                    }, cCpy);

                    latch.Wait(TimeSpan.FromSeconds(5));
                    Assert.True(latch.IsSet);

                    CloseChannel(cc);
                    CloseChannel(sc);
                    sc.CloseCompletion.Wait();

                    Assert.Null(LocalChannelRegistry.Get(TEST_ADDRESS));
                }
                finally
                {
                    CloseChannel(cc);
                    CloseChannel(sc);
                }
            }
        }

        [Fact]
        public void LocalChannel_WriteAsync_should_fail_fast_on_closed_channel()
        {
            var cb = new ClientBootstrap();
            var sb = new ServerBootstrap();

            cb.Group(_group1).Channel<LocalChannel>().Handler(new TestHandler());

            sb.Group(_group2).Channel<LocalServerChannel>().ChildHandler(new ActionChannelInitializer<LocalChannel>(
                channel =>
                {
                    channel.Pipeline.AddLast(new TestHandler());
                }));

            IChannel sc = null;
            IChannel cc = null;

            try
            {
                // Start server
                sc = sb.BindAsync(TEST_ADDRESS).Result;
                var latch = new CountdownEvent(1);

                // Connect to the server
                cc = cb.ConnectAsync(sc.LocalAddress).Result;

                // Close the channel and write something
                cc.CloseAsync().Wait();
                var ag = Assert.Throws<AggregateException>(() =>
                {
                    cc.WriteAndFlushAsync(new object()).Wait();
                }).Flatten();
                Assert.IsType<ClosedChannelException>(ag.InnerException);
            }
            finally
            {
                CloseChannel(cc);
                CloseChannel(sc);
            }
        }

        private class ReadHandler1 : ChannelHandlerAdapter
        {
            private CountdownEvent _event;

            public ReadHandler1(CountdownEvent @event)
            {
                _event = @event;
            }

            public override void ChannelRead(IChannelHandlerContext context, object message)
            {
                _event.Signal();
                context.CloseAsync();
            }
        }

        private class ReadHandler2 : ChannelHandlerAdapter
        {
            public override void ChannelRead(IChannelHandlerContext context, object message)
            {
                //discard
            }
        }

        [Fact]
        public void LocalServerChannel_should_be_able_to_close_channel_on_same_EventLoop()
        {
            var latch = new CountdownEvent(1);
            var sb = new ServerBootstrap();

            sb.Group(_group2).Channel<LocalServerChannel>().ChildHandler(new ReadHandler1(latch));

            IChannel sc = null;
            IChannel cc = null;

            try
            {
                sc = sb.BindAsync(TEST_ADDRESS).Result;

                var b = new ClientBootstrap()
                    .Group(_group2)
                    .Channel<LocalChannel>().Handler(new ReadHandler2());
                cc = b.ConnectAsync(sc.LocalAddress).Result;
                cc.WriteAndFlushAsync(new object());
                latch.Wait(TimeSpan.FromSeconds(5));
                Assert.True(latch.IsSet);
            }
            finally
            {
                CloseChannel(cc);
                CloseChannel(sc);
            }
        }

        private class ReadCountdown1 : ChannelHandlerAdapter
        {
            private readonly CountdownEvent _latch;
            private readonly IByteBuf _expectedData;

            public ReadCountdown1(CountdownEvent latch, IByteBuf expectedData)
            {
                _latch = latch;
                _expectedData = expectedData;
            }

            public override void ChannelRead(IChannelHandlerContext context, object message)
            {
                if (AbstractByteBuf.ByteBufComparer.Equals(_expectedData, message as IByteBuf))
                {
                    // todo: reference counting
                    _latch.Signal();
                }
                else
                {
                    base.ChannelRead(context, message);
                }
            }

            public override void ChannelInactive(IChannelHandlerContext context)
            {
                _latch.Signal();
                base.ChannelInactive(context);
            }
        }


        [Fact]
        public void LocalChannel_close_when_WritePromiseComplete_should_still_preserve_order()
        {
            var cb = new ClientBootstrap();
            var sb = new ServerBootstrap();
            var messageLatch = new CountdownEvent(2);
            var data = Unpooled.WrappedBuffer(new byte[1024]);

            try
            {
                cb.Group(_group1)
                    .Channel<LocalChannel>()
                    .Handler(new TestHandler());

                sb.Group(_group2)
                    .Channel<LocalServerChannel>()
                    .ChildHandler(new ReadCountdown1(messageLatch, data));

                IChannel sc = null;
                IChannel cc = null;

                try
                {
                    // Start server
                    sc = sb.BindAsync(TEST_ADDRESS).Result;

                    // Connect to server
                    cc = cb.ConnectAsync(sc.LocalAddress).Result;

                    var ccCpy = cc;

                    // Make sure a write operation is executed in the eventloop
                    cc.Pipeline.LastContext().Executor.Execute(() =>
                    {
                        // todo: reference counting
                        ccCpy.WriteAndFlushAsync(data.Duplicate()).ContinueWith(tr =>
                        {
                            ccCpy.Pipeline.LastContext().CloseAsync();
                        });
                    });

                    messageLatch.Wait(TimeSpan.FromSeconds(5));
                    Assert.True(messageLatch.IsSet);
                    Assert.False(cc.IsOpen);
                }
                finally
                {
                    CloseChannel(sc);
                    CloseChannel(cc);
                }
            }
            finally
            {
                // todo: referencing counting
            }
        }

        private class ReadCountdown2 : ChannelHandlerAdapter
        {
            private readonly CountdownEvent _latch;
            private readonly IByteBuf _expectedData1;
            private readonly IByteBuf _expectedData2;

            public ReadCountdown2(CountdownEvent latch, IByteBuf expectedData1, IByteBuf expectedData2)
            {
                _latch = latch;
                _expectedData1 = expectedData1;
                _expectedData2 = expectedData2;
            }

            public override void ChannelRead(IChannelHandlerContext context, object message)
            {
                var count = _latch.CurrentCount;
                if ((AbstractByteBuf.ByteBufComparer.Equals(_expectedData1, message as IByteBuf) && count == 2)
                    || (AbstractByteBuf.ByteBufComparer.Equals(_expectedData2, message as IByteBuf) && count == 1))
                {
                    // todo: reference counting
                    _latch.Signal();
                }
                else
                {
                    base.ChannelRead(context, message);
                }
            }
        }

        [Fact]
        public void LocalChannel_write_when_WritePromiseComplete_should_still_preserve_order()
        {
            var cb = new ClientBootstrap();
            var sb = new ServerBootstrap();
            var messageLatch = new CountdownEvent(2);
            var data1 = Unpooled.WrappedBuffer(new byte[1024]);
            var data2 = Unpooled.WrappedBuffer(new byte[512]);

            try
            {
                cb.Group(_group1)
                    .Channel<LocalChannel>()
                    .Handler(new TestHandler());

                sb.Group(_group2)
                    .Channel<LocalServerChannel>()
                    .ChildHandler(new ReadCountdown2(messageLatch, data1, data2));

                IChannel sc = null;
                IChannel cc = null;

                try
                {
                    // Start server
                    sc = sb.BindAsync(TEST_ADDRESS).Result;

                    // Connect to server
                    cc = cb.ConnectAsync(sc.LocalAddress).Result;

                    var ccCpy = cc;

                    // Make sure a write operation is executed in the eventloop
                    cc.Pipeline.LastContext().Executor.Execute(() =>
                    {
                        // todo: reference counting
                        ccCpy.WriteAndFlushAsync(data1.Duplicate()).ContinueWith(tr =>
                        {
                            // todo: reference counting
                            ccCpy.WriteAndFlushAsync(data2.Duplicate());
                        });
                    });

                    messageLatch.Wait(TimeSpan.FromSeconds(5));
                    Assert.True(messageLatch.IsSet);
                }
                finally
                {
                    CloseChannel(sc);
                    CloseChannel(cc);
                }
            }
            finally
            {
                // todo: referencing counting
            }
        }

        private static void CloseChannel(IChannel cc)
        {
            cc?.CloseAsync().Wait();
        }

        private class TestHandler : ChannelHandlerAdapter
        {
            public override void ChannelRead(IChannelHandlerContext context, object message)
            {
                Logger.Info("Received message: {0}", message);
                // todo: reference counting
            }
        }

        public void Dispose()
        {
            var t1 = _group1.ShutdownGracefullyAsync();
            var t2 = _group2.ShutdownGracefullyAsync();
            var t3 = _sharedGroup.ShutdownGracefullyAsync();
            Task.WaitAll(t1, t2, t3);
        }
    }
}
