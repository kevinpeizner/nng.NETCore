using nng.Native;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace nng.Tests
{
    using static nng.Tests.Util;

    [Collection("nng")]
    public class PushPullTests
    {
        NngCollectionFixture Fixture;
        IAPIFactory<INngMsg> Factory => Fixture.Factory;

        public PushPullTests(NngCollectionFixture collectionFixture)
        {
            Fixture = collectionFixture;
        }

        [Theory]
        [ClassData(typeof(TransportsClassData))]
        public async Task Basic(string url)
        {
            Fixture.TestIterate(() =>
            {
                using (var push = Factory.PusherOpen().Unwrap())
                using (var pull = Factory.PullerOpen().Unwrap())
                {
                    var listener = push.ListenWithListener(url).Unwrap();
                    pull.Dial(GetDialUrl(listener, url)).Unwrap();
                }

                // Manually create listener/dialer
                using (var push = Factory.PusherOpen().Unwrap())
                using (var listener0 = push.ListenerCreate(url).Unwrap())
                {
                    // Must start listener before using `NNG_OPT_LOCADDR`
                    listener0.Start();
                    using (var pull = Factory.PullerOpen().Unwrap())
                    using (var dialer1 = pull.DialerCreate(GetDialUrl(listener0, url)).Unwrap())
                    {

                    }
                }
            });
        }

        [Theory]
        [ClassData(typeof(TransportsNoWsClassData))]
        public Task PushPull(string url)
        {
            return Fixture.TestIterate(() => DoPushPull(url));
        }

        Task DoPushPull(string url)
        {
            var serverReady = new AsyncBarrier(2);
            var clientReady = new AsyncBarrier(2);
            var cts = new CancellationTokenSource();
            var dialUrl = string.Empty;
            var push = Task.Run(async () =>
            {
                using (var socket = Factory.PusherOpen().ThenListenAs(out var listener, url).Unwrap())
                using (var ctx = socket.CreateAsyncContext(Factory).Unwrap())
                {
                    dialUrl = GetDialUrl(listener, url);
                    await serverReady.SignalAndWait();
                    await clientReady.SignalAndWait();
                    (await ctx.Send(Factory.CreateMessage())).Unwrap();
                }
            });
            var pull = Task.Run(async () =>
            {
                await serverReady.SignalAndWait();
                using (var socket = Factory.PullerOpen().ThenDial(dialUrl).Unwrap())
                using (var ctx = socket.CreateAsyncContext(Factory).Unwrap())
                {
                    var task = ctx.Receive(cts.Token);
                    await WaitShort();
                    await clientReady.SignalAndWait();
                    var _ = await task;
                }
            });
            return CancelAfterAssertwait(cts, pull, push);
        }

        [Fact]
        public async Task Broker()
        {
            await PushPullBrokerAsync(2, 3, 2);
        }

        async Task PushPullBrokerAsync(int numPushers, int numPullers, int numMessagesPerPusher, int msTimeout = DefaultTimeoutMs)
        {
            // In pull/push (pipeline) pattern, each message is sent to one receiver in round-robin fashion
            int numTotalMessages = numPushers * numMessagesPerPusher;
            var counter = new AsyncCountdownEvent(numTotalMessages);
            var cts = new CancellationTokenSource();
            using (var broker = new Broker(new PushPullBrokerImpl(Factory)))
            {
                var tasks = await broker.RunAsync(numPushers, numPullers, numMessagesPerPusher, counter, cts.Token);
                tasks.Add(counter.WaitAsync());
                await CancelAfterAssertwait(tasks, cts);
            }
        }
    }

    class PushPullBrokerImpl : IBrokerImpl<INngMsg>
    {
        public IAPIFactory<INngMsg> Factory { get; private set; }

        public PushPullBrokerImpl(IAPIFactory<INngMsg> factory)
        {
            Factory = factory;
        }

        public IReceiveAsyncContext<INngMsg> CreateInSocket(string url)
        {
            var socket = Factory.PullerOpen().Unwrap();
            socket.Listen(url).Unwrap();
            var ctx = socket.CreateAsyncContext(Factory).Unwrap();
            disposable.Add(socket);
            disposable.Add(ctx);
            return ctx;
        }
        public ISendAsyncContext<INngMsg> CreateOutSocket(string url)
        {
            var socket = Factory.PusherOpen().Unwrap();
            socket.Listen(url).Unwrap();
            var ctx = socket.CreateAsyncContext(Factory).Unwrap();
            disposable.Add(socket);
            disposable.Add(ctx);
            return ctx;
        }
        public IReceiveAsyncContext<INngMsg> CreateClient(string url)
        {
            var socket = Factory.PullerOpen().Unwrap();
            socket.Dial(url).Unwrap();
            var ctx = socket.CreateAsyncContext(Factory).Unwrap();
            disposable.Add(socket);
            disposable.Add(ctx);
            return ctx;
        }

        public INngMsg CreateMessage()
        {
            return Factory.CreateMessage();
        }

        public void Dispose()
        {
            foreach (var obj in disposable)
            {
                obj.Dispose();
            }
        }

        ConcurrentBag<IDisposable> disposable = new ConcurrentBag<IDisposable>();
    }
}
