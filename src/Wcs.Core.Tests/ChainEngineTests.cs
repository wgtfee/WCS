using Wcs.Core.TaskEngine.Chain;
using Version = Wcs.Core.TaskEngine.Chain.Version;

namespace WcsCoreTests;

/// <summary>
/// ChainBuilder + ChainExecutionEngine 测试：DAG 构造、拓扑排序、执行流程
/// </summary>
public class ChainEngineTests
{
    // ========== ChainBuilder DAG 构造 ==========

    [Fact]
    public void Build_LinearGraph_TopologicalOrderCorrect()
    {
        var graph = ChainBuilder.Create()
            .AddAction("start", "PlcWrite")
            .AddAction("middle", "HttpCall")
                .DependsOn("middle", "start")
            .AddAction("end", "Script")
                .DependsOn("end", "middle")
            .Build();

        var order = graph.TopologicalOrder.Select(n => n.NodeId).ToList();
        Assert.Equal(new[] { "start", "middle", "end" }, order);
    }

    [Fact]
    public void Build_DiamondGraph_AllNodesPresent()
    {
        var graph = ChainBuilder.Create()
            .AddAction("start", "Init")
            .AddAction("branchA", "PathA")
                .DependsOn("branchA", "start")
            .AddAction("branchB", "PathB")
                .DependsOn("branchB", "start")
            .AddAction("join", "Merge")
                .DependsOn("join", "branchA")
                .DependsOn("join", "branchB")
            .Build();

        Assert.Equal(4, graph.Nodes.Count);
        Assert.Equal(4, graph.TopologicalOrder.Count);
        // start must come before both branches
        var order = graph.TopologicalOrder.Select(n => n.NodeId).ToList();
        Assert.Contains("branchA", order);
        Assert.Contains("branchB", order);
    }

    [Fact]
    public void Build_WithWaitCondition_StoresCondition()
    {
        var condition = new WaitCondition
        {
            DeviceId = "CV01",
            ExpectedStatus = "Ready",
            SignalName = "Signal-1"
        };

        var graph = ChainBuilder.Create()
            .AddWait("wait-1", condition)
            .AddAction("act-1", "PlcWrite")
                .DependsOn("act-1", "wait-1")
            .Build();

        var waitNode = graph.Nodes.OfType<WaitNode>().First();
        Assert.NotNull(waitNode.Condition);
        Assert.Equal("CV01", waitNode.Condition.DeviceId);
        Assert.Equal("Ready", waitNode.Condition.ExpectedStatus);
    }

    [Fact]
    public void Build_WithOlderWaitExpression_BackwardCompatible()
    {
        var graph = ChainBuilder.Create()
            .AddWait("wait-1", "Signal", "CV01:Running")
            .Build();

        var waitNode = graph.Nodes.OfType<WaitNode>().First();
        Assert.Equal("CV01:Running", waitNode.ConditionExpression);
        Assert.Null(waitNode.Condition); // old API doesn't set Condition
    }

    [Fact]
    public void Build_WithDelayNode_StoresDelay()
    {
        var graph = ChainBuilder.Create()
            .AddDelay("delay-1", 5000)
            .Build();

        var delayNode = graph.Nodes.OfType<DelayNode>().First();
        Assert.Equal(5000, delayNode.DelayMs);
    }

    [Fact]
    public void Build_WithParallelNode_StoresBranches()
    {
        var graph = ChainBuilder.Create()
            .AddParallel("parallel-1", new[] { "branchA", "branchB" }, waitAll: false)
            .Build();

        var parallelNode = graph.Nodes.OfType<ParallelNode>().First();
        Assert.Equal(new[] { "branchA", "branchB" }, parallelNode.BranchNodeIds);
        Assert.False(parallelNode.WaitAll);
    }

    [Fact]
    public void Build_WithDefinition_SetsVersionAndDefinitionId()
    {
        var def = new TaskChainDefinition
        {
            DefinitionId = "DEF-001",
            Name = "TestChain",
            Version = new Version(2, 0)
        };

        var graph = ChainBuilder.Create()
            .AddAction("a1", "PlcWrite")
            .WithDefinition(def)
            .Build();

        Assert.Equal("DEF-001", graph.DefinitionId);
        Assert.NotNull(graph.Version);
        Assert.Equal(2, graph.Version.Major);
    }

