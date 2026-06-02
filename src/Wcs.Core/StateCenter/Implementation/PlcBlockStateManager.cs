namespace Wcs.Core.StateCenter.Implementation;

using System.Collections.Concurrent;
using Wcs.Core.StateCenter.Models;

/// <summary>
/// PLC 数据块状态管理器
/// </summary>
public class PlcBlockStateManager
{
    private readonly ConcurrentDictionary<string, PlcBlockState> _plcBlockStates = new();

    public void UpdatePlcBlockState(string blockName, PlcBlockState state)
    {
        ArgumentNullException.ThrowIfNull(blockName);
        ArgumentNullException.ThrowIfNull(state);

        _plcBlockStates.AddOrUpdate(blockName, state, (_, _) => state);
    }

    public PlcBlockState? GetPlcBlockState(string blockName)
    {
        _plcBlockStates.TryGetValue(blockName, out var state);
        return state;
    }

    public IEnumerable<PlcBlockState> GetAllPlcBlockStates()
        => _plcBlockStates.Values.ToList();

    public Dictionary<string, PlcBlockState> GetSnapshot()
        => new(_plcBlockStates);

    public void RestoreFromSnapshot(Dictionary<string, PlcBlockState> snapshot)
    {
        _plcBlockStates.Clear();
        foreach (var kvp in snapshot)
            _plcBlockStates.TryAdd(kvp.Key, kvp.Value);
    }

    public void Clear() => _plcBlockStates.Clear();

    public int Count => _plcBlockStates.Count;
}
