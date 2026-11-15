using System.Collections.Generic;
using FMOD.Studio;
using FMODUnity;
using UnityEngine;

namespace ShockMine;

/// <summary>
/// What a seize sounds like: the lightning strike's own thunderclap as the victim is caught,
/// then an electrical hum held over them until they are let go.
///
/// The game has no looping zap event to borrow — its thunderstorm audio is
/// <c>ThunderstormLightningStrikeEvent</c>, a one-shot clap — and repeating a thunderclap four
/// times over five seconds would be unbearable. The Electromagnet's shield hum is the closest
/// thing the game ships to a continuous electrical sound, so the clap marks the moment and the
/// hum carries the hold.
/// </summary>
internal static class ShockAudio
{
    private static readonly Dictionary<uint, EventInstance> Hums = new();

    /// <summary>The thunderclap, at the moment of being caught.</summary>
    public static void PlayCatch(Vector3 position)
    {
        EventReference strike = GameManager.AudioSettings.ThunderstormLightningStrikeEvent;
        if (strike.IsNull)
        {
            return;
        }

        RuntimeManager.PlayOneShot(strike, position);
    }

    /// <summary>Starts the hum over a victim, if one is not already running.</summary>
    public static void BeginHum(uint victimNetId, Vector3 position)
    {
        if (Hums.ContainsKey(victimNetId))
        {
            return;
        }

        EventReference hum = GameManager.AudioSettings.ElectromagnetShieldHumEvent;
        if (hum.IsNull)
        {
            return;
        }

        EventInstance instance = RuntimeManager.CreateInstance(hum);
        instance.set3DAttributes(position.To3DAttributes());
        instance.setVolume(Plugin.SoundVolume.Value);
        instance.start();

        // Deliberately not released here, unlike the game's fire-and-forget one-shots: the
        // handle is what lets the hold be stopped when the victim is let go.
        Hums[victimNetId] = instance;
    }

    /// <summary>Keeps a running hum positioned on its victim.</summary>
    public static void Follow(uint victimNetId, Vector3 position)
    {
        if (Hums.TryGetValue(victimNetId, out EventInstance instance) && instance.isValid())
        {
            instance.set3DAttributes(position.To3DAttributes());
        }
    }

    public static void EndHum(uint victimNetId)
    {
        if (!Hums.TryGetValue(victimNetId, out EventInstance instance))
        {
            return;
        }

        Hums.Remove(victimNetId);
        Stop(instance);
    }

    /// <summary>Drops every hum, for a disconnect or a scene change.</summary>
    public static void Reset()
    {
        foreach (KeyValuePair<uint, EventInstance> entry in Hums)
        {
            Stop(entry.Value);
        }

        Hums.Clear();
    }

    private static void Stop(EventInstance instance)
    {
        if (!instance.isValid())
        {
            return;
        }

        instance.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);
        instance.release();
    }
}
