namespace Wcs.Core.StateCenter.Features;

using System.Collections.Concurrent;

/// <summary>
/// 状态变更类型
/// </summary>
public enum StateChangeType
{
    Added,
    Updated,
    Removed
}

/// <summary>
/// 单条状态变更记录
/// </summary>
/// <param name="ChangeType">变更类型</param>
/// <param name="Key">状态键</param>
/// <param name="OldValue">旧值（Added 时为 null）</param>
/// <param name="NewValue">新值（Removed 时为 null）</param>
public record StateChangeRecord(
    StateChangeType ChangeType,
    string Key,
    object? OldValue,
    object? NewValue
);

/// <summary>
/// 批量更新作用域 — 在作用域内的多次状态更新合并为一次通知
/// 通过 AsyncLocal 实现，无需方法间显式传参
/// </summary>
public interface IBatchScope : IDisposable
{
    IReadOnlyList<StateChangeRecord> Changes { get; }
    void AddChange(StateChangeRecord change);
}

public class BatchScope : IBatchScope
{
    private static readonly AsyncLocal<Stack<BatchScope>> _scopeStack = new();

    private readonly List<StateChangeRecord> _changes = new();
    private bool _disposed;

    public IReadOnlyList<StateChangeRecord> Changes => _changes.AsReadOnly();

    private static Stack<BatchScope> GetStack()
    {
        var stack = _scopeStack.Value;
        if (stack == null)
        {
            stack = new Stack<BatchScope>();
            _scopeStack.Value = stack;
        }
        return stack;
    }

    /// <summary>
    /// 当前是否在批量作用域内
    /// </summary>
    public static bool IsInBatch => GetStack().Count > 0;

    /// <summary>
    /// 获取当前批量作用域（未在批量中时返回 null）
    /// </summary>
    public static BatchScope? Current => GetStack().Count > 0 ? GetStack().Peek() : null;

    public void AddChange(StateChangeRecord change)
    {
        _changes.Add(change);
    }

    /// <summary>
    /// 开始批量更新作用域
    /// </summary>
    public static BatchScope Begin()
    {
        var scope = new BatchScope();
        GetStack().Push(scope);
        return scope;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var stack = GetStack();
        if (stack.Count > 0 && stack.Peek() == this)
        {
            stack.Pop();
        }
    }
}
