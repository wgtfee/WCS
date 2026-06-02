using Wcs.Core.AlarmCenter;
using Wcs.Core.AlarmCenter.Engine;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.StateCenter.Models;

namespace WcsCoreTests;

/// <summary>
/// AlarmCenter + AlarmAggregationEngine 测试：5 层管线、根因分析
/// </summary>
public class AlarmCenterTests
{
    private static (AlarmCenter alarmCenter, EventBus bus) CreateAlarmCenter()
    {
        var bus = new EventBus();
        var alarmCenter = new AlarmCenter(bus);
        return (alarmCenter, bus);
    }

    // ========== 报警规则 ==========

    [Fact]
    public void SetAlarmRule_StoresRule()
    {
        var (ac, _) = CreateAlarmCenter();
        ac.SetAlarmRule(new AlarmRule
        {
            AlarmCode = "PLC_ERR",
            Level = AlarmLevelEnum.Error,
            DelayRaiseMs = 100,
            DelayRecoverMs = 200
        });

        // No direct getter, but rule affects behavior
        // Just verify no exception
    }

    // ========== 报警生命周期 ==========

    [Fact]
    public async Task RaiseAlarm_WithRule_EventuallyActive()
    {
        var (ac, _) = CreateAlarmCenter();
        ac.SetAlarmRule(new AlarmRule
        {
            AlarmCode = "TEST_ERR",
            Level = AlarmLevelEnum.Error,
            DelayRaiseMs = 10,
            DelayRecoverMs = 5000
        });

        await ac.RaiseAlarmAsync("TEST_ERR", AlarmLevelEnum.Error, "Test error");

        // Wait for debounce to complete
        await Task.Delay(50);

        var activeAlarms = ac.GetActiveAlarms().ToList();
        Assert.NotEmpty(activeAlarms);
        var alarm = activeAlarms.First(a => a.AlarmCode == "TEST_ERR");
        Assert.Equal(AlarmStatusEnum.Active, alarm.Status);
    }

    [Fact]
    public async Task RaiseAndRecover_FullLifecycle()
    {
        var (ac, _) = CreateAlarmCenter();
        ac.SetAlarmRule(new AlarmRule
        {
            AlarmCode = "LIFECYCLE",
            Level = AlarmLevelEnum.Warning,
            DelayRaiseMs = 10,
            DelayRecoverMs = 10
        });

        await ac.RaiseAlarmAsync("LIFECYCLE", AlarmLevelEnum.Warning, "Lifecycle test");
        await Task.Delay(50);

        Assert.True(ac.GetActiveCount() > 0);

        await ac.RecoverAlarmAsync("LIFECYCLE");
        await Task.Delay(50);

        var active = ac.GetActiveAlarms().Where(a => a.AlarmCode == "LIFECYCLE").ToList();
        Assert.Empty(active); // recovered
    }

    [Fact]
    public async Task AcknowledgeAlarm_TransitionsState()
    {
        var (ac, _) = CreateAlarmCenter();
        ac.SetAlarmRule(new AlarmRule
        {
            AlarmCode = "ACK_TEST",
            Level = AlarmLevelEnum.Error,
            DelayRaiseMs = 10,
            DelayRecoverMs = 5000
        });

        await ac.RaiseAlarmAsync("ACK_TEST", AlarmLevelEnum.Error, "Ack test");
        await Task.Delay(50);

        var alarm = ac.GetActiveAlarms().First(a => a.AlarmCode == "ACK_TEST");
        await ac.AcknowledgeAlarmAsync(alarm.AlarmId);

        var acked = ac.GetAlarm(alarm.AlarmId);
        Assert.Equal(AlarmStatusEnum.Acknowledged, acked?.Status);
    }

    // ========== AlarmAggregationEngine 根因分析 ==========

    [Fact]
    public void Aggregation_RegisterAndGetRootCause()
    {
        var agg = new AlarmAggregationEngine();

        // Register two alarms in same group — first is root
        agg.RegisterAlarm("ALM-001", "device-1", "POWER_GROUP");
        agg.RegisterAlarm("ALM-002", "device-1", "POWER_GROUP");

        Assert.Equal("ALM-001", agg.GetRootCause("ALM-002"));
        Assert.True(agg.IsSuppressed("ALM-002"));
        Assert.False(agg.IsSuppressed("ALM-001"));
    }

