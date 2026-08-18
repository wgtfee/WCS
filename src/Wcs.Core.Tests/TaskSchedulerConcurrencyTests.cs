using Wcs.Core.TaskEngine.Context;
using Scheduler = Wcs.Core.TaskEngine.Scheduler.TaskScheduler;

namespace WcsCoreTests;

public class TaskSchedulerConcurrencyTests
{
    [Fact]
    public async Task DefaultDeviceLimit_AllowsOnlyOneRunningTaskPerDevice()
    {
        var scheduler = new Scheduler();
        await scheduler.EnqueueAsync(Task("A-1", "CV-01", priority: 4));
        await scheduler.EnqueueAsync(Task("A-2", "CV-01", priority: 2));

        var first = await scheduler.DequeueAsync();
        var blocked = await scheduler.DequeueAsync();

        Assert.NotNull(first);
        Assert.Equal("A-1", first!.TaskId);
        Assert.Null(blocked);
        Assert.Equal(1, scheduler.GetDeviceTaskCount("CV-01"));

        scheduler.ReleaseDeviceSlot("CV-01");
        var second = await scheduler.DequeueAsync();

        Assert.NotNull(second);
        Assert.Equal("A-2", second!.TaskId);
    }

    [Fact]
    public async Task BusyHighPriorityDevice_DoesNotBlockIdleDevice()
    {
        var scheduler = new Scheduler();
        await scheduler.EnqueueAsync(Task("A-running", "CV-01", priority: 4));
        var running = await scheduler.DequeueAsync();
        Assert.NotNull(running);

        await scheduler.EnqueueAsync(Task("A-blocked", "CV-01", priority: 4));
        await scheduler.EnqueueAsync(Task("B-ready", "CV-02", priority: 1));

        var next = await scheduler.DequeueAsync();

        Assert.NotNull(next);
        Assert.Equal("B-ready", next!.TaskId);
        Assert.Equal("CV-02", next.DeviceId);
        Assert.Equal(1, scheduler.GetQueueCount());
    }

    private static TaskContext Task(string taskId, string deviceId, int priority = 2)
        => new()
        {
            TaskId = taskId,
            DeviceId = deviceId,
            RouteId = $"{deviceId}→ASRS01",
            Priority = priority,
            PriorityLevel = priority >= 4 ? TaskPriority.Emergency : TaskPriority.Normal
        };
}
