namespace Wcs.Simulator.Tests;

using Wcs.FeatureCenter;

public sealed class FeatureCenterContractTests
{
    [Fact] public void Definition_hash_is_deterministic(){var a=D("health.latest");var b=D("health.latest");Assert.Equal(a.DefinitionHash,b.DefinitionHash);Assert.Equal(64,a.DefinitionHash.Length);}
    [Fact] public void Unit_change_changes_definition_hash()=>Assert.NotEqual(D("x",unit:"score").DefinitionHash,D("x",unit:"ratio").DefinitionHash);
    [Fact] public void Window_change_changes_definition_hash()=>Assert.NotEqual(D("x",window:TimeSpan.FromHours(1)).DefinitionHash,D("x",window:TimeSpan.FromHours(2)).DefinitionHash);
    [Fact] public void Schema_order_change_changes_hash(){var a=D("a");var b=D("b");var s1=FeatureSchema.Create("schema","1",[new(a.FeatureId,a.DefinitionHash,0),new(b.FeatureId,b.DefinitionHash,1)]);var s2=FeatureSchema.Create("schema","1",[new(b.FeatureId,b.DefinitionHash,0),new(a.FeatureId,a.DefinitionHash,1)]);Assert.NotEqual(s1.SchemaHash,s2.SchemaHash);}
    [Fact] public void Schema_rejects_duplicate_feature_ids(){var d=D("a");Assert.Throws<ArgumentException>(()=>FeatureSchema.Create("s","1",[new("a",d.DefinitionHash,0),new("a",d.DefinitionHash,1)]));}
    [Fact] public void Schema_rejects_non_contiguous_ordinals(){var d=D("a");Assert.Throws<ArgumentException>(()=>FeatureSchema.Create("s","1",[new("a",d.DefinitionHash,1)]));}
    [Fact] public void Stale_value_is_marked_stale(){var d=D("x",freshness:TimeSpan.FromMinutes(1));var now=DateTimeOffset.UtcNow;Assert.Equal(FeatureQualityStatus.Stale,new FeatureQualityValidator().Validate(d,50d,now.AddMinutes(-2),now).QualityStatus);}
    [Fact] public void Null_fail_is_missing(){var d=D("x",nullPolicy:FeatureNullPolicy.Fail);Assert.Equal(FeatureQualityStatus.Missing,new FeatureQualityValidator().Validate(d,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow).QualityStatus);}
    [Fact] public void Null_default_is_valid_and_uses_default(){var d=FeatureDefinition.Create("x","x","Asset",FeatureDataType.Double,"score","src","latest",TimeSpan.Zero,TimeSpan.FromMinutes(5),FeatureNullPolicy.Default,"12.5",0,100,"1","owner");var r=new FeatureQualityValidator().Validate(d,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);Assert.Equal(FeatureQualityStatus.Valid,r.QualityStatus);Assert.Equal("12.5",r.Value);}
    [Fact] public void Null_ignore_is_valid(){var d=D("x",nullPolicy:FeatureNullPolicy.Ignore);Assert.Equal(FeatureQualityStatus.Valid,new FeatureQualityValidator().Validate(d,null,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow).QualityStatus);}
    [Fact] public void Out_of_range_value_is_detected()=>Assert.Equal(FeatureQualityStatus.OutOfRange,new FeatureQualityValidator().Validate(D("x"),101d,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow).QualityStatus);
    [Fact] public void Catalog_contains_documented_initial_features(){var ids=FeatureCatalogV1.CreateDefault().Select(x=>x.FeatureId).ToHashSet(StringComparer.Ordinal);Assert.Equal(16,ids.Count);Assert.Contains("health.latest",ids);Assert.Contains("fusionRisk.maximum",ids);Assert.Contains("alarm.activeCount",ids);Assert.Contains("vehicle.busyRatio",ids);Assert.Contains("maintenance.hoursSinceLast",ids);}
    [Fact] public async Task Registry_rejects_same_version_different_hash(){var r=new InMemoryFeatureDefinitionRegistry();await r.RegisterAsync(D("x",unit:"score"),default);await Assert.ThrowsAsync<InvalidOperationException>(()=>r.RegisterAsync(D("x",unit:"ratio"),default));}
    [Fact] public async Task Schema_registry_requires_registered_definition_hash(){var definitions=new InMemoryFeatureDefinitionRegistry();var schemas=new InMemoryFeatureSchemaRegistry(definitions);var d=D("x");var schema=FeatureSchema.Create("s","1",[new("x",d.DefinitionHash,0)]);await Assert.ThrowsAsync<InvalidOperationException>(()=>schemas.RegisterAsync(schema,default));}
    [Fact] public async Task Snapshot_hash_is_deterministic(){var d=D("x");var schema=FeatureSchema.Create("s","1",[new("x",d.DefinitionHash,0)]).Approve("tester",DateTimeOffset.Parse("2026-01-01T00:00:00Z"));var at=DateTimeOffset.Parse("2026-01-02T00:00:00Z");var values=new[]{new FeatureValue("x",12.5d,FeatureQualityStatus.Valid,at)};var service=new FeatureSnapshotService();var a=await service.FreezeAsync("asset-1",at,schema,values,[],"m1",default);var b=await service.FreezeAsync("asset-1",at,schema,values,[],"m1",default);Assert.Equal(a.ValuesHash,b.ValuesHash);Assert.Equal(a.SnapshotId,b.SnapshotId);}
    [Fact] public async Task Snapshot_rejects_future_feature_value(){var d=D("x");var schema=FeatureSchema.Create("s","1",[new("x",d.DefinitionHash,0)]).Approve("tester",DateTimeOffset.UtcNow);var at=DateTimeOffset.UtcNow;await Assert.ThrowsAsync<InvalidOperationException>(()=>new FeatureSnapshotService().FreezeAsync("asset",at,schema,[new("x",1d,FeatureQualityStatus.Valid,at.AddSeconds(1))],[],"m1",default));}
    [Fact] public async Task Formal_snapshot_requires_approved_schema(){var d=D("x");var schema=FeatureSchema.Create("s","1",[new("x",d.DefinitionHash,0)]);var at=DateTimeOffset.UtcNow;await Assert.ThrowsAsync<InvalidOperationException>(()=>new FeatureSnapshotService().FreezeAsync("asset",at,schema,[new("x",1d,FeatureQualityStatus.Valid,at)],[],"m1",default));}
    [Fact] public void Pit_rule_rejects_future_source_value(){var at=DateTimeOffset.Parse("2026-01-01T00:00:00Z");var row=new FeatureDatasetRow("asset",at,new Dictionary<string,object?>());Assert.Throws<InvalidOperationException>(()=>PointInTimeRules.ValidateRow(row,[new("x",1d,FeatureQualityStatus.Valid,at.AddTicks(1))]));}
    [Fact] public void Pit_rule_rejects_outcome_at_or_before_asof(){var at=DateTimeOffset.Parse("2026-01-01T00:00:00Z");var row=new FeatureDatasetRow("asset",at,new Dictionary<string,object?>(),at);Assert.Throws<InvalidOperationException>(()=>PointInTimeRules.ValidateRow(row,[]));}
    [Fact] public void Bounded_limits_reject_excessive_dataset_rows()=>Assert.Throws<ArgumentOutOfRangeException>(()=>new FeatureCenterLimits{MaximumDatasetRows=50_000_001}.Validate());

