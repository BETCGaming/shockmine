using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace ShockMine;

/// <summary>
/// The server half of a sprung Shock Mine: one shared window, started by the first thing to trip
/// it, during which everything inside the seize radius is caught and held rigid.
///
/// Added to the mine's own GameObject when it trips, so its lifetime is the mine's and a mine
/// destroyed out from under it (blown up by someone else, or the hole ending) takes its window
/// with it. It is a plain MonoBehaviour, not a NetworkBehaviour: the hold itself reaches clients
/// through the <c>frozenState</c> SyncVar the game already replicates, and the presentation
/// through our own messages.
/// </summary>
internal sealed class ShockSeizeWindow : MonoBehaviour
{
    private Landmine mine = null!;
    private ItemUseId useId;
    private double endTime;
    private readonly List<Hittable> held = new();
    private bool firstSweepDone;

    public static ShockSeizeWindow Begin(Landmine mine)
    {
        ShockSeizeWindow window = mine.gameObject.AddComponent<ShockSeizeWindow>();
        window.mine = mine;
        window.useId = mine.itemUseId;
        window.endTime = Time.timeAsDouble + ShockRules.SeizeSeconds;
        Trace.Log($"server: mine sprung, window of {ShockRules.SeizeSeconds:0.#}s begins");

        // The discharge is shown at the mine itself, not only on victims. Until 0.4.0 the only
        // visible sign of a sprung mine was the zap on whoever it caught, so a mine set off
        // with nobody in range — shot by a railgun or shotgun, which the landmine's own hit
        // handlers turn into ServerExplode — gave no sign at all and then vanished when its
        // window ran out. A landmine shot the same way explodes on the spot.
        VfxManager.ServerPlayPooledVfxForAllClients(
            VfxType.ThunderstormStrike, mine.transform.position, Quaternion.identity);
        ShockNetwork.ServerAnnounceSprung(mine.transform.position);
        return window;
    }

    /// <summary>
    /// Runs on the physics tick, the same cadence <c>LandmineManager</c> drives detection on, so
    /// a player who walks in half a second after the mine went off is caught on the same beat
    /// they would have tripped it.
    /// </summary>
    private void FixedUpdate()
    {
        if (!NetworkServer.active)
        {
            return;
        }

        if (Time.timeAsDouble >= endTime)
        {
            Finish();
            return;
        }

        Catch();

        // A discharge that catches nothing is over at once, as a landmine's explosion is. The
        // window exists so that people tangled up in a shock stay caught and late entrants
        // join them; with nobody caught by the first sweep there is nothing to hold, and a
        // mine left live for the full window read as one that silently disappeared.
        if (!firstSweepDone)
        {
            firstSweepDone = true;
            if (held.Count == 0)
            {
                Trace.Log("server: discharge caught nothing, mine spent at once");
                Finish();
                return;
            }
        }

        HoldStill();
    }

    /// <summary>
    /// Everything inside the landmine's explosion radius — deliberately wider than the detection
    /// radius that trips it, so standing just outside where you set it off does not save you.
    /// </summary>
    private void Catch()
    {
        Vector3 centre = mine.transform.position;
        int count = Physics.OverlapSphereNonAlloc(
            centre,
            GameManager.ItemSettings.LandmineExplosionRange,
            PlayerGolfer.overlappingColliderBuffer,
            GameManager.LayerSettings.LandmineHittablesMask,
            QueryTriggerInteraction.Ignore);

        // Collected first rather than seized inside the loop: ejecting a cart's passengers
        // changes what is standing where, and mutating that mid-query invites surprises.
        List<Hittable> caught = new();
        for (int i = 0; i < count; i++)
        {
            if (!PlayerGolfer.overlappingColliderBuffer[i]
                    .TryGetComponentInParent(out Hittable hittable, includeInactive: true))
            {
                continue;
            }

            if (IsEligible(hittable) && !caught.Contains(hittable))
            {
                caught.Add(hittable);
            }
        }

        foreach (Hittable hittable in caught)
        {
            // A cart is emptied rather than held with people inside it: its passengers are
            // thrown clear on the game's own knockout exit, then seized where they land.
            if (hittable.AsEntity.IsGolfCart)
            {
                Evacuate(hittable.AsEntity.AsGolfCart);
            }

            Seize(hittable);
        }
    }