    [Fact]
    public void Aggregation_RecoverGroup_ReleasesChildren()
    {
        var agg = new AlarmAggregationEngine();
        agg.RegisterAlarm("ALM-001", "device-1", "GROUP");
        agg.RegisterAlarm("ALM-002", "device-1", "GROUP");

        var released = agg.RecoverGroup("ALM-001");
        Assert.Contains("ALM-002", released);
    }

    [Fact]
    public void Aggregation_NoGroup_ReturnsRoot()
    {
        var agg = new AlarmAggregationEngine();
        var isRoot = agg.RegisterAlarm("ALM-001", "device-1", null);
        Assert.True(isRoot);
        Assert.Null(agg.GetRootCause("ALM-001"));
    }

    // ========== 树形根因层次 (Phase 3) ==========

    [Fact]
    public void Aggregation_RegisterHierarchy_ComputesDepth()
    {
        var agg = new AlarmAggregationEngine();

        agg.RegisterAlarmHierarchy("ALM-ROOT", null);
        agg.RegisterAlarmHierarchy("ALM-CHILD-1", "ALM-ROOT");
        agg.RegisterAlarmHierarchy("ALM-GRANDCHILD", "ALM-CHILD-1");

        Assert.Equal(0, agg.GetRootCauseDepth("ALM-ROOT"));
        Assert.Equal(1, agg.GetRootCauseDepth("ALM-CHILD-1"));
        Assert.Equal(2, agg.GetRootCauseDepth("ALM-GRANDCHILD"));
    }

    [Fact]
    public void Aggregation_GetRootCausePath_ReturnsPath()
    {
        var agg = new AlarmAggregationEngine();

        agg.RegisterAlarmHierarchy("ALM-ROOT", null);
        agg.RegisterAlarmHierarchy("ALM-CHILD", "ALM-ROOT");
        agg.RegisterAlarmHierarchy("ALM-GRANDCHILD", "ALM-CHILD");

        var path = agg.GetRootCausePath("ALM-GRANDCHILD");
        Assert.Equal(new[] { "ALM-GRANDCHILD", "ALM-CHILD", "ALM-ROOT" }, path);
    }

    [Fact]
    public void Aggregation_GetDescendantAlarms_BFS()
    {
        var agg = new AlarmAggregationEngine();

        agg.RegisterAlarmHierarchy("ROOT", null);
        agg.RegisterAlarmHierarchy("C1", "ROOT");
        agg.RegisterAlarmHierarchy("C2", "ROOT");
        agg.RegisterAlarmHierarchy("GC1", "C1");

        var descendants = agg.GetDescendantAlarms("ROOT").ToList();
        Assert.Equal(3, descendants.Count);
        Assert.Contains("C1", descendants);
        Assert.Contains("C2", descendants);
        Assert.Contains("GC1", descendants);
    }

    [Fact]
    public void Aggregation_RecoverTree_RemovesAll()
    {
        var agg = new AlarmAggregationEngine();

        agg.RegisterAlarmHierarchy("ROOT", null);
        agg.RegisterAlarmHierarchy("C1", "ROOT");
        agg.RegisterAlarmHierarchy("C2", "ROOT");

        var recovered = agg.RecoverTree("ROOT");

        Assert.Equal(3, recovered.Count);
        Assert.Contains("ROOT", recovered);
        Assert.Contains("C1", recovered);
        Assert.Contains("C2", recovered);

        // After recovery, no depth info remains
        Assert.Equal(0, agg.GetRootCauseDepth("ROOT"));
    }

    // ========== 查询方法 ==========

