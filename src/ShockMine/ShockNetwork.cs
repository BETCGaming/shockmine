using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace ShockMine;

/// <summary>
/// Client to server: "I planted or tossed a Shock Mine, spawn it."
///
/// The mirror image of the game's own <c>CmdSpawnLandmine</c>, carrying the same arguments. It
/// has to exist separately because that command hardcodes <c>ItemType.Landmine</c> when it
/// reaches <c>CourseManager.ServerSpawnLandmineInternal</c>, so routing our item through it would
/// put a real landmine on the ground.
/// </summary>
internal struct ShockSpawnMessage : NetworkMessage
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Velocity;
    public Vector3 AngularVelocity;

    /// <summary>A <see cref="LandmineArmType"/>: planted sticks, tossed arms once it settles.</summary>
    public byte ArmType;

    public ItemUseId UseId;

    /// <summary>
    /// The server's proof that the client really holds the item it says it is spending. Validated
    /// and consumed exactly as the game does for its own item commands.
    /// </summary>
    public ulong UseHash;
}

/// <summary>
/// Server to every client: this entity is now held by a mine.
///
/// The hold itself does not need announcing — <c>frozenState</c> is a SyncVar the game already
/// replicates. What clients cannot work out for themselves is <em>who</em> froze it, and that
/// decides whether they draw an ice block or a lightning zap.
/// </summary>
internal struct ShockSeizedMessage : NetworkMessage
{
    public uint VictimNetId;

    /// <summary>Seconds left in the window, so a late entrant's countdown is honest.</summary>
    public float RemainingSeconds;
}

/// <summary>
/// Server to every client: a mine has just discharged here. Carries the thunderclap; the burst
/// itself goes through the game's own <c>VfxManager.ServerPlayPooledVfxForAllClients</c>, but
/// the game has no for-all-clients one-shot for that sound.
/// </summary>
internal struct ShockSprungMessage : NetworkMessage
{
    public Vector3 Position;
}

/// <summary>
/// Server to every client: the window is over, this entity is free — and, if it is a player,
/// should now fall over.
///
/// The knockout fields exist because the fall has to happen on the victim's own client (see
/// <see cref="ShockLocalVictim"/>), and that client needs to know who to credit and where the
/// mine was to call <c>TryKnockOut</c> the way the game's own items do.
/// </summary>
internal struct ShockReleasedMessage : NetworkMessage
{
    public uint VictimNetId;

    /// <summary>netId of the planter's <c>PlayerInfo</c>, or 0 if they have left.</summary>
    public uint OwnerNetId;

    public ItemUseId UseId;

    public Vector3 MinePosition;
}

/// <summary>
/// Mirror's [Command] and [ClientRpc] attributes need the Mirror weaver, which a BepInEx mod
/// cannot run, so the mine talks to the host with plain registered messages instead.
/// </summary>
internal static class ShockNetwork
{
    /// <summary>
    /// Matches the spirit of the game's own per-connection rate limiter on spawning a landmine,
    /// which allows a burst at roughly four a second.
    /// </summary>
    private const double MinSecondsBetweenSpawns = 0.2;

    private static readonly Dictionary<int, double> LastSpawnPerConnection = new();

    private static bool serverHandlerRegistered;
    private static bool clientHandlerRegistered;

