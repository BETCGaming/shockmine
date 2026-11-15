using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ShockMine;

/// <summary>
/// Entry point for the Shock Mine.
///
/// Deliberately few config entries: the two knobs players argue about — whether a hole allows
/// more than one mine, and how long a seize lasts — are match setup rules set by the host, so
/// every player in a lobby agrees on them without config sync. What is left here is the handful
/// of things that have no host-rule home.
/// </summary>
[BepInAutoPlugin]
public partial class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;

    internal static ConfigEntry<bool> Enabled { get; private set; } = null!;
    internal static ConfigEntry<bool> BlockUnmodded { get; private set; } = null!;
    internal static ConfigEntry<float> SoundVolume { get; private set; } = null!;
    internal static ConfigEntry<bool> DebugHotkeys { get; private set; } = null!;
    internal static ConfigEntry<bool> VerboseLogging { get; private set; } = null!;

    private void Awake()
    {
        Log = Logger;

        Enabled = Config.Bind(
            "General",
            "Enabled",
            true,
            "Enable the Shock Mine. When off, the item is not registered and never spawns.");

        SoundVolume = Config.Bind(
            "General",
            "SoundVolume",
            0.8f,
            new ConfigDescription(
                "Volume of the zapping loop played while a player is seized.",
                new AcceptableValueRange<float>(0f, 1f)));

        BlockUnmodded = Config.Bind(
            "Host",
            "BlockUnmodded",
            true,
            "Disconnect players who do not have this mod installed. They cannot handle the "
            + "Shock Mine if the item pool hands them one. Host setting; ignored on clients.");

        DebugHotkeys = Config.Bind(
            "Debug",
            "DebugHotkeys",
            false,
            "F7 gives the local player a Shock Mine, F8 trips the nearest one. Host only, "
            + "for testing.");

        VerboseLogging = Config.Bind(
            "Debug",
            "VerboseLogging",
            false,
            "Log every step of planting a mine, tripping it and releasing its victims. For "
            + "diagnosing a mine that never fires or never lets go.");

        MirrorSerializers.Register();
        ShockLocalization.Initialize();

        new Harmony(Id).PatchAll();
        gameObject.AddComponent<ModRunner>();

        Log.LogInfo($"Plugin {Name} v{Version} is loaded!");
    }
}
