using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace ShockMine;

/// <summary>
/// Drives what happens when a player presses use while holding a Shock Mine.
///
/// The game dispatches item use through GetItemUseRoutine, a local function inside TryUseItem
/// whose switch ends in <c>ThrowSwitchExpressionException</c> for an unrecognised ItemType.
/// Rather than patch a compiler-generated local function, this intercepts TryUseItem itself and
/// runs our own routines — which are deliberately line-for-line mirrors of the game's
/// PlantLandmineRoutine and TossLandmineRoutine, reading their timings, VFX, screenshake and
/// audio from the game's own settings so the mine is handled exactly like the mine it is built
/// from.
/// </summary>
[HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.TryUseItem))]
internal static class ShockUsePatch
{
    private static bool Prefix(
        PlayerInventory __instance,
        bool isAirhornReaction,
        ref bool shouldEatInput,
        ref bool __result)
    {
        shouldEatInput = true;

        if (!__instance.isLocalPlayer)
        {
            return true;
        }

        if (__instance.GetEffectivelyEquippedItem() != ShockItem.Type)
        {
            return true;
        }

        // The lobby's driving range is for warming up, not for shocking each other in.
        if (SingletonBehaviour<DrivingRangeManager>.HasInstance)
        {
            Trace.Log("use refused: driving range");
            ShockUi.ShowDenial("NOT HERE");
            __result = false;
            return false;
        }

        // Reuse the game's own gate so holstering, spectating, respawning, golf carts, dives,
        // cooldowns and the rest still apply. The flourish flag is ignored on purpose: it can
        // only be true for an item whose NonAimUse is Flourish, and the mine inherits the
        // landmine's, which is not.
        if (!__instance.CanUseEquippedItem(
                altUse: false,
                isAirhornReaction,
                out InventorySlot _,
                out ItemData _,
                out shouldEatInput,
                out bool _))
        {
            __result = false;
            return false;
        }

        bool toss = isAirhornReaction || __instance.PlayerInfo.Input.IsHoldingAimSwing;
        Trace.Log($"use accepted, {(toss ? "tossing" : "planting")}, slot={__instance.EquippedItemIndex}");

        __instance.ItemUseTimestamp = Time.timeAsDouble;
        __instance.CancelItemUse();
        __instance.itemUseRoutine = __instance.StartCoroutine(
            toss ? TossRoutine(__instance) : PlantRoutine(__instance));
        __instance.PlayerInfo.CancelEmote(canHideEmoteMenu: false);
        __instance.CancelItemFlourish();

        __result = true;
        return false;
    }

    /// <summary>Mirror of <c>PlayerInventory.PlantLandmineRoutine</c>.</summary>
    private static IEnumerator PlantRoutine(PlayerInventory inventory)
    {
        inventory.SetCurrentItemUse(ItemUseType.Regular);

        bool jammedIntoGround = false;
        for (float timeSince = BMath.GetTimeSince(inventory.ItemUseTimestamp);
             timeSince < GameManager.ItemSettings.LandminePlantingTime;
             timeSince = BMath.GetTimeSince(inventory.ItemUseTimestamp))
        {
            if (!jammedIntoGround && timeSince >= GameManager.ItemSettings.LandminePlantingStickIntoGroundTime)
            {
                Vector3 jamPosition = inventory.PlayerInfo.RightHandEquipmentSwitcher.transform.position;
                jamPosition.y = inventory.transform.position.y;
                PlayBurrowVfx(inventory, jamPosition);
                CameraModuleController.Shake(
                    GameManager.CameraGameplaySettings.LandminePlantJamScreenshakeSettings,
                    inventory.transform.position);
                inventory.PlayerInfo.PlayerAudio.PlayLandminePlantForAllClients(stomp: false);
                jammedIntoGround = true;
            }

            yield return null;
        }

        // Leaving the ground mid-plant abandons the use and keeps the item, as the mine does.
        if (!inventory.PlayerInfo.Movement.IsGrounded)
        {
            Trace.Log("plant: cancelled, left the ground");
            inventory.CancelItemUse();
            yield break;
        }

        ulong useHash = inventory.GetEquippedItemUseHash();
        inventory.DecrementUseFromSlotAt(inventory.EquippedItemIndex);

        Vector3 position = inventory.transform.position;
        Quaternion rotation = GetEffectiveRotation(inventory);
        Vector3 offset = rotation * Vector3.down * GameManager.ItemSettings.LandminePlantingOffsetIntoGround;

        inventory.LocalPlayerMarkThrownItem(PlayerInventory.ThrownItemHand.Right);
        ShockNetwork.RequestSpawn(
            position + offset,
            rotation,
            Vector3.zero,
            Vector3.zero,
            LandmineArmType.Planted,
            inventory.IncrementAndGetCurrentItemUseId(ShockItem.Type),
            useHash);

        PlayBurrowVfx(inventory, position);
        CameraModuleController.Shake(
            GameManager.CameraGameplaySettings.LandminePlantStompScreenshakeSettings,
            inventory.transform.position);
        inventory.PlayerInfo.PlayerAudio.PlayLandminePlantForAllClients(stomp: true);

        yield return new WaitForSeconds(GameManager.ItemSettings.LandminePostPlantingTime);

        inventory.SetCurrentItemUse(ItemUseType.None);
        inventory.RemoveIfOutOfUses(inventory.EquippedItemIndex, dueToFinishedItemUse: true);
        Trace.Log("plant: finished");
    }

