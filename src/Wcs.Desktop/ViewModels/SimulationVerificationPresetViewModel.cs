namespace Wcs.Desktop.ViewModels;

using CommunityToolkit.Mvvm.Input;

public partial class SimulationVerificationViewModel
{
    [RelayCommand]
    private void LoadPlcDisconnectPreset() => ApplyPreset(
        "plc-disconnect-recovery",
        20260810,
        "plc-disconnect-recovery.json",
        """
        {"SchemaVersion":1,"ScenarioId":"plc-disconnect-recovery","Version":"1.0.0","Seed":20260810,"StartTimeUtc":"2026-08-10T00:00:00+00:00","DurationMilliseconds":15000,"StopOnAssertionFailure":true,"Actions":[{"Id":"disconnect","AtMilliseconds":1000,"Order":0,"Kind":"plc.connection.set","Target":"PLC1","Payload":{"Connected":false}},{"Id":"reconnect","AtMilliseconds":10000,"Order":0,"Kind":"plc.connection.set","Target":"PLC1","Payload":{"Connected":true}}],"Assertions":[{"Id":"assert-offline","AtMilliseconds":2000,"Order":0,"Kind":"plc.connected","Target":"PLC1","Expected":false},{"Id":"assert-online","AtMilliseconds":11000,"Order":0,"Kind":"plc.connected","Target":"PLC1","Expected":true}]}
        """,
        "S2 PLC 断线/恢复：验证虚拟 PLC 离线与重连状态，全部动作仅存在于 Simulation State。 ");

    [RelayCommand]
    private void LoadTrafficDeadlockPreset() => ApplyPreset(
        "traffic-two-rgv-deadlock",
        20260731,
        "traffic-two-rgv-deadlock.json",
        """
        {"SchemaVersion":1,"ScenarioId":"traffic-two-rgv-deadlock","Version":"1.0.0","Seed":20260731,"StartTimeUtc":"2026-07-31T00:00:00+00:00","DurationMilliseconds":100,"StopOnAssertionFailure":true,"Actions":[{"Id":"segment-1","AtMilliseconds":0,"Order":0,"Kind":"rgv.segment.define","Target":"S1","Payload":{"FromNodeId":"N1","ToNodeId":"N2","LengthMillimeters":1000,"SpeedLimitMillimetersPerSecond":1000,"Enabled":true}},{"Id":"segment-2","AtMilliseconds":0,"Order":1,"Kind":"rgv.segment.define","Target":"S2","Payload":{"FromNodeId":"N2","ToNodeId":"N1","LengthMillimeters":1000,"SpeedLimitMillimetersPerSecond":1000,"Enabled":true}},{"Id":"vehicle-1","AtMilliseconds":0,"Order":2,"Kind":"rgv.vehicle.define","Target":"RGV1","Payload":{"InitialNodeId":"N1","SpeedMillimetersPerSecond":1000,"BatteryPercent":100,"IsOnline":true,"Capabilities":"Carry"}},{"Id":"vehicle-2","AtMilliseconds":0,"Order":3,"Kind":"rgv.vehicle.define","Target":"RGV2","Payload":{"InitialNodeId":"N2","SpeedMillimetersPerSecond":1000,"BatteryPercent":100,"IsOnline":true,"Capabilities":"Carry"}},{"Id":"zone-1","AtMilliseconds":0,"Order":4,"Kind":"traffic.zone.define","Target":"Z1","Payload":{"SegmentIds":["S1"],"Capacity":1,"Kind":"SharedSegment"}},{"Id":"zone-2","AtMilliseconds":0,"Order":5,"Kind":"traffic.zone.define","Target":"Z2","Payload":{"SegmentIds":["S2"],"Capacity":1,"Kind":"SharedSegment"}},{"Id":"rgv1-own-s1","AtMilliseconds":10,"Order":0,"Kind":"traffic.reserve","Target":"RGV1","Payload":{"SegmentId":"S1","Priority":10,"LeaseMilliseconds":10000}},{"Id":"rgv2-own-s2","AtMilliseconds":10,"Order":1,"Kind":"traffic.reserve","Target":"RGV2","Payload":{"SegmentId":"S2","Priority":20,"LeaseMilliseconds":10000}},{"Id":"rgv1-wait-s2","AtMilliseconds":20,"Order":0,"Kind":"traffic.reserve","Target":"RGV1","Payload":{"SegmentId":"S2","Priority":10,"LeaseMilliseconds":10000}},{"Id":"rgv2-wait-s1","AtMilliseconds":20,"Order":1,"Kind":"traffic.reserve","Target":"RGV2","Payload":{"SegmentId":"S1","Priority":20,"LeaseMilliseconds":10000}},{"Id":"detect","AtMilliseconds":30,"Order":0,"Kind":"traffic.deadlock.detect","Target":"global","Payload":{}}],"Assertions":[{"Id":"deadlock-exists","AtMilliseconds":40,"Order":0,"Kind":"traffic.deadlock.exists","Target":"global","Expected":true},{"Id":"rgv1-waits-rgv2","AtMilliseconds":40,"Order":1,"Kind":"traffic.waits-for","Target":"RGV1","Expected":"RGV2"},{"Id":"rgv2-waits-rgv1","AtMilliseconds":40,"Order":2,"Kind":"traffic.waits-for","Target":"RGV2","Expected":"RGV1"}]}
        """,
        "S3/S4 双 RGV 死锁：两个车辆分别持有一个区段并交叉等待，验证 wait-for graph 与 deadlock detection。 ");

