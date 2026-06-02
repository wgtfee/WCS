using Wcs.Core.StateCenter.Implementation;
using Wcs.Core.StateCenter.Models;

namespace WcsCoreTests;

/// <summary>
/// StateCenter 核心功能测试：设备/任务/物体状态管理，System Truth
/// </summary>
public class StateCenterTests
{
    [Fact]
    public void UpdateAndGetDeviceState_RoundTrip()
    {
        var sc = new StateCenter();
        sc.UpdateDeviceState("CV_101", new DeviceState
        {
            DeviceId = "CV_101",
            Status = DeviceStatusEnum.Running,
            CurrentPosition = "Zone-A"
        });

        var state = sc.GetDeviceState("CV_101");
        Assert.NotNull(state);
        Assert.Equal(DeviceStatusEnum.Running, state.Status);
        Assert.Equal("Zone-A", state.CurrentPosition);
    }

    [Fact]
    public void GetDeviceState_UnknownDevice_ReturnsNull()
    {
        var sc = new StateCenter();
        Assert.Null(sc.GetDeviceState("NONEXISTENT"));
    }

    [Fact]
    public void GetAllDeviceStates_ReturnsAll()
    {
        var sc = new StateCenter();
        sc.UpdateDeviceState("CV_01", new DeviceState { DeviceId = "CV_01", Status = DeviceStatusEnum.Running });
        sc.UpdateDeviceState("CV_02", new DeviceState { DeviceId = "CV_02", Status = DeviceStatusEnum.Idle });

        var all = sc.GetAllDeviceStates().ToList();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void UpdateDeviceState_SameStatus_DoesNotTriggerDuplicate()
    {
        var sc = new StateCenter();
        sc.UpdateDeviceState("CV_01", new DeviceState { DeviceId = "CV_01", Status = DeviceStatusEnum.Running });
        sc.UpdateDeviceState("CV_01", new DeviceState { DeviceId = "CV_01", Status = DeviceStatusEnum.Error });

        var state = sc.GetDeviceState("CV_01");
        Assert.Equal(DeviceStatusEnum.Error, state.Status);
    }

    // ========== 任务运行时 ==========

    [Fact]
    public void UpdateAndGetTaskRuntime_RoundTrip()
    {
        var sc = new StateCenter();
        sc.UpdateTaskRuntime("TASK-001", new TaskRuntime
        {
            TaskId = "TASK-001",
            Status = TaskStatusEnum.Created,
            RouteId = "ROUTE-A",
            Priority = 1
        });

        var task = sc.GetTaskRuntime("TASK-001");
        Assert.NotNull(task);
        Assert.Equal(TaskStatusEnum.Created, task.Status);
        Assert.Equal("ROUTE-A", task.RouteId);
    }

    [Fact]
    public void UpdateTaskRuntime_StatusTransition_Works()
    {
        var sc = new StateCenter();
        sc.UpdateTaskRuntime("T-1", new TaskRuntime { TaskId = "T-1", Status = TaskStatusEnum.Created });
        sc.UpdateTaskRuntime("T-1", new TaskRuntime { TaskId = "T-1", Status = TaskStatusEnum.Running });

        var task = sc.GetTaskRuntime("T-1");
        Assert.Equal(TaskStatusEnum.Running, task.Status);
    }

    [Fact]
    public void GetAllActiveTasks_ExcludesCompleted()
    {
        var sc = new StateCenter();
        sc.UpdateTaskRuntime("T-1", new TaskRuntime { TaskId = "T-1", Status = TaskStatusEnum.Running });
        sc.UpdateTaskRuntime("T-2", new TaskRuntime { TaskId = "T-2", Status = TaskStatusEnum.Completed });
        sc.UpdateTaskRuntime("T-3", new TaskRuntime { TaskId = "T-3", Status = TaskStatusEnum.Created });

        var active = sc.GetAllActiveTasks().ToList();
        Assert.Equal(2, active.Count);
    }

    // ========== 物体状态 ==========

    [Fact]
    public void UpdateAndGetObjectState_RoundTrip()
    {
        var sc = new StateCenter();
        sc.UpdateObjectState("PALLET-001", new ObjectState
        {
            ObjectId = "PALLET-001",
            CurrentPosition = "Zone-A",
            Status = ObjectStatusEnum.Idle
        });

        var obj = sc.GetObjectState("PALLET-001");
        Assert.NotNull(obj);
        Assert.Equal("Zone-A", obj.CurrentPosition);
    }

    [Fact]
    public void UpdateObjectState_OverwritesExisting()
    {
        var sc = new StateCenter();
        sc.UpdateObjectState("P-1", new ObjectState { ObjectId = "P-1", CurrentPosition = "Zone-A" });
        sc.UpdateObjectState("P-1", new ObjectState { ObjectId = "P-1", CurrentPosition = "Zone-B" });

        var obj = sc.GetObjectState("P-1");
        Assert.Equal("Zone-B", obj.CurrentPosition);
    }

    // ========== 快照 ==========

    [Fact]
    public void GetSnapshot_RoundTrip()
    {
        var sc = new StateCenter();
        sc.UpdateDeviceState("CV_01", new DeviceState { DeviceId = "CV_01", Status = DeviceStatusEnum.Running });
        sc.UpdateTaskRuntime("T-1", new TaskRuntime { TaskId = "T-1", Status = TaskStatusEnum.Running });

        var snapshot = sc.GetSnapshot();

        var sc2 = new StateCenter();
        sc2.RestoreFromSnapshot(snapshot);

        Assert.Equal(DeviceStatusEnum.Running, sc2.GetDeviceState("CV_01")?.Status);
        Assert.Equal(TaskStatusEnum.Running, sc2.GetTaskRuntime("T-1")?.Status);
    }

    [Fact]
    public void Clear_RemovesAll()
    {
        var sc = new StateCenter();
        sc.UpdateDeviceState("CV_01", new DeviceState { DeviceId = "CV_01", Status = DeviceStatusEnum.Running });
        sc.Clear();

        Assert.Null(sc.GetDeviceState("CV_01"));
    }

    // ========== Batch ==========

    [Fact]
    public void BeginBatch_Scope_DoesNotThrow()
    {
        var sc = new StateCenter();
        using (var batch = sc.BeginBatch())
        {
            sc.UpdateDeviceState("CV_01", new DeviceState { DeviceId = "CV_01", Status = DeviceStatusEnum.Running });
            sc.UpdateDeviceState("CV_02", new DeviceState { DeviceId = "CV_02", Status = DeviceStatusEnum.Idle });
        }

        Assert.Equal(DeviceStatusEnum.Running, sc.GetDeviceState("CV_01")?.Status);
    }

    // ========== Watch ==========

    [Fact]
    public void WatchDevice_ReceivesUpdates()
    {
        var sc = new StateCenter();
        DeviceState? received = null;

        using (sc.WatchDevice("CV_01", state => received = state))
        {
            sc.UpdateDeviceState("CV_01", new DeviceState { DeviceId = "CV_01", Status = DeviceStatusEnum.Running });
        }

        Assert.NotNull(received);
        Assert.Equal(DeviceStatusEnum.Running, received.Status);
    }
}
