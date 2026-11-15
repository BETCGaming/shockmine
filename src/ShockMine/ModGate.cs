using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace ShockMine;

/// <summary>Client to server on connect: "I have the mod installed."</summary>
internal struct ShockHelloMessage : NetworkMessage
{
}

/// <summary>
/// Keeps unmodded players out of a modded lobby.
///
/// The Shock Mine is injected into the item pools, so the host can hand any player an
/// ItemType an unmodded client cannot resolve — it would log errors, show a blank slot and throw
/// on use. Rather than let that happen mid-match, clients that do not announce themselves
/// shortly after connecting are disconnected.
/// </summary>
internal static class ModGate
{
    /// <summary>Generous enough to cover a slow connect, short enough to fail fast.</summary>
    private const float GraceSeconds = 10f;

    private static readonly Dictionary<int, float> FirstSeen = new();
    private static readonly HashSet<int> Modded = new();

    private static bool serverHandlerRegistered;
    private static bool helloSent;

    public static void Reset()
    {
        FirstSeen.Clear();
        Modded.Clear();
    }

    /// <summary>Polled by <see cref="ModRunner"/>.</summary>
    public static void Tick()
    {
        EnsureHandlers();
        AnnounceSelf();
        KickUnmodded();
    }

    private static void EnsureHandlers()
    {
        if (NetworkServer.active)
        {
            if (!serverHandlerRegistered)
            {
                NetworkServer.RegisterHandler<ShockHelloMessage>(
                    static (conn, _) => MarkModded(conn),
                    requireAuthentication: true);
                serverHandlerRegistered = true;
            }
        }
        else if (serverHandlerRegistered)
        {
            serverHandlerRegistered = false;
            Reset();
        }
    }

    /// <summary>
    /// Tells the host we have the mod. Sent once the connection is ready rather than on connect:
    /// the game's own authenticator has to finish first or the message is dropped.
    /// </summary>
    private static void AnnounceSelf()
    {
        if (!NetworkClient.active || NetworkServer.active)
        {
            // The host does not have to introduce itself.
            helloSent = NetworkServer.active;
            return;
        }

        if (helloSent)
        {
            return;
        }

        if (!NetworkClient.isConnected || !NetworkClient.connection.isAuthenticated)
        {
            return;
        }

        NetworkClient.Send(default(ShockHelloMessage));
        helloSent = true;
        Trace.Log("client: announced the mod to the host");
    }

    private static void MarkModded(NetworkConnectionToClient? conn)
    {
        if (conn == null)
        {
            return;
        }

        if (Modded.Add(conn.connectionId))
        {
            Plugin.Log.LogInfo($"Connection {conn.connectionId} has ShockMine installed.");
        }
    }

    private static void KickUnmodded()
    {
        if (!NetworkServer.active || !Plugin.BlockUnmodded.Value || !Plugin.Enabled.Value)
        {
            return;
        }

        float now = Time.time;
        List<NetworkConnectionToClient>? toKick = null;

        foreach (NetworkConnectionToClient conn in NetworkServer.connections.Values)
        {
            if (conn == null || Modded.Contains(conn.connectionId))
            {
                continue;
            }

            // Never gate the host's own connection.
            if (conn == NetworkServer.localConnection)
            {
                continue;
            }

            // The grace period starts at authentication, not at connection. A client cannot
            // announce itself until the game's own authenticator has finished, so counting from
            // first sight would kick players whose handshake is merely slow.
            if (!conn.isAuthenticated)
            {
                continue;
            }

            if (!FirstSeen.TryGetValue(conn.connectionId, out float seen))
            {
                FirstSeen[conn.connectionId] = now;
                continue;
            }

            if (now - seen >= GraceSeconds)
            {
                (toKick ??= new List<NetworkConnectionToClient>()).Add(conn);
            }
        }

        if (toKick is null)
        {
            return;
        }

        foreach (NetworkConnectionToClient conn in toKick)
        {
            Plugin.Log.LogWarning(
                $"Disconnecting connection {conn.connectionId}: ShockMine is not installed. "
                + "Set BlockUnmodded to false to allow unmodded players (they will still be "
                + "handed Shock Mines they cannot resolve, so this is not recommended).");

            FirstSeen.Remove(conn.connectionId);
            conn.Disconnect();
        }
    }
}