    /// <summary>
    /// Throws a cart's passengers clear using the game's own knockout exit — the same path it
    /// uses when a driver is knocked out — so they land in a sane place rather than being
    /// teleported. Each one is then caught in its own right on this tick or the next.
    /// </summary>
    private void Evacuate(GolfCartInfo cart)
    {
        // Copied before iterating: exiting mutates the passenger list.
        List<PlayerInfo> passengers = new(cart.passengers);
        foreach (PlayerInfo passenger in passengers)
        {
            if (passenger == null)
            {
                continue;
            }

            passenger.ExitGolfCart(GolfCartExitType.Knockout);
            Trace.Log($"server: ejected {passenger.name} from a cart");

            // Seized directly rather than left for the next overlap: an ejected passenger can
            // land outside the radius, and being thrown clear by a mine you drove into should
            // not be a way to escape it.
            if (passenger.AsHittable != null && IsEligible(passenger.AsHittable))
            {
                Seize(passenger.AsHittable);
            }
        }
    }

    private bool IsEligible(Hittable hittable)
    {
        // The mine itself.
        if (hittable == mine.AsEntity.AsHittable)
        {
            return false;
        }

        if (held.Contains(hittable))
        {
            return false;
        }

        Entity entity = hittable.AsEntity;
        if (entity == null || entity.IsDestroyed)
        {
            return false;
        }

        // Traffic is scenery as far as the landmine is concerned, and so it is here.
        if (entity.IsTrafficVehicle)
        {
            return false;
        }

        // Nothing to hold in place.
        if (!entity.HasRigidbody)
        {
            return false;
        }

        // Already frozen by something else — a freeze bomb, most likely. Taking the state over
        // would mean releasing it on our schedule rather than theirs, so leave it alone.
        if (hittable.FrozenState != FrozenState.None)
        {
            return false;
        }

        // The planter's teammates are spared, as they are by the landmine. The planter is not:
        // you can walk into your own mine.
        return !IsTeammateOfOwner(entity);
    }

    /// <summary>Mirror of <c>Landmine.IsTeammateOfOwner</c>, which is private to the game.</summary>
    private bool IsTeammateOfOwner(Entity entity)
    {
        PlayerInventory owner = mine.Owner;
        if (owner == null || MatchSetupMenu.CurrentGameMode != GameMode.Teams)
        {
            return false;
        }

        if (entity.IsPlayer)
        {
            return owner.PlayerInfo.IsTeammateOf(entity.PlayerInfo, excludeSelf: true);
        }

        if (entity.IsGolfCart)
        {
            return entity.AsGolfCart.ContainsOnlyTeammatesOf(owner.PlayerInfo, excludeReferencePlayer: true);
        }

        return false;
    }

    private void Seize(Hittable hittable)
    {
        // No pull toward the mine: victims freeze exactly where they were caught. Up to 0.3.0 a
        // 6 m/s VelocityChange "snap" was applied here, but HoldStill zeroes velocity in the
        // same FixedUpdate, before the physics step, so it moved a victim ~12 cm at most — and
        // never moved a remote player at all, whose body is kinematic on the server. Removed in
        // 0.4.0 rather than made to work.
        // Named as ours *before* the freeze, not after. Setting the SyncVar runs its hook
        // synchronously on the host, and that hook is what builds the ice block — so a victim
        // recorded afterwards would already be encased by the time the suppression patch could
        // have said no.
        ShockVictims.MarkHeld(hittable.netId);

        hittable.NetworkfrozenState = FrozenState.Frozen;
        held.Add(hittable);

        ShockNetwork.ServerAnnounceSeized(hittable, (float)(endTime - Time.timeAsDouble));
        AnnounceToFeed(hittable);
        Trace.Log($"server: seized {hittable.name}");
    }

    /// <summary>
    /// Credits the planter in the top-right feed. Server-side on purpose:
    /// <c>ShowItemHitMessage</c> broadcasts to every client itself.
    ///
    /// Nothing is posted for a cart, ball or item — the feed deals in players — and nothing for
    /// a mine whose owner has since left.
    /// </summary>
    private void AnnounceToFeed(Hittable victim)
    {
        if (!victim.AsEntity.IsPlayer || mine.Owner == null)
        {
            return;
        }

        InfoFeed.ShowItemHitMessage(mine.Owner.PlayerInfo, victim.AsEntity.PlayerInfo, ShockItem.Type);
    }

