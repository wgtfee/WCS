namespace Wcs.Core.PlcSubsystem.S7;

using System.Reflection;

/// <summary>
/// 单个字段的变化记录
/// </summary>
public class StructFieldChange
{
    public string FieldName { get; set; } = string.Empty;
    public object? OldValue { get; set; }
    public object? NewValue { get; set; }
    public bool HasChanged => !Equals(OldValue, NewValue);
}

/// <summary>
/// 结构体变化结果
/// </summary>
public class StructDiffResult
{
    public bool HasChanges => Changes.Any(c => c.HasChanged);
    public List<StructFieldChange> Changes { get; set; } = new();
    public DateTime CompareTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 结构体 Diff 引擎 — 比较两个同类型 struct 实例的字段级差异
///
/// 用法：
///   var old = Struct.FromBytes<DB1_Struct>(bytes1);
///   var cur = Struct.FromBytes<DB1_Struct>(bytes2);
///   var diff = StructDiffEngine.Compare(old, cur);
///   // diff.Changes 包含所有变化的字段名、旧值、新值
///   // diff.HasChanges → true 表示有字段变化
/// </summary>
public static class StructDiffEngine
{
    /// <summary>
    /// 比较两个同类型对象的字段差异
    /// </summary>
    public static StructDiffResult Compare<T>(T? oldObj, T? newObj) where T : class
    {
        var result = new StructDiffResult();

        if (oldObj == null && newObj == null)
            return result;

        if (oldObj == null)
        {
            // 全部为新字段
            foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                result.Changes.Add(new StructFieldChange
                {
                    FieldName = field.Name,
                    OldValue = null,
                    NewValue = field.GetValue(newObj)
                });
            }
            return result;
        }

        if (newObj == null)
        {
            foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                result.Changes.Add(new StructFieldChange
                {
                    FieldName = field.Name,
                    OldValue = field.GetValue(oldObj),
                    NewValue = null
                });
            }
            return result;
        }

        foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var oldVal = field.GetValue(oldObj);
            var newVal = field.GetValue(newObj);

            if (!Equals(oldVal, newVal))
            {
                result.Changes.Add(new StructFieldChange
                {
                    FieldName = field.Name,
                    OldValue = oldVal,
                    NewValue = newVal
                });
            }
        }

        return result;
    }
}