    /// <summary>Mirror of <c>PlayerInventory.TossLandmineRoutine</c>.</summary>
    private static IEnumerator TossRoutine(PlayerInventory inventory)
    {
        inventory.SetCurrentItemUse(ItemUseType.Alt);
        inventory.PlayerInfo.Movement.AlignWithCameraImmediately();

        for (float time = 0f; time < GameManager.ItemSettings.LandmineTossingTime; time += Time.deltaTime)
        {
            yield return null;
            inventory.PlayerInfo.Movement.AlignWithCameraImmediately();
        }

        ulong useHash = inventory.GetEquippedItemUseHash();
        inventory.DecrementUseFromSlotAt(inventory.EquippedItemIndex);

        inventory.PlayerInfo.RightHandEquipmentSwitcher.transform
            .GetPositionAndRotation(out Vector3 position, out Quaternion rotation);

        Vector3 forward = inventory.transform.TransformDirection(
            GameManager.ItemSettings.LandmineTossingDirectionLocalRotation * Vector3.forward);
        Vector3 spin = inventory.transform.TransformDirection(
            GameManager.ItemSettings.LandmineTossingDirectionLocalRotation
            * GameManager.ItemSettings.LandmineTossingLocalAngularVelocity);

        Vector3 velocity = inventory.PlayerInfo.Rigidbody.linearVelocity
            + forward * GameManager.ItemSettings.LandmineTossingSpeed;
        Vector3 angularVelocity = inventory.PlayerInfo.Rigidbody.angularVelocity + spin;

        inventory.LocalPlayerMarkThrownItem(PlayerInventory.ThrownItemHand.Right);
        ShockNetwork.RequestSpawn(
            position,
            rotation,
            velocity,
            angularVelocity,
            LandmineArmType.Tossed,
            inventory.IncrementAndGetCurrentItemUseId(ShockItem.Type),
            useHash);

        yield return new WaitForSeconds(GameManager.ItemSettings.LandminePostTossingTime);

        inventory.SetCurrentItemUse(ItemUseType.None);
        inventory.RemoveIfOutOfUses(inventory.EquippedItemIndex, dueToFinishedItemUse: true);
        Trace.Log("toss: finished");
    }

    /// <summary>
    /// Mirror of the plant routine's local GetEffectiveRotation: lay the device flat against the
    /// slope it is planted on, falling back to the player's facing on ground with no usable
    /// normal.
    /// </summary>
    private static Quaternion GetEffectiveRotation(PlayerInventory inventory)
    {
        Vector3 normal = inventory.PlayerInfo.Movement.GroundData.normal;
        if (!inventory.transform.forward.TryProjectOnPlaneAlong(Vector3.up, normal, out Vector3 projection))
        {
            return Quaternion.Euler(0f, GameManager.LocalPlayerInfo.transform.eulerAngles.y, 0f);
        }

        return Quaternion.LookRotation(projection, normal);
    }

    private static void PlayBurrowVfx(PlayerInventory inventory, Vector3 position)
    {
        if (inventory.isServer)
        {
            VfxManager.ServerPlayPooledVfxForAllClients(VfxType.MineBurrow, position, Quaternion.identity);
        }
        else
        {
            VfxManager.ClientPlayPooledVfxForAllClients(VfxType.MineBurrow, position, Quaternion.identity);
        }
    }
}

/// <summary>
/// Three places in PlayerInventory special-case <c>ItemType.Landmine</c> for handling rather than
/// for effect, and a modded type slips past all of them. Without these the mine can be planted in
/// mid-air, keeps planting after you leave the ground, and mishandles a use that began while the
/// aim-swing button was already down.
/// </summary>
[HarmonyPatch]
internal static class ShockLandmineGatePatches
{
    /// <summary>
    /// <c>TrySelectItemSlot</c>: selecting a mine while already holding aim-swing waits for the
    /// button to be released before it will toss, so the toss is deliberate rather than an
    /// artefact of the button being down as you switched to it.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.TrySelectItemSlot))]
    private static void SelectPostfix(PlayerInventory __instance, int index, bool __result)
    {
        if (!__result || !Plugin.Enabled.Value)
        {
            return;
        }

        if (__instance.GetEffectiveSlot(index).itemType == ShockItem.Type
            && __instance.PlayerInfo.Input.IsHoldingAimSwing)
        {
            __instance.isWaitingForAltUseInputRelease = true;
        }
    }

    /// <summary>
    /// <c>CanUseEquippedItem</c>: a mine cannot be planted in mid-air. Only the non-aiming plant
    /// is blocked — a tossed mine is thrown from wherever you are, as the mine is.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.CanUseEquippedItem))]
    private static void CanUsePostfix(PlayerInventory __instance, ref bool __result)
    {
        if (!__result || !Plugin.Enabled.Value)
        {
            return;
        }

        // The same slot the method itself resolved. Read again rather than taken as an out
        // parameter: Harmony can bind one, but the analyzer cannot tell a comparison from an
        // assignment through it, and this is no more work.
        if (__instance.GetEffectiveSlot(__instance.EquippedItemIndex).itemType == ShockItem.Type
            && !__instance.IsAimingItem
            && !__instance.PlayerInfo.Movement.IsGrounded)
        {
            __result = false;
        }
    }

    /// <summary>
    /// <c>InformLocalPlayerGroundedChanged</c>: leaving the ground part-way through a plant
    /// abandons it. The routine checks this too, but only once its animation has run out; this is
    /// what cancels it the moment the player is airborne.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.InformLocalPlayerGroundedChanged))]
    private static void GroundedPostfix(PlayerInventory __instance)
    {
        if (!Plugin.Enabled.Value)
        {
            return;
        }

        if (!__instance.PlayerInfo.Movement.IsGrounded
            && __instance.CurrentItemUse == ItemUseType.Regular
            && __instance.GetEffectivelyEquippedItem() == ShockItem.Type)
        {
            __instance.CancelItemUse();
        }
    }
}