    [RelayCommand]
    private void LoadExternalTimeoutPreset() => ApplyPreset(
        "external-timeout-recovery",
        20260801,
        "external-timeout-recovery.json",
        """
        {"SchemaVersion":1,"ScenarioId":"external-timeout-recovery","Version":"1.0.0","Seed":20260801,"StartTimeUtc":"2026-08-01T00:00:00+00:00","DurationMilliseconds":100,"StopOnAssertionFailure":true,"Actions":[{"Id":"endpoint","AtMilliseconds":0,"Order":0,"Kind":"external.endpoint.define","Target":"MES1","Payload":{"Kind":"Mes"}},{"Id":"fault","AtMilliseconds":0,"Order":1,"Kind":"external.fault.apply","Target":"F1","Payload":{"EndpointId":"MES1","Kind":"Timeout","StartsAtOffsetMilliseconds":0,"EndsAtOffsetMilliseconds":50,"DelayMilliseconds":0}},{"Id":"invoke","AtMilliseconds":0,"Order":2,"Kind":"external.request.invoke","Target":"MES1","Payload":{"Operation":"Order.Push","IdempotencyKey":"scenario-key","PayloadHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","MaxAttempts":2,"TimeoutMilliseconds":20,"RetryDelayMilliseconds":60}}],"Assertions":[{"Id":"request-state","AtMilliseconds":70,"Order":0,"Kind":"external.request.state","Target":"EXTREQ-000000000001","Expected":"Succeeded"},{"Id":"attempts","AtMilliseconds":70,"Order":1,"Kind":"external.request.attempts","Target":"EXTREQ-000000000001","Expected":2},{"Id":"circuit","AtMilliseconds":70,"Order":2,"Kind":"external.circuit.state","Target":"MES1","Expected":"Closed"},{"Id":"fault-ended","AtMilliseconds":70,"Order":3,"Kind":"external.fault.active","Target":"F1","Expected":false}]}
        """,
        "S5 外部超时恢复：第一次请求超时，虚拟时间重试后成功，并验证 Circuit 关闭与故障窗口结束。 ");

    [RelayCommand]
    private void LoadHealthRulPreset() => ApplyPreset(
        "synthetic-health-rul",
        20260801,
        "synthetic-health-rul.json",
        """
        {"SchemaVersion":1,"ScenarioId":"synthetic-health-rul","Version":"1.0.0","Seed":20260801,"StartTimeUtc":"2026-08-01T00:00:00+00:00","DurationMilliseconds":259200000,"StopOnAssertionFailure":true,"Actions":[{"Id":"define","AtMilliseconds":0,"Order":0,"Kind":"health.asset.define","Target":"RGV-S6","Payload":{"InitialHealthScore":100,"InitialFusionRiskScore":0.05,"IndependentSourceCount":1}},{"Id":"degrade-48","AtMilliseconds":172800000,"Order":0,"Kind":"health.profile.linear","Target":"RGV-S6","Payload":{"TargetHealthScore":55,"TargetFusionRiskScore":0.75,"SampleIntervalMilliseconds":3600000,"Reason":"bearing-wear"}},{"Id":"forecast-48","AtMilliseconds":172800000,"Order":1,"Kind":"health.forecast.oracle","Target":"RGV-S6","Payload":{"FailureProbability24Hours":0.10,"FailureProbability72Hours":0.25,"FailureProbability168Hours":0.45,"RulLowerHours":120,"RulMedianHours":180,"RulUpperHours":260,"Phase":"degradation"}},{"Id":"degrade-72","AtMilliseconds":259200000,"Order":0,"Kind":"health.profile.linear","Target":"RGV-S6","Payload":{"TargetHealthScore":30,"TargetFusionRiskScore":0.95,"SampleIntervalMilliseconds":3600000,"Reason":"bearing-wear"}},{"Id":"forecast-72","AtMilliseconds":259200000,"Order":1,"Kind":"health.forecast.oracle","Target":"RGV-S6","Payload":{"FailureProbability24Hours":0.25,"FailureProbability72Hours":0.50,"FailureProbability168Hours":0.80,"RulLowerHours":40,"RulMedianHours":100,"RulUpperHours":160,"Phase":"degradation"}},{"Id":"outcome","AtMilliseconds":259200000,"Order":2,"Kind":"health.outcome.record","Target":"RGV-S6","Payload":{"Kind":"ObservedFailure","Note":"synthetic-bearing-failure"}}],"Assertions":[{"Id":"grade","AtMilliseconds":259200000,"Order":0,"Kind":"health.asset.grade","Target":"RGV-S6","Expected":"Critical"},{"Id":"score","AtMilliseconds":259200000,"Order":1,"Kind":"health.asset.score.at-most","Target":"RGV-S6","Expected":30},{"Id":"samples","AtMilliseconds":259200000,"Order":2,"Kind":"health.sample.count","Target":"RGV-S6","Expected":73},{"Id":"trend","AtMilliseconds":259200000,"Order":3,"Kind":"health.trend.direction","Target":"RGV-S6","Expected":"Deteriorating"},{"Id":"feature","AtMilliseconds":259200000,"Order":4,"Kind":"health.feature.valid","Target":"RGV-S6","Expected":true},{"Id":"contract","AtMilliseconds":259200000,"Order":5,"Kind":"health.forecast.contract.valid","Target":"RGV-S6","Expected":true},{"Id":"rul","AtMilliseconds":259200000,"Order":6,"Kind":"health.rul.nonincreasing","Target":"RGV-S6","Expected":true},{"Id":"probability","AtMilliseconds":259200000,"Order":7,"Kind":"health.probability.nondecreasing","Target":"RGV-S6","Expected":true},{"Id":"outcome-kind","AtMilliseconds":259200000,"Order":8,"Kind":"health.outcome.kind","Target":"RGV-S6","Expected":"ObservedFailure"}]}
        """,
        "S6 Health/RUL：用虚拟时间生成 72 小时轴承退化、两次 Forecast Oracle 和合成故障结果，验证健康等级、特征、概率与 RUL 单调性。 ");

