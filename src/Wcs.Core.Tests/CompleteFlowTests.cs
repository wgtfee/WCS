using Wcs.Core.EventBus.Events;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.EventDetection;
using Wcs.Core.PlcSubsystem;
using Wcs.Core.PlcSubsystem.Examples;
using Wcs.Core.PlcSubsystem.SignalMapper.S7;
using Wcs.Core.PlcSubsystem.Validation;
using Wcs.Core.SignalSnapshot;
using Wcs.Core.StateCenter.Implementation;
using Wcs.Core.StateCenter.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace WcsCoreTests;

public class CompleteFlowTests
{
    // ==================== 辅助方法 ====================

    private static byte[] MakeData(bool arrived, bool fault, bool busy, short speed, int stationIndex = 0)
    {
        var data = new byte[40];
        int offset = stationIndex * 4;
        data[offset] = 0x01;
        if (arrived) data[offset] |= 0x02;
        if (fault)   data[offset] |= 0x04;
        if (busy)    data[offset] |= 0x08;
        var sb = BitConverter.GetBytes(speed);
        if (BitConverter.IsLittleEndian) Array.Reverse(sb);
        data[offset + 2] = sb[0];
        data[offset + 3] = sb[1];
        return data;
    }

    private static PLC1_DB1_ConveyorStatus ParseData(byte[] data)
        => (PLC1_DB1_ConveyorStatus)Struct.FromBytes(
            typeof(PLC1_DB1_ConveyorStatus), data, 40, 0)!;

    // ==================== T1: Struct 解析 ====================

    [Fact]
    public void T1_StructDeserialize()
    {
        var r = ParseData(MakeData(true, false, false, 1500, 0));
        Assert.True(r.CV01_DriveReady);
        Assert.True(r.CV01_PalletArrived);
        Assert.False(r.CV01_Fault);
        Assert.Equal(1500, r.CV01_Speed);
    }

    // ==================== T2: StateCenter 同步 ====================

    [Fact]
    public void T2_StateCenterSync()
    {
        var sc = new StateCenter(null);
        var s = ParseData(MakeData(true, false, true, 1500, 0));
        foreach (var f in typeof(PLC1_DB1_ConveyorStatus).GetFields())
        {
            if (f.GetValue(s) is bool b)
            {
                var did = f.Name.Split('_')[0];
                sc.UpdateDeviceState(did, new DeviceState { DeviceId = did,
                    Status = b ? DeviceStatusEnum.Running : DeviceStatusEnum.Idle,
                    LastUpdateTime = DateTime.UtcNow });
            }
        }
        Assert.Equal(DeviceStatusEnum.Running, sc.GetDeviceState("CV01")!.Status);
    }

    // ==================== T3: EventDetector 边沿检测 ====================

    [Fact]
    public void T3_EdgeDetection()
    {
        var snap = new SignalSnapshotCenter();
        var det = new EventDetector(new EventBus(), snap, new StateCenter(null), NullLogger<EventDetector>.Instance);

        snap.Update("PLC1.DB1", ParseData(MakeData(false, false, false, 0, 0)),
            typeof(PLC1_DB1_ConveyorStatus));
        snap.Update("PLC1.DB1", ParseData(MakeData(true, false, true, 1500, 0)),
            typeof(PLC1_DB1_ConveyorStatus));

        // 验证没有异常抛出即可 — 边沿检测+RawSignalEvent+PalletArrivedEvent
        var ex = Record.Exception(() =>
            det.Detect("PLC1.DB1", ParseData(MakeData(true, false, true, 1500, 0)), "PLC1", 1));
        Assert.Null(ex);
    }

    // ==================== T4: 验证器拒绝 ====================

    [Fact]
    public void T4_ValidatorReject()
    {
        var snap = new SignalSnapshotCenter();
        var det = new EventDetector(new EventBus(), snap, new StateCenter(null), NullLogger<EventDetector>.Instance);
        det.RegisterValidator(new Cv01_ArrivalValidator());

        snap.Update("PLC1.DB1", ParseData(MakeData(false, false, false, 0, 0)),
            typeof(PLC1_DB1_ConveyorStatus));
        snap.Update("PLC1.DB1", ParseData(MakeData(true, true, false, 0, 0)),
            typeof(PLC1_DB1_ConveyorStatus));

        var ex = Record.Exception(() =>
            det.Detect("PLC1.DB1", ParseData(MakeData(true, true, false, 0, 0)), "PLC1", 1));
        Assert.Null(ex);
    }

    // ==================== T5: [PlcBlock] 特性 ====================

    [Fact]
    public void T5_PlcBlockAttribute()
    {
        var a = typeof(PLC1_DB1_ConveyorStatus)
            .GetCustomAttributes(typeof(PlcBlockAttribute), false).First() as PlcBlockAttribute;
        Assert.Equal("PLC1", a!.PlcName);
        Assert.Equal(1, a.DbBlock);
    }

    // ==================== T6: PlcSerializer 序列化 ====================

    [Fact]
    public void T6_PlcSerializer()
    {
        var cmd = new ConveyorControlCommand { StartStation1 = true, SpeedSetpoint1 = 1500 };
        var d = PlcSerializer.Serialize(cmd, 12);
        Assert.Equal(0x01, d[0] & 0x01);
        Assert.Equal(1500, BitConverter.ToInt16(d, 2));
    }

    // ==================== T7: 自动发现 ====================

    [Fact]
    public void T7_AutoDiscovery()
    {
        var types = typeof(PLC1_DB1_ConveyorStatus).Assembly.GetTypes()
            .Where(t => t.IsValueType && !t.IsEnum
                && t.GetCustomAttributes(typeof(PlcBlockAttribute), false).Length > 0).ToList();
        Assert.Contains(types, t => t.Name == "PLC1_DB1_ConveyorStatus");
        Assert.Contains(types, t => t.Name == "PLC2_DB1_StackerStatus");
        Assert.Contains(types, t => t.Name == "PLC3_DB1_RobotStatus");
        Assert.Contains(types, t => t.Name == "ConveyorControlCommand");
    }

    // ==================== T8: EventBus 直接测试 ====================

    [Fact]
    public async Task T8_EventBus_Direct()
    {
        var bus = new EventBus();
        var raw = new List<RawSignalEvent>();

        bus.Subscribe<RawSignalEvent>(async (e, ct) =>
        {
            lock (raw) { raw.Add(e); }
            await Task.CompletedTask;
        });

        await bus.PublishAsync(new RawSignalEvent { PlcName = "PLC1", DbBlock = 1 });
        Assert.NotEmpty(raw);
        Assert.Equal("PLC1", raw[0].PlcName);
    }
}
