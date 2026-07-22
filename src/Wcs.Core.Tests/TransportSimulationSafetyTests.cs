using Microsoft.Extensions.DependencyInjection;
using Wcs.Core.AlarmCenter;
using Wcs.Core.EventBus.Publisher;
using Wcs.Core.TransportScheduling;

namespace WcsCoreTests;

public class TransportSimulationSafetyTests
{
    [Fact]
    public async Task CapacityGuard_RejectsDangerousTaskRate()
    {
        using var provider = CreateProvider();
        var service = provider.GetRequiredService<ITransportSimulationService>();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RunCapacityBenchmarkAsync(new TransportCapacityBenchmarkRequest
            {
                Name = "dangerous-capacity",
                VehicleCounts = new[] { 1 },
                TaskRatesPerHour = new[] { 10001 },
                Repetitions = 1,
                Policy = new TransportSimulationPolicy { Name = "baseline" }
            }, "tester"));

        Assert.Contains("任务率", exception.Message, StringComparison.Ordinal);
        Assert.Empty(service.GetBenchmarks());
    }

    [Fact]
    public async Task SimulationHistory_RestoresFromJournalAfterRestart()
    {
        var journal = new InMemoryTransportJournalStore();
        string runId;
        using (var firstProvider = CreateProvider(journal))
        {
            var service = firstProvider.GetRequiredService<ITransportSimulationService>();
            var run = await service.RunAsync(
                CreateScenario(),
                new TransportSimulationPolicy { Name = "baseline" },
                "tester");
            runId = run.RunId;
            Assert.Single(service.GetRuns());
        }

        using var secondProvider = CreateProvider(journal);
        var restoredService = secondProvider.GetRequiredService<ITransportSimulationService>();
        await restoredService.LoadAsync();

        var restored = Assert.Single(restoredService.GetRuns());
        Assert.Equal(runId, restored.RunId);
        Assert.Equal(1, restoredService.GetSummary().RunCount);
    }

    [Fact]
    public async Task QueueSnapshotRead_DoesNotRefreshPriorityAfterTuningChange()
    {
        using var provider = CreateProvider();
        var production = provider.GetRequiredService<ITransportProductionDispatchService>();
        var tuning = provider.GetRequiredService<ITransportProductionTuningService>();
        var enqueued = production.Enqueue(new TransportProductionDispatchRequest
        {
            Request = new TransportDispatchRequest
            {
                RequestId = "SNAPSHOT-01",
                SourceNodeId = "N1",
                DestinationNodeId = "N2",
                Priority = 10
            },
            EnqueuedAtUtc = DateTime.UtcNow.AddMinutes(-10)
        });
        var saved = await tuning.SaveAsync(
            tuning.Current with
            {
                AgingPointsPerMinute = tuning.Current.AgingPointsPerMinute + 20
            },
            tuning.Current.Version,
            "tester");
        Assert.True(saved.Success, saved.Error);

        var snapshot = Assert.Single(production.GetQueue());

        Assert.Equal(enqueued.EffectivePriority, snapshot.EffectivePriority);
        Assert.Equal(enqueued.UpdatedAtUtc, snapshot.UpdatedAtUtc);
        Assert.Equal(enqueued.State, snapshot.State);
        Assert.Equal(enqueued.AttemptCount, snapshot.AttemptCount);
    }

    private static TransportSimulationScenario CreateScenario() => new()
    {
        Name = "restart-history",
        HorizonSeconds = 300,
        Vehicles = new[]
        {
            new TransportSimulationVehicle
            {
                VehicleId = "EMS-01",
                Kind = TransportVehicleKind.Ems,
                Online = true,
                BatteryPercent = 100
            }
        },
        Tasks = new[]
        {
            new TransportSimulationTask
            {
                TaskId = "TASK-01",
                SourceNodeId = "N1",
                DestinationNodeId = "N2",
                EstimatedTravelSeconds = 30,
                ServiceSeconds = 5
            }
        }
    };

    private static ServiceProvider CreateProvider(ITransportJournalStore? journal = null)
    {
        var services = new ServiceCollection();
        if (journal is not null)
            services.AddSingleton(journal);
        services.AddSingleton<IAlarmCenter>(new AlarmCenter(new EventBus()));
        services.AddUnifiedTransportScheduling();
        return services.BuildServiceProvider();
    }
}
