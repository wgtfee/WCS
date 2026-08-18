using RuntimeStateCenter = Wcs.Core.StateCenter.Implementation.StateCenter;
using Wcs.Core.StateCenter.Models;
using Wcs.Host.Mcp;

namespace Wcs.Core.Tests.Mcp;

public sealed class WcsReadOnlyMcpToolsTests
{
    [Fact]
    public void GetDeviceState_ReturnsMinimalCurrentState()
    {
        var stateCenter = new RuntimeStateCenter();
        stateCenter.UpdateDeviceState("D01", new DeviceState
        {
            DeviceId = "D01",
            Status = DeviceStatusEnum.Running,
            CurrentPosition = "P10",
            LastUpdateTime = new DateTime(2026, 8, 14, 1, 2, 3, DateTimeKind.Utc),
            Properties = new Dictionary<string, object> { ["raw-plc-secret"] = 123 }
        });

        var result = new WcsReadOnlyMcpTools().GetDeviceState(stateCenter, "D01");

        Assert.True(result.Found);
        Assert.NotNull(result.Device);
        Assert.Equal("D01", result.Device!.DeviceId);
        Assert.Equal("Running", result.Device.Status);
        Assert.Equal("P10", result.Device.CurrentPosition);
    }

    [Fact]
    public void GetActiveTasks_RespectsLimitAndOmitsParameters()
    {
        var stateCenter = new RuntimeStateCenter();
        stateCenter.UpdateTaskRuntime("T01", new TaskRuntime
        {
            TaskId = "T01",
            Status = TaskStatusEnum.Running,
            Priority = 10,
            RouteId = "R01",
            CreatedTime = DateTime.UtcNow,
            Parameters = new Dictionary<string, object> { ["internal"] = "not-exposed" }
        });
        stateCenter.UpdateTaskRuntime("T02", new TaskRuntime
        {
            TaskId = "T02",
            Status = TaskStatusEnum.Queued,
            Priority = 5,
            RouteId = "R02",
            CreatedTime = DateTime.UtcNow
        });

        var result = new WcsReadOnlyMcpTools().GetActiveTasks(stateCenter, 1);

        Assert.Equal(2, result.TotalCount);
        Assert.Single(result.Tasks);
        Assert.Contains(result.Tasks[0].TaskId, new[] { "T01", "T02" });
    }

    [Fact]
    public void GetActiveAlarms_ReturnsReadOnlyAlarmView()
    {
        var stateCenter = new RuntimeStateCenter();
        stateCenter.UpdateAlarmState("A01", new AlarmState
        {
            AlarmId = "A01",
            AlarmCode = "E102",
            Status = AlarmStatusEnum.Active,
            Level = AlarmLevelEnum.Warning,
            Message = "Test alarm",
            OccurTime = DateTime.UtcNow
        });

        var result = new WcsReadOnlyMcpTools().GetActiveAlarms(stateCenter, 10);

        Assert.Equal(1, result.TotalCount);
        Assert.Single(result.Alarms);
        Assert.Equal("E102", result.Alarms[0].AlarmCode);
        Assert.Equal("Active", result.Alarms[0].Status);
    }

    [Fact]
    public void GetSystemOverview_UsesStateCenterOnly()
    {
        var stateCenter = new RuntimeStateCenter();
        stateCenter.UpdateDeviceState("D01", new DeviceState
        {
            DeviceId = "D01",
            Status = DeviceStatusEnum.Online,
            LastUpdateTime = DateTime.UtcNow
        });
        stateCenter.UpdateDeviceState("D02", new DeviceState
        {
            DeviceId = "D02",
            Status = DeviceStatusEnum.Error,
            LastUpdateTime = DateTime.UtcNow
        });
        stateCenter.UpdateObjectState("O01", new ObjectState
        {
            ObjectId = "O01",
            CurrentPosition = "P01",
            Status = ObjectStatusEnum.Idle,
            UpdateTime = DateTime.UtcNow
        });

        var result = new WcsReadOnlyMcpTools().GetSystemOverview(stateCenter);

        Assert.Equal(2, result.DeviceCount);
        Assert.Equal(2, result.NonOfflineDeviceCount);
        Assert.Equal(1, result.ErrorDeviceCount);
        Assert.Equal(1, result.TrackedObjectCount);
    }
}
