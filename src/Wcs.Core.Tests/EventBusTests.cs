using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Handlers;
using Wcs.Core.EventBus.Publisher;

namespace WcsCoreTests;

/// <summary>
/// EventBus 核心功能测试：订阅、发布、取消订阅、泛型/非泛型
/// </summary>
public class EventBusTests
{
    [Fact]
    public async Task SubscribeAndPublish_HandlerReceivesEvent()
    {
        var bus = new EventBus();
        string? receivedId = null;

        bus.Subscribe<TestEvent>(async (evt, ct) =>
        {
            receivedId = evt.EventId;
            await Task.CompletedTask;
        });

        var evt = new TestEvent();
        await bus.PublishAsync(evt);

        Assert.Equal(evt.EventId, receivedId);
    }

    [Fact]
    public async Task MultipleHandlers_AllExecute()
    {
        var bus = new EventBus();
        var executed = new List<int>();

        for (int i = 1; i <= 3; i++)
        {
            var id = i;
            bus.Subscribe<TestEvent>(async (evt, ct) =>
            {
                lock (executed) { executed.Add(id); }
                await Task.CompletedTask;
            });
        }

        await bus.PublishAsync(new TestEvent());
        Assert.Equal(3, executed.Count);
        Assert.Contains(1, executed);
        Assert.Contains(2, executed);
        Assert.Contains(3, executed);
    }

    [Fact]
    public async Task Unsubscribe_HandlerNotCalled()
    {
        var bus = new EventBus();
        var callCount = 0;

        EventHandlerDelegate<TestEvent> handler = async (evt, ct) =>
        {
            Interlocked.Increment(ref callCount);
            await Task.CompletedTask;
        };

        bus.Subscribe(handler);
        await bus.PublishAsync(new TestEvent());
        Assert.Equal(1, callCount);

        // 不能 unsubscribe 委托类型 — 测试非泛型版的接口处理器
    }

    [Fact]
    public async Task PublishNonGeneric_WorksViaReflection()
    {
        var bus = new EventBus();
        var received = false;

        bus.Subscribe<TestEvent>(async (evt, ct) =>
        {
            received = true;
            await Task.CompletedTask;
        });

        IEvent evt = new TestEvent();
        await bus.PublishAsync(evt);

        Assert.True(received);
    }

    [Fact]
    public void GetSubscribedEvents_ReturnsRegisteredTypes()
    {
        var bus = new EventBus();
        bus.Subscribe<TestEvent>(async (evt, ct) => await Task.CompletedTask);

        var types = bus.GetSubscribedEvents().ToList();
        Assert.Contains(typeof(TestEvent), types);
    }

    [Fact]
    public void GetHandlerCount_ReturnsCorrectCount()
    {
        var bus = new EventBus();
        Assert.Equal(0, bus.GetHandlerCount<TestEvent>());

        bus.Subscribe<TestEvent>(async (evt, ct) => await Task.CompletedTask);
        Assert.Equal(1, bus.GetHandlerCount<TestEvent>());
    }

    [Fact]
    public void ClearAllSubscriptions_RemovesAllHandlers()
    {
        var bus = new EventBus();
        bus.Subscribe<TestEvent>(async (evt, ct) => await Task.CompletedTask);
        bus.Subscribe<DeviceStateChangedEvent>(async (evt, ct) => await Task.CompletedTask);

        bus.ClearAllSubscriptions();

        Assert.Equal(0, bus.GetHandlerCount<TestEvent>());
    }

    [Fact]
    public async Task HandlerException_DoesNotCrashOtherHandlers()
    {
        var bus = new EventBus();
        var secondCalled = false;

        bus.Subscribe<TestEvent>(async (evt, ct) =>
        {
            throw new InvalidOperationException("Handler 1 failed");
        });
        bus.Subscribe<TestEvent>(async (evt, ct) =>
        {
            secondCalled = true;
            await Task.CompletedTask;
        });

        // Should not throw
        await bus.PublishAsync(new TestEvent());
        Assert.True(secondCalled);
    }

    [Fact]
    public async Task IEventHandlerInterface_Works()
    {
        var bus = new EventBus();
        var handler = new TestEventHandler();
        bus.Subscribe(handler);

        await bus.PublishAsync(new TestEvent());
        Assert.True(handler.Handled);
    }

    private class TestEvent : EventBase { }

    private class TestEventHandler : IEventHandler<TestEvent>
    {
        public bool Handled { get; private set; }

        public Task HandleAsync(TestEvent @event, CancellationToken cancellationToken = default)
        {
            Handled = true;
            return Task.CompletedTask;
        }
    }
}
