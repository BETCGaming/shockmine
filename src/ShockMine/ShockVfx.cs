using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace ShockMine;

/// <summary>
/// Plays the lightning-in-a-bottle zap over a seized victim, for as long as the hold lasts.
///
/// The game's own <c>Hittable.PlayLightningStrikeHitEffectsForAllClients</c> is no use directly:
/// it plays <c>ThunderstormPlayerZapped</c> for a fixed
/// <c>ThunderstormLightningStrikeHitVfxDuration</c> and then stops, which is a flash rather than
/// a five-second hold.
///
/// The first version drove that by pulling a *fresh* effect out of the pool each time the
/// previous one ran out, and never stopping the old one. That produced all three of the visual
/// defects at once: effects stacking up, a trail of them left behind wherever the victim had
/// been, and leftovers still playing after the hold ended. The pool also reclaims an effect on
/// its own once it finishes, so the instance we thought we owned could be handed to someone else
/// mid-hold.
///
/// So: exactly one pooled effect per victim, <em>parented to the victim</em> so it tracks them
/// rather than being repositioned each frame, replayed on the same instance when it runs out,
/// and explicitly stopped, unparented and returned to the pool on release. <c>Play</c> is called
/// with <c>registerWhenFinished: false</c> so the pool cannot reclaim it behind our back.
/// </summary>
internal static class ShockVfx
{
    private sealed class Zap
    {
        public PoolableParticleSystem? Particles;
        public Transform? Follow;
        public bool IsGolfCart;
    }

    private static readonly Dictionary<uint, Zap> Active = new();

    /// <summary>Starts the zap over the entity with this net id, if it has not got one.</summary>
    public static void BeginZap(uint victimNetId, float remainingSeconds)
    {
        if (Active.ContainsKey(victimNetId))
        {
            return;
        }

        if (!NetworkClient.spawned.TryGetValue(victimNetId, out NetworkIdentity identity)
            || identity == null)
        {
            // The victim has not spawned on this client yet. Nothing to attach to, and the
            // release message will tidy the entry away regardless.
            return;
        }

        if (!identity.TryGetComponent(out Hittable hittable) || hittable.AsEntity == null)
        {
            return;
        }

        Zap zap = new()
        {
            Follow = AttachPoint(hittable),
            IsGolfCart = hittable.AsEntity.IsGolfCart,
        };

        Active[victimNetId] = zap;
        Acquire(zap);

        Trace.Log($"client: zap started on {victimNetId} for {remainingSeconds:0.#}s");
    }

    /// <summary>Stops the zap, hands the effect back, and plays the burst it ends on.</summary>
    public static void EndZap(uint victimNetId)
    {
        if (!Active.TryGetValue(victimNetId, out Zap zap))
        {
            return;
        }

        Active.Remove(victimNetId);

        Vector3 where = zap.Follow != null ? zap.Follow.position : Vector3.zero;
        Release(zap);

        VfxType endType = zap.IsGolfCart
            ? VfxType.ThunderstormGolfCartZappedEnd
            : VfxType.ThunderstormPlayerZappedEnd;

        // The closing burst is a genuine one-shot, so the pool may have it back when it is done.
        if (VfxPersistentData.TryGetPooledVfx(endType, out PoolableParticleSystem end))
        {
            end.transform.SetParent(null, worldPositionStays: false);
            end.transform.position = where;
            end.Play();
        }

        Trace.Log($"client: zap ended on {victimNetId}");
    }

    /// <summary>
    /// Keeps each zap running. Driven from <see cref="ModRunner"/> rather than from a coroutine
    /// per victim, so a client that misses a release message cannot leak one.
    ///
    /// No repositioning here: the effect is parented to its victim, so it follows them for free
    /// and cannot be left behind when they are thrown about.
    /// </summary>
    public static void Tick()
    {
        if (Active.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<uint, Zap> entry in Active)
        {
            Zap zap = entry.Value;
            if (zap.Follow == null)
            {
                continue;
            }

            if (zap.Particles == null)
            {
                // The pool had nothing to give last time. Try again.
                Acquire(zap);
            }
            else if (!zap.Particles.IsPlaying)
            {
                // Same instance, played again — never a second one.
                zap.Particles.Play(registerWhenFinished: false);
            }

            ShockAudio.Follow(entry.Key, zap.Follow.position);
        }
    }

    private static void Acquire(Zap zap)
    {
        if (zap.Follow == null)
        {
            return;
        }

        VfxType zapType = zap.IsGolfCart
            ? VfxType.ThunderstormGolfCartZapped
            : VfxType.ThunderstormPlayerZapped;

        if (!VfxPersistentData.TryGetPooledVfx(zapType, out PoolableParticleSystem particles))
        {
            // The pool is empty this frame. Tick will try again.
            return;
        }

        zap.Particles = particles;
        particles.transform.SetParent(zap.Follow, worldPositionStays: false);
        particles.transform.localPosition = Vector3.zero;
        particles.transform.localRotation = Quaternion.identity;

        // registerWhenFinished: false — otherwise the pool reclaims it the moment the effect
        // runs out and hands our instance to something else mid-hold.
        particles.Play(registerWhenFinished: false);
    }

    private static void Release(Zap zap)
    {
        PoolableParticleSystem? particles = zap.Particles;
        zap.Particles = null;

        if (particles == null)
        {
            return;
        }

        particles.Stop(ParticleSystemStopBehavior.StopEmittingAndClear);

        // Unparented before it goes back: a pooled object left under a player would be destroyed
        // with them, and the pool would hand out a dead reference later.
        particles.transform.SetParent(null, worldPositionStays: false);
        particles.ReturnToPool();
    }

    /// <summary>
    /// Where the lightning strike hangs its own direct-hit effect: on the player's transform,
    /// and on the cart itself for a golf cart.
    /// </summary>
    private static Transform AttachPoint(Hittable hittable) => hittable.transform;

    /// <summary>Drops every zap, for a disconnect or a scene change.</summary>
    public static void Reset()
    {
        foreach (KeyValuePair<uint, Zap> entry in Active)
        {
            Release(entry.Value);
        }

        Active.Clear();
    }
}
