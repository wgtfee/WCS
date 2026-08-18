namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;

public partial class SimulationVerificationViewModel
{
    private static readonly string[] SupportedPlcFaultKinds =
    [
        "Disconnect", "Timeout", "ReadFailure", "WriteFailure",
        "Stuck", "BitFlip", "Jitter", "OutOfRange"
    ];

    [ObservableProperty] private string _devicePanelStatusText = "S2/S3 设备操作只生成受治理仿真场景；不会直接写入生产控制器或真实轨道车。";

    [ObservableProperty] private string _devicePlcBlockKey = "PLC1.DB100";
    [ObservableProperty] private string _devicePlcBlockSizeText = "16";
    [ObservableProperty] private string _devicePlcInitialBase64 = "AAAAAAAAAAAAAAAAAAAAAA==";
    [ObservableProperty] private string _devicePlcWriteOffsetText = "0";
    [ObservableProperty] private string _devicePlcWriteBase64 = "AQIDBA==";
    [ObservableProperty] private string _devicePlcReadOffsetText = "0";
    [ObservableProperty] private string _devicePlcReadCountText = "4";
    [ObservableProperty] private string _devicePlcFaultId = "F-PLC-1";
    [ObservableProperty] private string _devicePlcFaultKind = "BitFlip";
    [ObservableProperty] private string _devicePlcFaultStartMsText = "30";
    [ObservableProperty] private string _devicePlcFaultEndMsText = "80";
    [ObservableProperty] private string _devicePlcFaultOffsetText = "0";
    [ObservableProperty] private string _devicePlcFaultLengthText = "1";
    [ObservableProperty] private string _devicePlcFaultBitIndexText = "0";
    [ObservableProperty] private string _devicePlcJitterMinimumText = "-1";
    [ObservableProperty] private string _devicePlcJitterMaximumText = "1";
    [ObservableProperty] private string _devicePlcReplacementBase64 = "";

    [ObservableProperty] private string _deviceRgvVehicleId = "RGV1";
    [ObservableProperty] private string _deviceRgvSourceNodeId = "N1";
    [ObservableProperty] private string _deviceRgvMiddleNodeId = "N2";
    [ObservableProperty] private string _deviceRgvDestinationNodeId = "N3";
    [ObservableProperty] private string _deviceRgvSegmentA = "S1";
    [ObservableProperty] private string _deviceRgvSegmentB = "S2";
    [ObservableProperty] private string _deviceRgvSegmentLengthMmText = "1000";
    [ObservableProperty] private string _deviceRgvSpeedMmPerSecondText = "1000";
    [ObservableProperty] private string _deviceRgvBatteryPercentText = "100";
    [ObservableProperty] private string _deviceRgvLoadId = "LOAD1";
    [ObservableProperty] private string _deviceRgvOfflineDurationMsText = "500";

    public string PlcFaultKindsText => string.Join(" / ", SupportedPlcFaultKinds.Select(TranslatePlcFaultKind));

    [RelayCommand]
    private void GenerateDevicePlcScenario() => TryLoadDevicePlcScenario();

