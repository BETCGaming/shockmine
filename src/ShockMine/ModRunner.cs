using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ShockMine;

/// <summary>
/// The plugin's single persistent behaviour: keeps the runtime registrations topped up and
/// provides the testing hotkeys.
/// </summary>
internal sealed class ModRunner : MonoBehaviour
{
    /// <summary>Lets other parts of the mod start coroutines from static code.</summary>
    internal static ModRunner? Instance { get; private set; }

    private void Awake() => Instance = this;

    private void FixedUpdate() => ShockLocalVictim.FixedTick();

    private void Update()
    {
        ShockNetwork.EnsureHandlers();
        ShockLocalization.EnsureApplied();
        ShockItem.EnsureNetworkPrefabRegistered();
        ShockEquipment.EnsureRegistered();
        ShockVfx.Tick();
        ShockUi.Tick();
        ShockLocalVictim.Tick();
        ModGate.Tick();

        if (!Plugin.DebugHotkeys.Value || Keyboard.current is null)
        {
            return;
        }

        if (Keyboard.current[Key.F7].wasPressedThisFrame)
        {
            GiveMine();
        }

        if (Keyboard.current[Key.F8].wasPressedThisFrame)
        {
            TripNearestMine();
        }
    }

    /// <summary>
    /// Springs the nearest mine without having to walk into it, which is the only way to watch
    /// a seize from outside one.
    /// </summary>
    private static void TripNearestMine()
    {
        if (!NetworkServer.active)
        {
            Plugin.Log.LogWarning("F8 ignored: only the host can trip a mine.");
            return;
        }

        PlayerInfo? local = GameManager.LocalPlayerInfo;
        if (local == null)
        {
            Plugin.Log.LogWarning("F8 ignored: no local player yet.");
            return;
        }

        Landmine? nearest = null;
        float nearestDistance = float.MaxValue;

        foreach (ShockMarker marker in FindObjectsByType<ShockMarker>(FindObjectsSortMode.None))
        {
            if (!marker.TryGetComponent(out Landmine landmine)
                || landmine.GetComponent<ShockSeizeWindow>() != null)
            {
                continue;
            }

            float distance = (marker.transform.position - local.transform.position).sqrMagnitude;
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = landmine;
            }
        }

        if (nearest == null)
        {
            Plugin.Log.LogInfo("F8: no unsprung Shock Mine in the scene.");
            return;
        }

        ShockSeizeWindow.Begin(nearest);
        Plugin.Log.LogInfo($"F8: sprung a Shock Mine {Mathf.Sqrt(nearestDistance):0.#}m away.");
    }

    private static void GiveMine()
    {
        if (!NetworkServer.active)
        {
            Plugin.Log.LogWarning("F7 ignored: only the host can hand out items directly.");
            return;
        }

        PlayerInventory? inventory = GameManager.LocalPlayerInventory;
        if (inventory == null)
        {
            Plugin.Log.LogWarning("F7 ignored: no local player inventory yet.");
            return;
        }

        bool given = inventory.ServerTryAddItem(ShockItem.Type, remainingUses: 1);
        Plugin.Log.LogInfo(given
            ? "Gave the local player a Shock Mine."
            : "Could not give a Shock Mine (inventory full?).");
    }
}
