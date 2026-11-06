using System.Runtime.CompilerServices;

namespace SRdeckPlugin.Sdk;

/// <summary>
/// Tracks the IQ generation associated with plugin-produced audio so stream
/// changes can mark the first following frame as discontinuous.
/// </summary>
public sealed class PluginAudioGenerationTracker
{
    private long generation = long.MinValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Observe(long currentGeneration, bool discontinuous = false)
    {
        bool requiresReset = discontinuous || generation != currentGeneration;
        generation = currentGeneration;
        return requiresReset;
    }

    public void Reset() => generation = long.MinValue;
}