    [RelayCommand]
    private async Task GenerateAndRegisterDevicePlcScenarioAsync()
    {
        if (TryLoadDevicePlcScenario())
            await RegisterScenarioAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void GenerateDeviceRgvScenario() => TryLoadDeviceRgvScenario();

    [RelayCommand]
    private async Task GenerateAndRegisterDeviceRgvScenarioAsync()
    {
        if (TryLoadDeviceRgvScenario())
            await RegisterScenarioAsync().ConfigureAwait(true);
    }

    private bool TryLoadDevicePlcScenario()
    {
        if (!TryGetVisualCommon(out var seed, out var start))
            return false;
        if (!TryRequired(DevicePlcBlockKey, "控制器数据块标识", out var blockKey) ||
            !TryRequired(DevicePlcFaultId, "异常编号", out var faultId) ||
            !TryRequired(DevicePlcFaultKind, "异常类型", out var faultKind))
            return false;

        var dotDb = blockKey.IndexOf(".DB", StringComparison.OrdinalIgnoreCase);
        if (dotDb <= 0)
            return DeviceError("控制器数据块标识必须使用 PLC_NAME.DB<number> 格式，例如 PLC1.DB100。");
        var plcName = blockKey[..dotDb];

        if (!SupportedPlcFaultKinds.Contains(faultKind, StringComparer.OrdinalIgnoreCase))
            return DeviceError($"控制器异常类型只支持：{PlcFaultKindsText}。");
        faultKind = SupportedPlcFaultKinds.First(x => string.Equals(x, faultKind, StringComparison.OrdinalIgnoreCase));

        if (!TryLong(DevicePlcBlockSizeText, "数据块大小", 1, 1_048_576, out var blockSize) ||
            !TryLong(DevicePlcWriteOffsetText, "写入偏移", 0, int.MaxValue, out var writeOffset) ||
            !TryLong(DevicePlcReadOffsetText, "读取偏移", 0, int.MaxValue, out var readOffset) ||
            !TryLong(DevicePlcReadCountText, "读取长度", 1, 1536, out var readCount) ||
            !TryLong(DevicePlcFaultStartMsText, "异常开始时间", 0, long.MaxValue - 1000, out var faultStart) ||
            !TryLong(DevicePlcFaultEndMsText, "异常结束时间", faultStart, long.MaxValue - 1000, out var faultEnd) ||
            !TryLong(DevicePlcFaultOffsetText, "异常偏移", 0, int.MaxValue, out var faultOffset) ||
            !TryLong(DevicePlcFaultLengthText, "异常长度", 1, 1536, out var faultLength) ||
            !TryLong(DevicePlcFaultBitIndexText, "位序号", 0, 7, out var bitIndex) ||
            !TryLong(DevicePlcJitterMinimumText, "抖动最小值", -255, 255, out var jitterMin) ||
            !TryLong(DevicePlcJitterMaximumText, "抖动最大值", -255, 255, out var jitterMax))
            return false;
        if (jitterMin > jitterMax)
            return DeviceError("抖动最小值不能大于抖动最大值。");

        byte[] initialBytes;
        byte[] writeBytes;
        byte[]? replacementBytes = null;
        try
        {
            initialBytes = string.IsNullOrWhiteSpace(DevicePlcInitialBase64) ? [] : Convert.FromBase64String(DevicePlcInitialBase64.Trim());
            writeBytes = Convert.FromBase64String(DevicePlcWriteBase64?.Trim() ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(DevicePlcReplacementBase64))
                replacementBytes = Convert.FromBase64String(DevicePlcReplacementBase64.Trim());
        }
        catch (FormatException)
        {
            return DeviceError("控制器初始数据、写入数据和替换数据必须是有效的 Base64。");
        }
        if (initialBytes.Length > blockSize)
            return DeviceError("初始数据解码后的字节数不能超过数据块大小。");
        if (writeBytes.Length is < 1 or > 1536)
            return DeviceError("写入数据解码后必须是 1～1536 字节。");
        if (writeOffset + writeBytes.Length > blockSize || readOffset + readCount > blockSize)
            return DeviceError("控制器读取或写入范围不能超过数据块大小。");

        var requiresBlock = faultKind is "Stuck" or "BitFlip" or "Jitter" or "OutOfRange";
        var faultTarget = requiresBlock ? blockKey : plcName;
        if (requiresBlock && faultOffset + faultLength > blockSize)
            return DeviceError("异常偏移与异常长度之和不能超过数据块大小。");
        if (replacementBytes is { Length: > 0 } && replacementBytes.Length != faultLength)
            return DeviceError("替换数据解码后的字节数必须等于异常长度。");

        var defineAt = 0L;
        var writeAt = 10L;
        var preReadAt = 20L;
        var effectiveFaultStart = Math.Max(faultStart, 30L);
        var effectiveFaultEnd = Math.Max(faultEnd, effectiveFaultStart + 10);
        var faultReadAt = effectiveFaultStart + Math.Max(1, (effectiveFaultEnd - effectiveFaultStart) / 2);
        var clearAt = effectiveFaultEnd + 1;
        var finalReadAt = clearAt + 10;
        var duration = finalReadAt + 20;

        var actions = new List<object>
        {
            Action("block-define", defineAt, 0, "plc.block.define", blockKey,
                Payload(("Size", (int)blockSize), ("InitialBase64", Convert.ToBase64String(initialBytes)))),
            Action("block-write", writeAt, 0, "plc.block.write", blockKey,
                Payload(("Offset", (int)writeOffset), ("DataBase64", Convert.ToBase64String(writeBytes)), ("ResultStateKey", "device.plc.write.result"))),
            Action("block-read-before-fault", preReadAt, 0, "plc.block.read", blockKey,
                Payload(("Offset", (int)readOffset), ("Count", (int)readCount), ("ResultStateKey", "device.plc.read.before"))),
            Action("fault-apply", effectiveFaultStart, 0, "plc.fault.apply", faultTarget,
                Payload(
                    ("Id", faultId), ("Kind", faultKind),
                    ("StartMilliseconds", effectiveFaultStart), ("EndMilliseconds", effectiveFaultEnd),
                    ("Offset", (int)faultOffset), ("Length", (int)faultLength),
                    ("BitIndex", (int)bitIndex), ("JitterMinimum", (int)jitterMin), ("JitterMaximum", (int)jitterMax),
                    ("ReplacementBase64", replacementBytes is null ? null : Convert.ToBase64String(replacementBytes)))),
            Action("block-read-during-fault", faultReadAt, 0, "plc.block.read", blockKey,
                Payload(("Offset", (int)readOffset), ("Count", (int)readCount), ("ResultStateKey", "device.plc.read.during"))),
            Action("fault-clear", clearAt, 0, "plc.fault.clear", faultId, Payload()),
            Action("block-read-after-clear", finalReadAt, 0, "plc.block.read", blockKey,
                Payload(("Offset", (int)readOffset), ("Count", (int)readCount), ("ResultStateKey", "device.plc.read.after")))
        };

        var assertions = new List<object>
        {
            Assertion("written-bytes", preReadAt, 1, "plc.block.equals", blockKey,
                Payload(("Offset", (int)writeOffset), ("DataBase64", Convert.ToBase64String(writeBytes)))),
            Assertion("fault-active", faultReadAt, 1, "plc.fault.active", faultId, true),
            Assertion("fault-cleared", finalReadAt, 1, "plc.fault.active", faultId, false)
        };

        var faultKindText = TranslatePlcFaultKind(faultKind);
        ApplyVisualScenario(
            $"visual-device-plc-{Slug(plcName)}-{Slug(faultKind)}",
            seed, start, duration, actions, assertions,
            $"S2 设备面板：{blockKey} 完成定义、写入和读取，模拟“{faultKindText}”异常后自动清除；全部通过现有 S2 受治理场景执行。 ");
        DevicePanelStatusText = $"已生成控制器设备场景：{blockKey}；异常类型={faultKindText}；异常目标={faultTarget}。";
        return true;
    }

    private bool TryLoadDeviceRgvScenario()
    {
        if (!TryGetVisualCommon(out var seed, out var start))
            return false;
        if (!TryRequired(DeviceRgvVehicleId, "轨道车编号", out var vehicleId) ||
            !TryRequired(DeviceRgvSourceNodeId, "起点", out var sourceNode) ||
            !TryRequired(DeviceRgvMiddleNodeId, "中间节点", out var middleNode) ||
            !TryRequired(DeviceRgvDestinationNodeId, "终点", out var destinationNode) ||
            !TryRequired(DeviceRgvSegmentA, "区段一", out var segmentA) ||
            !TryRequired(DeviceRgvSegmentB, "区段二", out var segmentB))
            return false;
        if (new[] { sourceNode, middleNode, destinationNode }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            return DeviceError("轨道车起点、中间节点和终点必须互不相同。");
        if (string.Equals(segmentA, segmentB, StringComparison.OrdinalIgnoreCase))
            return DeviceError("轨道车区段一和区段二必须不同。");

        if (!TryLong(DeviceRgvSegmentLengthMmText, "区段长度", 1, int.MaxValue, out var lengthMm) ||
            !TryLong(DeviceRgvSpeedMmPerSecondText, "运行速度", 1, int.MaxValue, out var speed) ||
            !TryLong(DeviceRgvBatteryPercentText, "电量", 0, 100, out var battery) ||
            !TryLong(DeviceRgvOfflineDurationMsText, "离线持续时间", 1, 86_400_000, out var offlineDuration))
            return false;

        var travelMs = checked((lengthMm * 1000L + speed - 1L) / speed);
        var firstAdvanceAt = checked(travelMs + 10);
        var offlineAt = checked(firstAdvanceAt + 10);
        var onlineAt = checked(offlineAt + offlineDuration);
        var secondAdvanceAt = checked(onlineAt + travelMs + 10);
        var unloadAt = checked(secondAdvanceAt + 10);
        var assertAt = checked(unloadAt + 10);
        var duration = checked(assertAt + 10);
        var hasLoad = !string.IsNullOrWhiteSpace(DeviceRgvLoadId);
        var loadId = DeviceRgvLoadId?.Trim() ?? string.Empty;
        var batteryFloor = Math.Max(0, (int)battery - 1);

        var actions = new List<object>
        {
            Action("segment-a", 0, 0, "rgv.segment.define", segmentA,
                Payload(("FromNodeId", sourceNode), ("ToNodeId", middleNode), ("LengthMillimeters", (int)lengthMm), ("SpeedLimitMillimetersPerSecond", (int)speed), ("Enabled", true))),
            Action("segment-b", 0, 1, "rgv.segment.define", segmentB,
                Payload(("FromNodeId", middleNode), ("ToNodeId", destinationNode), ("LengthMillimeters", (int)lengthMm), ("SpeedLimitMillimetersPerSecond", (int)speed), ("Enabled", true))),
            Action("vehicle", 0, 2, "rgv.vehicle.define", vehicleId,
                Payload(("InitialNodeId", sourceNode), ("SpeedMillimetersPerSecond", (int)speed), ("BatteryPercent", (int)battery), ("IsOnline", true), ("LoadId", (string?)null), ("Capabilities", "Carry")))
        };
        var order = 3;
        if (hasLoad)
            actions.Add(Action("load", 1, order++, "rgv.vehicle.load", vehicleId, Payload(("LoadId", loadId))));
        actions.Add(Action("route", 2, order++, "rgv.route.assign", vehicleId, Payload(("SegmentIds", new[] { segmentA, segmentB }))));
        actions.Add(Action("advance-a", firstAdvanceAt, 0, "rgv.vehicle.advance", vehicleId, Payload()));
        actions.Add(Action("offline", offlineAt, 0, "rgv.vehicle.online.set", vehicleId, Payload(("IsOnline", false))));
        actions.Add(Action("online", onlineAt, 0, "rgv.vehicle.online.set", vehicleId, Payload(("IsOnline", true))));
        actions.Add(Action("advance-b", secondAdvanceAt, 0, "rgv.vehicle.advance", vehicleId, Payload()));
        if (hasLoad)
            actions.Add(Action("unload", unloadAt, 0, "rgv.vehicle.unload", vehicleId, Payload(("ExpectedLoadId", loadId))));

        var assertions = new List<object>
        {
            Assertion("at-destination", assertAt, 0, "rgv.vehicle.at-node", vehicleId, destinationNode),
            Assertion("route-completed", assertAt, 1, "rgv.route.completed", vehicleId, true),
            Assertion("battery", assertAt, 2, "rgv.vehicle.battery.at-least", vehicleId, batteryFloor)
        };
        if (hasLoad)
            assertions.Add(Assertion("unloaded", assertAt, 3, "rgv.vehicle.load.equals", vehicleId, null));

        ApplyVisualScenario(
            $"visual-device-rgv-{Slug(vehicleId)}",
            seed, start, duration, actions, assertions,
            $"S3 设备面板：轨道车 {vehicleId} 从 {sourceNode} 经 {middleNode} 到 {destinationNode}，完成路线分配、分段前进、离线恢复以及装载/卸载，全部使用现有 S3 受治理场景。 ");
        DevicePanelStatusText = $"已生成轨道车设备场景：{vehicleId}；速度={speed} 毫米/秒；电量={battery}%；离线时长={offlineDuration} 毫秒。";
        return true;
    }

    private static string TranslatePlcFaultKind(string value) => value switch
    {
        "Disconnect" => "断线",
        "Timeout" => "超时",
        "ReadFailure" => "读取失败",
        "WriteFailure" => "写入失败",
        "Stuck" => "数据卡住",
        "BitFlip" => "位翻转",
        "Jitter" => "数据抖动",
        "OutOfRange" => "数据越界",
        _ => value
    };

    private bool DeviceError(string message)
    {
        DevicePanelStatusText = message;
        StatusText = message;
        return false;
    }
}