    /// <summary>
    /// Holds every victim rigid for the duration.
    ///
    /// <see cref="FrozenState"/> alone does not do this — the game only changes a frozen body's
    /// damping and gravity, so a victim would slide down a slope, drift, or keep falling. The
    /// shock is supposed to stop them dead, so all motion is cancelled on every physics tick.
    /// Caught in mid-air, you hang there shocked until the mine lets go.
    /// </summary>
    private void HoldStill()
    {
        foreach (Hittable hittable in held)
        {
            if (hittable == null || hittable.AsEntity == null || !hittable.AsEntity.HasRigidbody)
            {
                continue;
            }

            // A kinematic body here is one the server does not simulate — a remote player, whose
            // own client owns their physics. Writing to it does nothing but log "Setting linear
            // velocity of a kinematic body is not supported" twice a tick: the first two-player
            // session logged 500 of them for one victim. That victim's own client holds them
            // instead (ShockLocalVictim.FixedTick).
            Rigidbody body = hittable.AsEntity.Rigidbody;
            if (body.isKinematic)
            {
                continue;
            }

            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
    }

    /// <summary>
    /// Lets everyone go and spends the mine. One mine, one window: the landmine destroys itself
    /// when it explodes, and the Shock Mine does the same once its hold runs out.
    /// </summary>
    private void Finish()
    {
        foreach (Hittable hittable in held)
        {
            if (hittable == null)
            {
                continue;
            }

            // Only let go of what is still ours to let go of. Something frozen again since — or
            // thawed early by a respawn — is no longer our business.
            if (hittable.FrozenState == FrozenState.Frozen)
            {
                hittable.NetworkfrozenState = FrozenState.None;
            }

            CreditPlanter(hittable);
            ShockNetwork.ServerAnnounceReleased(
                hittable,
                mine != null && mine.Owner != null ? mine.Owner.PlayerInfo : null,
                useId,
                mine != null ? mine.transform.position : hittable.transform.position);
        }

        Trace.Log($"server: window over, released {held.Count} victim(s), mine spent");
        held.Clear();

        // Destroy the mine rather than the component: the window is over and so is the device.
        enabled = false;
        if (mine != null && mine.AsEntity != null && !mine.AsEntity.IsDestroyed)
        {
            mine.AsEntity.DestroyEntity();
        }
    }

    /// <summary>
    /// The server's half of the pay-off: names the planter as the one to blame if the victim is
    /// still out of the running when the hole resolves. Unlike a freeze, being shocked can cost
    /// you the hole.
    ///
    /// The knockout itself is deliberately *not* here. 0.3.0 called <c>TryKnockOut</c> from the
    /// server, and the first two-player session showed it failing both ways. For the host it
    /// returned false every time, because unfreezing a local player starts the game's thaw
    /// immunity synchronously (<c>OnLocalPlayerFrozenStateChanged</c>), and that blocked the
    /// knockout on the very next line. For a remote player it "succeeded" on the server's copy
    /// only and threw "Only the local player is allowed to remove their own status effects":
    /// <c>TryKnockOut</c> is a local-player API that drives the victim's own animator and sends
    /// <c>CmdInformKnockedOut</c> from their client. The release message now carries what the
    /// victim's client needs to knock itself down (see <see cref="ShockLocalVictim"/>).
    /// </summary>
    private void CreditPlanter(Hittable victim)
    {
        if (!victim.AsEntity.IsPlayer)
        {
            return;
        }

        PlayerInfo? owner = mine != null && mine.Owner != null ? mine.Owner.PlayerInfo : null;
        victim.AsEntity.PlayerInfo.AsGolfer.ServerSetPotentialEliminationReason(
            owner != null ? owner.AsGolfer : null, useId, EliminationReason.Landmine);
    }
}

/// <summary>
/// Who is currently held by a Shock Mine, on every client as well as the server.
///
/// The hold itself replicates for free through the <c>frozenState</c> SyncVar, but a client
/// cannot tell our freeze from a freeze bomb's — and the difference decides whether it draws an
/// ice block or a lightning zap. The server names its victims explicitly, and this is where that
/// list lives.
/// </summary>
internal static class ShockVictims
{
    private static readonly HashSet<uint> Held = new();

    public static bool IsHeld(uint netId) => Held.Contains(netId);

    public static bool IsHeld(Hittable hittable) => hittable != null && Held.Contains(hittable.netId);

    public static void MarkHeld(uint netId) => Held.Add(netId);

    public static void MarkReleased(uint netId) => Held.Remove(netId);

    /// <summary>Clears the list when a client disconnects, so it cannot leak into the next match.</summary>
    public static void Reset() => Held.Clear();
}