    [RelayCommand]
    private void LoadIntegratedRecoveryPreset() => ApplyPreset(
        "integrated-recovery-exactly-once",
        20260802,
        "integrated-recovery-exactly-once.json",
        """
        {"SchemaVersion":1,"ScenarioId":"integrated-recovery-exactly-once","Version":"1.0.0","Seed":20260802,"StartTimeUtc":"2026-08-02T00:00:00+00:00","DurationMilliseconds":2300,"StopOnAssertionFailure":true,"Actions":[{"Id":"define","AtMilliseconds":0,"Order":0,"Kind":"integration.mission.define","Target":"M1","Payload":{"PlcBlockKey":"PLC1.DB100","VehicleId":"RGV1","LoadId":"LOAD1","SourceNodeId":"N1","DestinationNodeId":"N3","ExternalEndpointId":"MES1","ExternalSystemKind":"Mes","HealthAssetId":"ASSET1","Priority":100,"VehicleSpeedMillimetersPerSecond":1000,"VehicleBatteryPercent":100,"InitialHealthScore":95,"InitialFusionRiskScore":0.05,"Segments":[{"SegmentId":"S1","FromNodeId":"N1","ToNodeId":"N2","LengthMillimeters":1000,"SpeedLimitMillimetersPerSecond":1000},{"SegmentId":"S2","FromNodeId":"N2","ToNodeId":"N3","LengthMillimeters":1000,"SpeedLimitMillimetersPerSecond":1000}]}},{"Id":"dispatch","AtMilliseconds":10,"Order":0,"Kind":"integration.mission.dispatch","Target":"M1","Payload":{}},{"Id":"advance-1","AtMilliseconds":1010,"Order":0,"Kind":"integration.mission.advance","Target":"M1","Payload":{}},{"Id":"advance-2","AtMilliseconds":2010,"Order":0,"Kind":"integration.mission.advance","Target":"M1","Payload":{}},{"Id":"ack-1","AtMilliseconds":2100,"Order":0,"Kind":"integration.mission.ack","Target":"M1","Payload":{}},{"Id":"ack-replay","AtMilliseconds":2200,"Order":0,"Kind":"integration.mission.ack","Target":"M1","Payload":{}}],"Assertions":[{"Id":"state","AtMilliseconds":2300,"Order":0,"Kind":"integration.mission.state","Target":"M1","Expected":"Acknowledged"},{"Id":"consistent","AtMilliseconds":2300,"Order":1,"Kind":"integration.mission.consistent","Target":"M1","Expected":true},{"Id":"exactly-once","AtMilliseconds":2300,"Order":2,"Kind":"integration.external.exactly-once","Target":"M1","Expected":true}]}
        """,
        "S7 全链恢复/幂等：贯穿虚拟 PLC、RGV、Traffic、External、Health，并通过重复 ACK 验证 exactly-once。 ");

    private void ApplyPreset(
        string scenarioId,
        long seed,
        string scenarioFile,
        string json,
        string description)
    {
        ScenarioId = scenarioId;
        ScenarioVersion = "1.0.0";
        ScenarioSeedText = seed.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ScenarioFile = scenarioFile;
        ScenarioSource = "Wcs.Desktop Governed Preset";
        ScenarioApprovedBy = "simulation-operator";
        ScenarioJson = json.Trim();
        SpeedFactorText = "1";
        Assertions.Clear();
        CheckpointHash = "-";
        CheckpointStateText = description;
        StatusText = $"已载入预置场景：{scenarioId}。下一步执行“校验并注册”，通过 S0 Manifest/SHA-256 后才能创建 Run。";
    }
}
