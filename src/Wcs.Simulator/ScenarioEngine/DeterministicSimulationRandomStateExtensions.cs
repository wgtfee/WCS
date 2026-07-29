namespace Wcs.Simulator.ScenarioEngine;

using System.Runtime.CompilerServices;
using Wcs.Simulator.Governance;

internal static class DeterministicSimulationRandomStateExtensions
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_state")]
    private static extern ref ulong GetState(DeterministicSimulationRandom instance);

    public static ulong CaptureState(this DeterministicSimulationRandom random)
    {
        ArgumentNullException.ThrowIfNull(random);
        return GetState(random);
    }

    public static void RestoreState(this DeterministicSimulationRandom random, ulong state)
    {
        ArgumentNullException.ThrowIfNull(random);
        GetState(random) = state;
    }
}