    [Fact]
    public void GetAlarmsByLevel_FiltersCorrectly()
    {
        var (ac, _) = CreateAlarmCenter();
        ac.SetAlarmRule(new AlarmRule { AlarmCode = "ERR", Level = AlarmLevelEnum.Error, DelayRaiseMs = 10 });
        ac.SetAlarmRule(new AlarmRule { AlarmCode = "WARN", Level = AlarmLevelEnum.Warning, DelayRaiseMs = 10 });

        ac.RaiseAlarmAsync("ERR", AlarmLevelEnum.Error, "Error").GetAwaiter().GetResult();
        ac.RaiseAlarmAsync("WARN", AlarmLevelEnum.Warning, "Warning").GetAwaiter().GetResult();
        Thread.Sleep(50);

        var errors = ac.GetAlarmsByLevel(AlarmLevelEnum.Error).ToList();
        Assert.NotEmpty(errors);
        Assert.All(errors, a => Assert.Equal(AlarmLevelEnum.Error, a.Level));
    }

    [Fact]
    public void GetAlarmsByTimeRange_FiltersCorrectly()
    {
        var (ac, _) = CreateAlarmCenter();
        ac.SetAlarmRule(new AlarmRule { AlarmCode = "T1", Level = AlarmLevelEnum.Info, DelayRaiseMs = 10 });
        ac.SetAlarmRule(new AlarmRule { AlarmCode = "T2", Level = AlarmLevelEnum.Info, DelayRaiseMs = 10 });

        ac.RaiseAlarmAsync("T1", AlarmLevelEnum.Info, "Event 1").GetAwaiter().GetResult();
        ac.RaiseAlarmAsync("T2", AlarmLevelEnum.Info, "Event 2").GetAwaiter().GetResult();
        Thread.Sleep(50);

        var from = DateTime.UtcNow.AddMinutes(-1);
        var to = DateTime.UtcNow.AddMinutes(1);

        var alarms = ac.GetAlarmsByTimeRange(from, to).ToList();
        Assert.NotEmpty(alarms);
    }

    [Fact]
    public void GetTotalCount_ReturnsCorrect()
    {
        var (ac, _) = CreateAlarmCenter();
        ac.SetAlarmRule(new AlarmRule { AlarmCode = "C1", Level = AlarmLevelEnum.Info, DelayRaiseMs = 10 });
        ac.SetAlarmRule(new AlarmRule { AlarmCode = "C2", Level = AlarmLevelEnum.Info, DelayRaiseMs = 10 });

        ac.RaiseAlarmAsync("C1", AlarmLevelEnum.Info, "Count 1").GetAwaiter().GetResult();
        ac.RaiseAlarmAsync("C2", AlarmLevelEnum.Info, "Count 2").GetAwaiter().GetResult();
        Thread.Sleep(50);

        Assert.Equal(2, ac.GetTotalCount());
    }

    [Fact]
    public void IsInStormMode_InitiallyFalse()
    {
        var (ac, _) = CreateAlarmCenter();
        Assert.False(ac.IsInStormMode);
    }

    // ========== AlarmCenter Snapshot ==========

    [Fact]
    public async Task CaptureSnapshot_ReturnsAlarmAndRules()
    {
        var (ac, _) = CreateAlarmCenter();
        ac.SetAlarmRule(new AlarmRule { AlarmCode = "TEST", Level = AlarmLevelEnum.Error, DelayRaiseMs = 10 });
        await ac.RaiseAlarmAsync("TEST", AlarmLevelEnum.Error, "Snapshot test");
        await Task.Delay(50);

        var snap = await ac.CaptureSnapshotAsync(default);
        Assert.NotNull(snap);
    }

    [Fact]
    public void GetRootCauseDepth_NotInTree_ReturnsZero()
    {
        var agg = new AlarmAggregationEngine();
        Assert.Equal(0, agg.GetRootCauseDepth("NONEXISTENT"));
    }

    [Fact]
    public void GetDescendantAlarms_NoChildren_ReturnsEmpty()
    {
        var agg = new AlarmAggregationEngine();
        agg.RegisterAlarmHierarchy("ROOT", null);
        Assert.Empty(agg.GetDescendantAlarms("ROOT"));
    }

    [Fact]
    public void GetRootCausePath_UnknownAlarm_ReturnsSelf()
    {
        var agg = new AlarmAggregationEngine();
        var path = agg.GetRootCausePath("UNKNOWN");
        Assert.Single(path);
        Assert.Equal("UNKNOWN", path[0]);
    }
}
