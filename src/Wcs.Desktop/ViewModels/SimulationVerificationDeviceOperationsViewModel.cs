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

    [ObservableProperty] private string _devicePanelStatusText = "S2/S3 设备操作只生成受治理 Scenario DSL；不会直接写生产 PLC 或真实 RGV。";

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

    public string PlcFaultKindsText => string.Join(" / ", SupportedPlcFaultKinds);

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
        if (!TryRequired(DevicePlcBlockKey, "PLC Block Key", out var blockKey) ||
            !TryRequired(DevicePlcFaultId, "Fault Id", out var faultId) ||
            !TryRequired(DevicePlcFaultKind, "Fault Kind", out var faultKind))
            return false;

        var dotDb = blockKey.IndexOf(".DB", StringComparison.OrdinalIgnoreCase);
        if (dotDb <= 0)
            return DeviceError("PLC Block Key 必须使用 PLC_NAME.DB<number> 格式，例如 PLC1.DB100。");
        var plcName = blockKey[..dotDb];

        if (!SupportedPlcFaultKinds.Contains(faultKind, StringComparer.OrdinalIgnoreCase))
            return DeviceError($"PLC Fault Kind 只支持：{PlcFaultKindsText}。");
        faultKind = SupportedPlcFaultKinds.First(x => string.Equals(x, faultKind, StringComparison.OrdinalIgnoreCase));

        if (!TryLong(DevicePlcBlockSizeText, "Block Size", 1, 1_048_576, out var blockSize) ||
            !TryLong(DevicePlcWriteOffsetText, "Write Offset", 0, int.MaxValue, out var writeOffset) ||
            !TryLong(DevicePlcReadOffsetText, "Read Offset", 0, int.MaxValue, out var readOffset) ||
            !TryLong(DevicePlcReadCountText, "Read Count", 1, 1536, out var readCount) ||
            !TryLong(DevicePlcFaultStartMsText, "Fault Start", 0, long.MaxValue - 1000, out var faultStart) ||
            !TryLong(DevicePlcFaultEndMsText, "Fault End", faultStart, long.MaxValue - 1000, out var faultEnd) ||
            !TryLong(DevicePlcFaultOffsetText, "Fault Offset", 0, int.MaxValue, out var faultOffset) ||
            !TryLong(DevicePlcFaultLengthText, "Fault Length", 1, 1536, out var faultLength) ||
            !TryLong(DevicePlcFaultBitIndexText, "BitIndex", 0, 7, out var bitIndex) ||
            !TryLong(DevicePlcJitterMinimumText, "Jitter Minimum", -255, 255, out var jitterMin) ||
            !TryLong(DevicePlcJitterMaximumText, "Jitter Maximum", -255, 255, out var jitterMax))
            return false;
        if (jitterMin > jitterMax)
            return DeviceError("Jitter Minimum 不能大于 Jitter Maximum。");

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
            return DeviceError("PLC Initial / Write / Replacement 必须是有效 Base64。");
        }
        if (initialBytes.Length > blockSize)
            return DeviceError("InitialBase64 解码后的字节数不能超过 Block Size。");
        if (writeBytes.Length is < 1 or > 1536)
            return DeviceError("WriteBase64 解码后必须是 1～1536 字节。");
        if (writeOffset + writeBytes.Length > blockSize || readOffset + readCount > blockSize)
            return DeviceError("PLC Read/Write 范围不能超过 Block Size。");

        var requiresBlock = faultKind is "Stuck" or "BitFlip" or "Jitter" or "OutOfRange";
        var faultTarget = requiresBlock ? blockKey : plcName;
        if (requiresBlock && faultOffset + faultLength > blockSize)
            return DeviceError("PLC Fault Offset + Length 不能超过 Block Size。");
        if (replacementBytes is { Length: > 0 } && replacementBytes.Length != faultLength)
            return DeviceError("ReplacementBase64 解码字节数必须等于 Fault Length。");

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

        ApplyVisualScenario(
            $"visual-device-plc-{Slug(plcName)}-{Slug(faultKind)}",
            seed, start, duration, actions, assertions,
            $"S2 设备面板：{blockKey} define/write/read，并注入 {faultKind} ({faultTarget}) 后 clear；Read/Write/Fault 全部通过现有 S2 DSL。 ");
        DevicePanelStatusText = $"已生成 PLC 设备场景：{blockKey} / {faultKind}。Fault target={faultTarget}。";
        return true;
    }

    private bool TryLoadDeviceRgvScenario()
    {
        if (!TryGetVisualCommon(out var seed, out var start))
            return false;
        if (!TryRequired(DeviceRgvVehicleId, "VehicleId", out var vehicleId) ||
            !TryRequired(DeviceRgvSourceNodeId, "Source Node", out var sourceNode) ||
            !TryRequired(DeviceRgvMiddleNodeId, "Middle Node", out var middleNode) ||
            !TryRequired(DeviceRgvDestinationNodeId, "Destination Node", out var destinationNode) ||
            !TryRequired(DeviceRgvSegmentA, "Segment A", out var segmentA) ||
            !TryRequired(DeviceRgvSegmentB, "Segment B", out var segmentB))
            return false;
        if (new[] { sourceNode, middleNode, destinationNode }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            return DeviceError("RGV Source / Middle / Destination 必须互不相同。");
        if (string.Equals(segmentA, segmentB, StringComparison.OrdinalIgnoreCase))
            return DeviceError("RGV Segment A / B 必须不同。");

        if (!TryLong(DeviceRgvSegmentLengthMmText, "Segment Length", 1, int.MaxValue, out var lengthMm) ||
            !TryLong(DeviceRgvSpeedMmPerSecondText, "Vehicle Speed", 1, int.MaxValue, out var speed) ||
            !TryLong(DeviceRgvBatteryPercentText, "Battery", 0, 100, out var battery) ||
            !TryLong(DeviceRgvOfflineDurationMsText, "Offline Duration", 1, 86_400_000, out var offlineDuration))
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
            $"S3 设备面板：{vehicleId} {sourceNode}→{middleNode}→{destinationNode}，route/advance、offline/online、load/unload 均使用现有 S3 DSL。 ");
        DevicePanelStatusText = $"已生成 RGV 设备场景：{vehicleId}，Speed={speed}mm/s，Battery={battery}%，Offline={offlineDuration}ms。";
        return true;
    }

    private bool DeviceError(string message)
    {
        DevicePanelStatusText = message;
        StatusText = message;
        return false;
    }
}
