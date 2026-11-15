using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace ShockMine;

/// <summary>
/// The half of a seize that has to run on the victim's own machine.
///
/// A player's physics belongs to their own client. On the host a remote player's rigidbody is
/// kinematic, so the server cannot hold them still and cannot knock them down: the first
/// two-player session showed the server zeroing a kinematic body for the whole window (500
/// "Setting linear velocity of a kinematic body is not supported" warnings, and a victim who was
/// never actually held), and <c>TryKnockOut</c> failing for both players. The server still
/// decides who is caught and when they are released; each player's own client carries the hold
/// and the fall out on itself.
///
/// Bodies the server does simulate — carts, balls, loose items, and the host's own player —
/// are held by <see cref="ShockSeizeWindow"/> as before. The host's player is held from both
/// sides, which is harmless: both write zero.
/// </summary>
internal static class ShockLocalVictim
{
    /// <summary>
    /// How long to wait for the thaw to replicate before giving up on the knockout. The release
    /// message and the <c>frozenState</c> SyncVar travel independently, and the message usually
    /// wins, so the local player is normally still frozen for a frame or two when it lands.
    /// </summary>
    private const double ThawTimeoutSeconds = 3.0;

    private sealed class PendingKnockout
    {
        public ShockReleasedMessage Message;
        public double Deadline;
    }

    private static readonly List<PendingKnockout> Pending = new();

    /// <summary>Called from <see cref="ModRunner"/>'s FixedUpdate: holds the local player rigid.</summary>
    public static void FixedTick()
    {
        if (!Plugin.Enabled.Value || !NetworkClient.active)
        {
            return;
        }

        PlayerInfo? local = GameManager.LocalPlayerInfo;
        if (local == null || !ShockVictims.IsHeld(local.netId))
        {
            return;
        }

        Entity entity = local.AsEntity;
        if (entity == null || !entity.HasRigidbody || entity.Rigidbody.isKinematic)
        {
            return;
        }

        entity.Rigidbody.linearVelocity = Vector3.zero;
        entity.Rigidbody.angularVelocity = Vector3.zero;
    }

    /// <summary>Called from the release handler when the released entity is the local player.</summary>
    public static void QueueKnockout(ShockReleasedMessage message)
    {
        Pending.Add(new PendingKnockout
        {
            Message = message,
            Deadline = Time.timeAsDouble + ThawTimeoutSeconds,
        });
    }

    /// <summary>Called from <see cref="ModRunner"/>'s Update.</summary>
    public static void Tick()
    {
        if (Pending.Count == 0)
        {
            return;
        }

        PlayerInfo? local = GameManager.LocalPlayerInfo;

        for (int i = Pending.Count - 1; i >= 0; i--)
        {
            PendingKnockout pending = Pending[i];

            if (local == null || local.netId != pending.Message.VictimNetId)
            {
                Pending.RemoveAt(i);
                continue;
            }

            // Still frozen: the thaw has not replicated yet, and CanBeKnockedOutBy refuses a
            // frozen player outright.
            if (local.AsHittable.FrozenState == FrozenState.Frozen)
            {
                if (Time.timeAsDouble >= pending.Deadline)
                {
                    Plugin.Log.LogWarning(
                        "Shock Mine: the thaw never arrived, so the knockout was skipped.");
                    Pending.RemoveAt(i);
                }

                continue;
            }

            Pending.RemoveAt(i);
            KnockOut(local, pending.Message);
        }
    }

    public static void Reset() => Pending.Clear();

    /// <summary>
    /// Drops the local player where they stand, credited to the planter.
    ///
    /// Unfreezing a local player starts the game's thaw immunity
    /// (<c>OnLocalPlayerFrozenStateChanged</c> → <c>StartKnockoutImmunity</c>) — protection meant
    /// for someone climbing out of a freeze bomb's ice. For the Shock Mine the fall *is* the
    /// end of the freeze, so that immunity is cancelled first, or it blocks the knockout it was
    /// never meant to apply to. The immunity from the knockout itself still follows normally.
    ///
    /// No impulse: victims crumple where they were held rather than being thrown.
    /// </summary>
    private static void KnockOut(PlayerInfo local, ShockReleasedMessage message)
    {
        PlayerInfo? owner = null;
        if (message.OwnerNetId != 0
            && NetworkClient.spawned.TryGetValue(message.OwnerNetId, out NetworkIdentity identity)
            && identity != null)
        {
            identity.TryGetComponent(out owner);
        }

        PlayerMovement movement = local.Movement;
        movement.CancelKnockoutImmunity();

        bool knocked = movement.TryKnockOut(
            owner,
            KnockoutType.Landmine,
            isLegSweep: false,
            local.transform.InverseTransformPoint(message.MinePosition),
            Vector3.Distance(local.transform.position, message.MinePosition),
            incomingVelocityChange: Vector3.zero,
            ElectromagnetShieldHitBlockType.NotBlocked,
            message.UseId,
            fromSpecialState: false,
            canFallbackToUnground: true,
            out bool _,
            out bool blockedByTeamProtection);

        Trace.Log($"client: local player drops over (knocked={knocked}, "
            + $"teamProtected={blockedByTeamProtection})");
    }
}