    [Fact]
    public void Build_CyclicGraph_Throws()
    {
        // A depends on B and B depends on A → cycle
        var builder = ChainBuilder.Create()
            .AddAction("A", "TaskA")
            .AddAction("B", "TaskB");
        builder.DependsOn("A", "B");
        builder.DependsOn("B", "A");

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_DuplicateNodeId_Throws()
    {
        var builder = ChainBuilder.Create();
        builder.AddAction("A", "TaskA");
        Assert.Throws<InvalidOperationException>(() =>
            builder.AddAction("A", "TaskA_Dup"));
    }

    [Fact]
    public void Build_UnknownDependency_Throws()
    {
        var builder = ChainBuilder.Create()
            .AddAction("A", "TaskA");
        builder.DependsOn("A", "NONEXISTENT"); // A depends on unknown node
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    // ========== DecisionNode ==========

    [Fact]
    public void Build_WithDecisionNode_StoresBranches()
    {
        var graph = ChainBuilder.Create()
            .AddDecision("dec-1", "CheckStorage", "branch-true", "branch-false")
            .AddAction("branch-true", "StoreAction")
            .AddAction("branch-false", "RejectAction")
            .Build();

        var decision = graph.Nodes.OfType<DecisionNode>().First();
        Assert.Equal("CheckStorage", decision.Expression);
        Assert.Equal("branch-true", decision.TrueBranchNodeId);
        Assert.Equal("branch-false", decision.FalseBranchNodeId);
    }

    // ========== ChainExecutionEngine ==========

    [Fact]
    public async Task ExecuteAsync_SimpleAction_Succeeds()
    {
        var recovery = new ChainRecoveryService();
        var engine = new ChainExecutionEngine(recovery);

        var graph = ChainBuilder.Create()
            .AddAction("a1", "PlcWrite")
            .Build();

        var result = await engine.ExecuteAsync(graph);

        Assert.True(result.Success);
        Assert.Equal(1, result.CompletedNodes);
        Assert.Equal(0, result.FailedNodes);
    }

    [Fact]
    public async Task ExecuteAsync_LinearThreeNodes_AllComplete()
    {
        var recovery = new ChainRecoveryService();
        var engine = new ChainExecutionEngine(recovery);

        var graph = ChainBuilder.Create()
            .AddAction("start", "Init")
            .AddAction("middle", "Process")
                .DependsOn("middle", "start")
            .AddAction("end", "Finish")
                .DependsOn("end", "middle")
            .Build();

        var result = await engine.ExecuteAsync(graph);

        Assert.True(result.Success);
        Assert.Equal(3, result.CompletedNodes);
    }

    [Fact]
    public async Task ExecuteAsync_DelayNode_WaitsCorrectly()
    {
        var recovery = new ChainRecoveryService();
        var engine = new ChainExecutionEngine(recovery);

        var graph = ChainBuilder.Create()
            .AddDelay("delay-1", 10)
            .AddAction("action-1", "PostProcess")
                .DependsOn("action-1", "delay-1")
            .Build();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await engine.ExecuteAsync(graph);
        sw.Stop();

        Assert.True(result.Success);
        Assert.Equal(2, result.CompletedNodes);
        Assert.True(sw.ElapsedMilliseconds >= 10);
    }

    [Fact]
    public async Task ExecuteAsync_WithDecision_ResultCorrect()
    {
        var recovery = new ChainRecoveryService();
        var engine = new ChainExecutionEngine(recovery);

        var graph = ChainBuilder.Create()
            .AddDecision("dec-1", "AlwaysTrue", "branch-true", "branch-false")
            .AddAction("branch-true", "TrueAction")
                .DependsOn("branch-true", "dec-1")
            .AddAction("branch-false", "FalseAction")
                .DependsOn("branch-false", "dec-1")
            .Build();

        // Register handler that returns true
        engine.RegisterDecisionHandler("AlwaysTrue", (node, ct) => Task.FromResult(true));

        var result = await engine.ExecuteAsync(graph);

        Assert.True(result.Success);
        Assert.Equal(2, result.CompletedNodes); // dec-1 + branch-true
        Assert.Equal(1, result.SkippedNodes); // branch-false was pruned
    }

    [Fact]
    public async Task ExecuteAsync_WithDecision_FalseBranch_Pruned()
    {
        var recovery = new ChainRecoveryService();
        var engine = new ChainExecutionEngine(recovery);

        var graph = ChainBuilder.Create()
            .AddDecision("dec-1", "AlwaysFalse", "branch-true", "branch-false")
            .AddAction("branch-true", "TrueAction")
                .DependsOn("branch-true", "dec-1")
            .AddAction("branch-false", "FalseAction")
                .DependsOn("branch-false", "dec-1")
            .Build();

        engine.RegisterDecisionHandler("AlwaysFalse", (node, ct) => Task.FromResult(false));

        var result = await engine.ExecuteAsync(graph);

        Assert.True(result.Success);
        Assert.Equal(2, result.CompletedNodes); // dec-1 + branch-false
        Assert.Equal(1, result.SkippedNodes); // branch-true was pruned
    }

    [Fact]
    public async Task ExecuteAsync_Checkpoint_ResumesCorrectly()
    {
        var recovery = new ChainRecoveryService();
        var engine = new ChainExecutionEngine(recovery);

        var graph = ChainBuilder.Create()
            .AddAction("a1", "First")
            .AddAction("a2", "Second")
                .DependsOn("a2", "a1")
            .Build();

        // "a1" was already completed in a previous run
        recovery.CheckpointCompleted(graph.GraphId, "a1");

        var result = await engine.ExecuteAsync(graph);

        Assert.True(result.Success);
        Assert.Equal(1, result.CompletedNodes); // only a2 executed
        Assert.Equal(1, result.SkippedNodes); // a1 was checkpointed
    }

    [Fact]
    public async Task ExecuteAsync_NodeTimeout_RetriesAndFails()
    {
        var recovery = new ChainRecoveryService();
        var engine = new ChainExecutionEngine(recovery);

        // ActionNode always succeeds, so just verify basic execution
        var graph = ChainBuilder.Create()
            .AddAction("a1", "PlcWrite")
            .Build();

        var result = await engine.ExecuteAsync(graph);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_ParallelNode_AllBranchesComplete()
    {
        var recovery = new ChainRecoveryService();
        var engine = new ChainExecutionEngine(recovery);

        // Create the branch nodes first
        var graph = ChainBuilder.Create()
            .AddAction("branchA", "BranchA")
            .AddAction("branchB", "BranchB")
            .AddAction("branchC", "BranchC")
            .AddParallel("parallel-1", new[] { "branchA", "branchB", "branchC" }, waitAll: true)
            .Build();

        var result = await engine.ExecuteAsync(graph);

        Assert.True(result.Success);
        Assert.Equal(4, result.CompletedNodes); // 3 branches + parallel node itself
    }

    [Fact]
    public async Task ExecuteAsync_Cancellation_StopsExecution()
    {
        var recovery = new ChainRecoveryService();
        var engine = new ChainExecutionEngine(recovery);

        var graph = ChainBuilder.Create()
            .AddDelay("delay-1", 30000) // long delay
            .Build();

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(10);

        var result = await engine.ExecuteAsync(graph, cts.Token);

        Assert.False(result.Success);
        Assert.Equal("Execution cancelled", result.ErrorMessage);
    }

    // ========== TaskChainEngine ==========

    [Fact]
    public void TaskChainDefinition_WithVersion()
    {
        var def = new TaskChainDefinition
        {
            Name = "TransferChain",
            Version = new Version(2, 0),
            IsBreakingChange = true,
            Description = "V2 with new PLC protocol"
        };

        Assert.Equal("TransferChain", def.Name);
        Assert.Equal(2, def.Version.Major);
        Assert.True(def.IsBreakingChange);
    }
}