    /// <summary>
    /// Mirror drops registered handlers when a server or client shuts down, so handler state is
    /// re-checked every frame rather than registered once at startup.
    /// </summary>
    public static void EnsureHandlers()
    {
        if (NetworkServer.active)
        {
            if (!serverHandlerRegistered)
            {
                NetworkServer.RegisterHandler<ShockSpawnMessage>(OnServerSpawnRequest, requireAuthentication: true);
                serverHandlerRegistered = true;
                Trace.Log("server: registered the mine spawn handler");
            }
        }
        else if (serverHandlerRegistered)
        {
            serverHandlerRegistered = false;
            LastSpawnPerConnection.Clear();
        }

        if (NetworkClient.active)
        {
            if (!clientHandlerRegistered)
            {
                NetworkClient.RegisterHandler<ShockSeizedMessage>(OnClientSeized);
                NetworkClient.RegisterHandler<ShockReleasedMessage>(OnClientReleased);
                NetworkClient.RegisterHandler<ShockSprungMessage>(OnClientSprung);
                clientHandlerRegistered = true;
                Trace.Log("client: registered the seize handlers");
            }
        }
        else if (clientHandlerRegistered)
        {
            clientHandlerRegistered = false;

            // A disconnect leaves nobody to send the release, so the list would otherwise carry
            // stale victims into the next lobby and suppress their ice blocks there.
            ShockVictims.Reset();
            ShockVfx.Reset();
            ShockAudio.Reset();
            ShockUi.Clear();
            IceBlockHider.Reset();
            ShockLocalVictim.Reset();
        }
    }

    /// <summary>Tells every client that this entity is held by a mine rather than iced.</summary>
    public static void ServerAnnounceSeized(Hittable victim, float remainingSeconds)
    {
        ShockVictims.MarkHeld(victim.netId);
        NetworkServer.SendToAll(new ShockSeizedMessage
        {
            VictimNetId = victim.netId,
            RemainingSeconds = remainingSeconds,
        });
    }

    public static void ServerAnnounceReleased(
        Hittable victim,
        PlayerInfo? owner,
        ItemUseId useId,
        Vector3 minePosition)
    {
        ShockVictims.MarkReleased(victim.netId);
        NetworkServer.SendToAll(new ShockReleasedMessage
        {
            VictimNetId = victim.netId,
            OwnerNetId = owner != null ? owner.netId : 0,
            UseId = useId,
            MinePosition = minePosition,
        });
    }

    public static void ServerAnnounceSprung(Vector3 position) =>
        NetworkServer.SendToAll(new ShockSprungMessage { Position = position });

    private static void OnClientSprung(ShockSprungMessage message) =>
        ShockAudio.PlayCatch(message.Position);

    private static void OnClientSeized(ShockSeizedMessage message)
    {
        // The host has already recorded its own victims directly; marking again is harmless and
        // keeps the two paths identical.
        ShockVictims.MarkHeld(message.VictimNetId);
        ShockVfx.BeginZap(message.VictimNetId, message.RemainingSeconds);

        if (!NetworkClient.spawned.TryGetValue(message.VictimNetId, out NetworkIdentity identity)
            || identity == null)
        {
            return;
        }

        // The frozenState SyncVar and this message are sent independently, so the state can
        // arrive first and the hook will have built a visible ice block before anything knew
        // the freeze was a mine's. Hide it now the victim is named — never remove it: the
        // game's frozen-grounding check dereferences IceBlock.Collider every physics tick.
        if (identity.TryGetComponent(out Hittable hittable) && hittable.IceBlock != null)
        {
            IceBlockHider.Hide(hittable.IceBlock);
        }

        Vector3 position = identity.transform.position;
        ShockAudio.PlayCatch(position);
        ShockAudio.BeginHum(message.VictimNetId, position);

        // The jolt and the countdown are for the player actually caught, not for everyone
        // watching it happen.
        if (identity.isLocalPlayer)
        {
            CameraModuleController.Shake(
                GameManager.CameraGameplaySettings.ThunderstormExplosionScreenshakeSettings,
                position);
            ShockUi.BeginCountdown(message.RemainingSeconds);
        }
    }

    private static void OnClientReleased(ShockReleasedMessage message)
    {
        ShockVictims.MarkReleased(message.VictimNetId);
        ShockVfx.EndZap(message.VictimNetId);
        ShockAudio.EndHum(message.VictimNetId);

        if (NetworkClient.spawned.TryGetValue(message.VictimNetId, out NetworkIdentity identity)
            && identity != null
            && identity.isLocalPlayer)
        {
            ShockUi.Clear();
            ShockLocalVictim.QueueKnockout(message);
        }
    }

