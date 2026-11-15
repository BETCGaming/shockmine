using System;
using System.Collections.Generic;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace ShockMine;

/// <summary>
/// Turns one of our mines into a seize instead of an explosion.
///
/// <c>ServerExplode</c> is where every trip ends up — the proximity check in
/// <c>ProcessCollidersInWarningRange</c> and the contact check in <c>OnCollisionEnter</c> both
/// call it — so diverting here inherits the landmine's trip behaviour exactly, including the
/// rule that a target outside the small collision radius must be moving above
/// <c>LandmineDetectionMinSpeed</c> to set it off. Nothing about detection is reimplemented.
/// </summary>
[HarmonyPatch(typeof(Landmine), "ServerExplode")]
internal static class LandmineExplodePatch
{
    private static bool Prefix(Landmine __instance)
    {
        if (!Plugin.Enabled.Value || !__instance.TryGetComponent(out ShockMarker _))
        {
            // A real landmine. Let it blow up.
            return true;
        }

        if (!NetworkServer.active)
        {
            return false;
        }

        // The landmine's own one-shot guards — isExploded, and destroying the entity — are both
        // skipped by this prefix, so the mine needs its own. The window component is it: while
        // one is attached the mine is already sprung, and detection calling in again every
        // physics tick is simply ignored.
        if (__instance.GetComponent<ShockSeizeWindow>() != null)
        {
            return false;
        }

        ShockSeizeWindow.Begin(__instance);
        return false;
    }
}

/// <summary>
/// Hides the ice block on a Shock Mine's victims — rather than stopping it from existing.
///
/// The first version prevented <c>UpdateIceBlock</c> from running at all, which looked right and
/// was badly wrong: the game's own frozen-player code reads
/// <c>PlayerInfo.AsHittable.IceBlock.Collider</c> with no null check, every physics tick, in
/// <c>PlayerMovement.UpdateFrozenState</c>. With no block that threw a NullReferenceException on
/// every tick of every seize — 900 in one session — and left <c>FrozenPlayerState</c> never
/// computed.
///
/// So the block is built exactly as the game expects and then made invisible and intangible: its
/// renderers are switched off and its collider disabled. The boxcast that grounds a frozen player
/// only reads the collider's size and transform, both of which survive being disabled, so the
/// grounding check works and nothing throws.
/// </summary>
[HarmonyPatch(typeof(Hittable), "UpdateIceBlock")]
internal static class HittableIceBlockPatch
{
    private static void Postfix(Hittable __instance)
    {
        if (!Plugin.Enabled.Value || !ShockVictims.IsHeld(__instance))
        {
            return;
        }

        if (__instance.IceBlock != null)
        {
            IceBlockHider.Hide(__instance.IceBlock);
        }
    }
}

/// <summary>
/// Switches an ice block's renderers and collider off, and — crucially — switches them back on
/// before the block returns to the pool.
///
/// Ice blocks are pooled and reused, so a block left hidden would be handed to the next freeze
/// bomb victim invisible. Restoration hangs off the block's own
/// <see cref="FreezeBombIceBlock.WillBeReturnedToPool"/> event rather than off our release
/// message: the game returns the block the moment <c>frozenState</c> goes back to None, which
/// can happen before our message lands, and ordering the two would be guesswork.
/// </summary>
internal static class IceBlockHider
{
    private sealed class Hidden
    {
        public Renderer[] Renderers = Array.Empty<Renderer>();
        public bool ColliderWasEnabled;
        public Action? Restore;
    }

    private static readonly Dictionary<FreezeBombIceBlock, Hidden> Blocks = new();

    public static void Hide(FreezeBombIceBlock block)
    {
        if (block == null || Blocks.ContainsKey(block))
        {
            return;
        }

        List<Renderer> switchedOff = new();
        foreach (Renderer renderer in block.GetComponentsInChildren<Renderer>(includeInactive: true))
        {
            if (renderer.enabled)
            {
                renderer.enabled = false;
                switchedOff.Add(renderer);
            }
        }

        Hidden state = new()
        {
            Renderers = switchedOff.ToArray(),
            ColliderWasEnabled = block.Collider != null && block.Collider.enabled,
        };

        if (block.Collider != null)
        {
            block.Collider.enabled = false;
        }

        state.Restore = () => Restore(block);
        block.WillBeReturnedToPool += state.Restore;
        Blocks[block] = state;
    }

    private static void Restore(FreezeBombIceBlock block)
    {
        if (block == null || !Blocks.TryGetValue(block, out Hidden state))
        {
            return;
        }

        Blocks.Remove(block);

        if (state.Restore != null)
        {
            block.WillBeReturnedToPool -= state.Restore;
        }

        foreach (Renderer renderer in state.Renderers)
        {
            if (renderer != null)
            {
                renderer.enabled = true;
            }
        }

        if (block.Collider != null && state.ColliderWasEnabled)
        {
            block.Collider.enabled = true;
        }
    }

    /// <summary>Puts every block we touched back, for a disconnect or a scene change.</summary>
    public static void Reset()
    {
        foreach (FreezeBombIceBlock block in new List<FreezeBombIceBlock>(Blocks.Keys))
        {
            Restore(block);
        }

        Blocks.Clear();
    }
}