    [Fact]
    public async Task Realtime_cache_replays_older_value_after_newer_observation_arrives()
    {
        var d=D("x",freshness:TimeSpan.FromHours(2));
        var schema=FeatureSchema.Create("s","1",[new("x",d.DefinitionHash,0)]).Approve("tester",DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var cache=new BoundedFeatureRealtimeCache([d]);
        var t1=DateTimeOffset.Parse("2026-01-01T01:00:00Z"); var t2=t1.AddHours(1);
        await cache.ApplyAsync(new("asset","x",10d,t1,"events","1"),default);
        await cache.ApplyAsync(new("asset","x",20d,t2,"events","2"),default);
        var old=await cache.ReadAsOfAsync("asset",schema,t1.AddMinutes(1),default);
        var current=await cache.ReadAsOfAsync("asset",schema,t2.AddMinutes(1),default);
        Assert.Equal(10d,old.Single().Value); Assert.Equal(20d,current.Single().Value);
    }

    private static FeatureDefinition D(string id,string unit="score",TimeSpan? window=null,TimeSpan? freshness=null,FeatureNullPolicy nullPolicy=FeatureNullPolicy.Fail)=>FeatureDefinition.Create(id,id,"Asset",FeatureDataType.Double,unit,"src","latest",window??TimeSpan.Zero,freshness??TimeSpan.FromMinutes(5),nullPolicy,null,0,100,"1","owner");
}