    /// <summary>Called by the using client once its plant or toss animation reaches the throw.</summary>
    public static void RequestSpawn(
        Vector3 position,
        Quaternion rotation,
        Vector3 velocity,
        Vector3 angularVelocity,
        LandmineArmType armType,
        ItemUseId useId,
        ulong useHash)
    {
        ShockSpawnMessage message = new()
        {
            Position = position,
            Rotation = rotation,
            Velocity = velocity,
            AngularVelocity = angularVelocity,
            ArmType = (byte)armType,
            UseId = useId,
            UseHash = useHash,
        };

        if (NetworkServer.active)
        {
            // The host is its own client: Mirror would round-trip this through the loopback
            // connection, but handling it directly keeps the host's mine in the same frame as
            // the animation that threw it.
            ServerSpawn(NetworkServer.localConnection, message);
            return;
        }

        NetworkClient.Send(message);
    }

    private static void OnServerSpawnRequest(NetworkConnectionToClient conn, ShockSpawnMessage message)
    {
        double now = Time.timeAsDouble;
        if (LastSpawnPerConnection.TryGetValue(conn.connectionId, out double last)
            && now - last < MinSecondsBetweenSpawns)
        {
            Plugin.Log.LogWarning($"Ignoring rapid mine spawn from connection {conn.connectionId}.");
            return;
        }

        LastSpawnPerConnection[conn.connectionId] = now;
        ServerSpawn(conn, message);
    }

    /// <summary>
    /// The server half, mirroring <c>CourseManager.ServerSpawnLandmineInternal</c>: spawn the
    /// item unspawned, hand the Landmine component its arming orders, then put it on the network.
    /// </summary>
    private static void ServerSpawn(NetworkConnection? conn, ShockSpawnMessage message)
    {
        PlayerInventory? owner = ResolveInventory(conn);
        if (owner == null)
        {
            Plugin.Log.LogWarning("Mine spawn ignored: the requesting connection has no player.");
            return;
        }

        // The game's own anti-duplication guard. Without it a client could spend one mine many
        // times over; with it the hash is consumed and a replay is refused.
        if (!owner.ServerValidateAndConsumeItemUse(message.UseHash))
        {
            Plugin.Log.LogWarning("Mine spawn refused: the item use did not validate.");
            return;
        }

        if (!SingletonNetworkBehaviour<CourseManager>.HasInstance)
        {
            Plugin.Log.LogWarning("Mine spawn ignored: no CourseManager yet.");
            return;
        }

        PhysicalItem physical = SingletonNetworkBehaviour<CourseManager>.Instance.ServerSpawnItem(
            ShockItem.Type,
            remainingUses: 1,
            message.Position,
            message.Rotation,
            message.Velocity,
            message.AngularVelocity,
            spawner: null,
            message.UseId,
            networkSpawn: false);

        if (physical == null)
        {
            Plugin.Log.LogError("Mine spawn failed: the item did not instantiate.");
            return;
        }

        if (!physical.TryGetComponent(out Landmine landmine))
        {
            Plugin.Log.LogError("Spawned Shock Mine has no Landmine component; destroying it.");
            Object.Destroy(physical.gameObject);
            return;
        }

        landmine.ServerInitialize((LandmineArmType)message.ArmType, owner, message.UseId);
        NetworkServer.Spawn(landmine.gameObject);

        Trace.Log($"server: spawned a mine as {(LandmineArmType)message.ArmType} for {owner.name}");
    }

    private static PlayerInventory? ResolveInventory(NetworkConnection? conn)
    {
        if (conn?.identity == null)
        {
            return null;
        }

        return conn.identity.GetComponent<PlayerInfo>()?.Inventory;
    }
}
