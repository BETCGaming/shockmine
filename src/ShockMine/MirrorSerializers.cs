using Mirror;
using UnityEngine;

namespace ShockMine;

/// <summary>
/// Registers read/write functions for our network messages.
///
/// Mirror's weaver normally generates these at compile time for every NetworkMessage in the Unity
/// project. A BepInEx mod cannot run the weaver, so without registering them by hand every send
/// fails with "No writer found for ...". Mirror looks the functions up through the public
/// Writer&lt;T&gt;/Reader&lt;T&gt; delegates, so we can simply fill them in.
///
/// The game's own GeneratedNetworkCode has already filled in <c>Writer&lt;ItemUseId&gt;</c> and
/// friends by the time we run, so the bodies below can lean on <c>writer.Write</c> for its types
/// rather than taking the struct apart field by field.
/// </summary>
internal static class MirrorSerializers
{
    public static void Register()
    {
        Writer<ShockSpawnMessage>.write = static (writer, message) =>
        {
            writer.Write(message.Position);
            writer.Write(message.Rotation);
            writer.Write(message.Velocity);
            writer.Write(message.AngularVelocity);
            writer.Write(message.ArmType);
            writer.Write(message.UseId);
            writer.Write(message.UseHash);
        };
        Reader<ShockSpawnMessage>.read = static reader => new ShockSpawnMessage
        {
            Position = reader.Read<Vector3>(),
            Rotation = reader.Read<Quaternion>(),
            Velocity = reader.Read<Vector3>(),
            AngularVelocity = reader.Read<Vector3>(),
            ArmType = reader.Read<byte>(),
            UseId = reader.Read<ItemUseId>(),
            UseHash = reader.Read<ulong>(),
        };

        Writer<ShockSeizedMessage>.write = static (writer, message) =>
        {
            writer.Write(message.VictimNetId);
            writer.Write(message.RemainingSeconds);
        };
        Reader<ShockSeizedMessage>.read = static reader => new ShockSeizedMessage
        {
            VictimNetId = reader.Read<uint>(),
            RemainingSeconds = reader.Read<float>(),
        };

        Writer<ShockReleasedMessage>.write = static (writer, message) =>
        {
            writer.Write(message.VictimNetId);
            writer.Write(message.OwnerNetId);
            writer.Write(message.UseId);
            writer.Write(message.MinePosition);
        };
        Reader<ShockReleasedMessage>.read = static reader => new ShockReleasedMessage
        {
            VictimNetId = reader.Read<uint>(),
            OwnerNetId = reader.Read<uint>(),
            UseId = reader.Read<ItemUseId>(),
            MinePosition = reader.Read<Vector3>(),
        };

        Writer<ShockSprungMessage>.write =
            static (writer, message) => writer.Write(message.Position);
        Reader<ShockSprungMessage>.read =
            static reader => new ShockSprungMessage { Position = reader.Read<Vector3>() };

        // Empty message: nothing to serialize, but the delegates still have to exist.
        Writer<ShockHelloMessage>.write = static (_, _) => { };
        Reader<ShockHelloMessage>.read = static _ => default;

        Plugin.Log.LogInfo("Registered Shock Mine message serializers.");
    }
}
