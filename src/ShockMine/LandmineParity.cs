using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mirror;

namespace ShockMine;

/// <summary>
/// The rest of the places the game special-cases <c>ItemType.Landmine</c>, re-applied to the
/// Shock Mine so it is handled by the landmine's rules everywhere, not only in the use routine.
///
/// <see cref="ShockLandmineGatePatches"/> covers the three in <c>PlayerInventory</c>. These are
/// the ones elsewhere, found by searching the decompiled game for every <c>ItemType.Landmine</c>
/// in 0.4.0. The movement locks were the visible gap: 0.3.0 mirrored the plant routine line for
/// line, but the landmine's halt is not in the routine at all — it lives in
/// <c>PlayerMovement</c>, which a modded type never matched, so a player could walk, jump and
/// turn while planting a Shock Mine.
///
/// Deliberately <em>not</em> carried over: the landmine's knockback and knockout cases in
/// <c>Hittable</c>/<c>PlayerMovement</c> (the mine never explodes, so it never hits anyone) and
/// its elimination reason (set explicitly on release).
/// </summary>
internal static class LandmineParity
{
    internal static bool IsShockMineEquipped(PlayerInfo player) =>
        Plugin.Enabled.Value
        && player.Inventory.GetEffectivelyEquippedItem(ignoreEquipmentHiding: true) == ShockItem.Type;

    internal static bool IsShockMineItem(Hittable hittable) =>
        hittable.AsEntity.IsItem && hittable.AsEntity.AsItem.ItemType == ShockItem.Type;

    /// <summary>
    /// Finds a compiler-generated local function by name prefix, searching the type and its
    /// nested closure classes. The compiler appends an index to local function names that shifts
    /// whenever the surrounding class is edited, so matching the whole name would break on a
    /// game update that touches the class at all.
    /// </summary>
    internal static MethodInfo? FindLocalFunction(Type type, string outer, string local)
    {
        string prefix = $"<{outer}>g__{local}|";
        return new[] { type }
            .Concat(type.GetNestedTypes(AccessTools.all))
            .SelectMany(AccessTools.GetDeclaredMethods)
            .FirstOrDefault(m => m.Name.StartsWith(prefix, StringComparison.Ordinal)
                && m.ReturnType == typeof(bool));
    }

    internal static bool ReportMissing(MethodInfo? method, string what)
    {
        if (method is null)
        {
            Plugin.Log.LogWarning($"Could not find {what}; the Shock Mine will not follow that landmine rule.");
        }

        return method is not null;
    }
}

/// <summary>
/// <c>PlayerMovement.CanMove</c>: a player planting a landmine stands still until it is in the
/// ground. Only the plant (<c>ItemUseType.Regular</c>) — a toss can be thrown on the move.
/// </summary>
[HarmonyPatch(typeof(PlayerMovement), "CanMove")]
internal static class ShockCanMovePatch
{
    private static void Postfix(PlayerMovement __instance, ref bool __result)
    {
        if (__result
            && LandmineParity.IsShockMineEquipped(__instance.PlayerInfo)
            && __instance.PlayerInfo.Inventory.CurrentItemUse == ItemUseType.Regular)
        {
            __result = false;
        }
    }
}

/// <summary><c>PlayerMovement.CanJump</c>: no jumping during a plant <em>or</em> a toss.</summary>
[HarmonyPatch(typeof(PlayerMovement), "CanJump")]
internal static class ShockCanJumpPatch
{
    private static void Postfix(PlayerMovement __instance, ref bool __result)
    {
        if (__result
            && LandmineParity.IsShockMineEquipped(__instance.PlayerInfo)
            && __instance.PlayerInfo.Inventory.CurrentItemUse != ItemUseType.None)
        {
            __result = false;
        }
    }
}

/// <summary>
/// <c>ApplyRotation</c>'s local <c>CanRotate</c>: no turning during a plant or a toss. The toss
/// routine still aims itself through <c>AlignWithCameraImmediately</c>, exactly as the
/// landmine's does; this only stops the player's own input turning them mid-use.
///
/// The game's check reads <c>GetEffectivelyEquippedItem()</c> without
/// <c>ignoreEquipmentHiding</c>, unlike the move and jump checks; matched here.
/// </summary>
[HarmonyPatch]
internal static class ShockCanRotatePatch
{
    private static readonly MethodInfo? Target =
        LandmineParity.FindLocalFunction(typeof(PlayerMovement), "ApplyRotation", "CanRotate");

    private static bool Prepare() =>
        LandmineParity.ReportMissing(Target, "PlayerMovement's ApplyRotation.CanRotate");

    private static MethodBase? TargetMethod() => Target;

    private static void Postfix(PlayerMovement __instance, ref bool __result)
    {
        if (__result
            && Plugin.Enabled.Value
            && __instance.PlayerInfo.Inventory.GetEffectivelyEquippedItem() == ShockItem.Type
            && __instance.PlayerInfo.Inventory.IsUsingItemAtAll)
        {
            __result = false;
        }
    }
}

/// <summary>
/// <c>HitWithItemInternal</c>'s local <c>ShouldBeDestroyedByHit</c>: a lightning strike or a
/// railgun shot vaporizes loose items — except landmines. Without this a Thunderstorm erased
/// planted Shock Mines that a landmine in the same spot would have survived.
///
/// The local function captures the hit's parameters, so it lives on a closure class and the
/// Hittable is reached through the closure's <c>&lt;&gt;4__this</c> field.
/// </summary>
[HarmonyPatch]
internal static class ShockVaporizePatch
{
    private static readonly MethodInfo? Target =
        LandmineParity.FindLocalFunction(typeof(Hittable), "HitWithItemInternal", "ShouldBeDestroyedByHit");

    private static readonly FieldInfo? This = Target?.DeclaringType is { } closure && closure != typeof(Hittable)
        ? AccessTools.Field(closure, "<>4__this")
        : null;

    private static bool Prepare() =>
        LandmineParity.ReportMissing(Target, "Hittable's HitWithItemInternal.ShouldBeDestroyedByHit");

    private static MethodBase? TargetMethod() => Target;

    private static void Postfix(object __instance, ref bool __result)
    {
        if (!__result || !Plugin.Enabled.Value)
        {
            return;
        }

        Hittable? hittable = __instance as Hittable ?? This?.GetValue(__instance) as Hittable;
        if (hittable != null && LandmineParity.IsShockMineItem(hittable))
        {
            __result = false;
        }
    }
}

/// <summary>
/// <c>UpdateSwungByRocketDriverTrailEffects</c>'s local <c>ShouldPlay</c>: a planted landmine
/// struck by a rocket-driver swing does not draw the rocket trail. Cosmetic only — the swing
/// still connects.
/// </summary>
[HarmonyPatch]
internal static class ShockRocketTrailPatch
{
    private static readonly MethodInfo? Target =
        LandmineParity.FindLocalFunction(typeof(Hittable), "UpdateSwungByRocketDriverTrailEffects", "ShouldPlay");

    private static bool Prepare() =>
        LandmineParity.ReportMissing(Target, "Hittable's rocket-driver trail ShouldPlay");

    private static MethodBase? TargetMethod() => Target;

    private static void Postfix(Hittable __instance, ref bool __result)
    {
        if (__result
            && Plugin.Enabled.Value
            && LandmineParity.IsShockMineItem(__instance)
            && __instance.TryGetComponent(out Landmine landmine)
            && landmine.IsPlanted)
        {
            __result = false;
        }
    }
}

/// <summary>
/// <c>PhysicalItem.CmdGiveToPlayer</c>: picking up someone else's armed landmine tells the
/// player they disarmed it.
///
/// The game only sends that notice after <c>ServerTryAddItem</c> succeeds, and the command
/// bails out silently on a rate limit, a full inventory or a suppressed pickup before then. So
/// rather than re-deriving every one of those checks, the prefix notes a pickup that would
/// count as a disarm and the <c>ServerTryAddItem</c> postfix sends the notice only if the item
/// was actually taken.
/// </summary>
[HarmonyPatch]
internal static class ShockDisarmNoticePatch
{
    private static PlayerInventory? pendingDisarmer;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(PhysicalItem), "UserCode_CmdGiveToPlayer__PlayerInventory__NetworkConnectionToClient")]
    private static void GivePrefix(PhysicalItem __instance, PlayerInventory player)
    {
        pendingDisarmer = null;
        if (!Plugin.Enabled.Value
            || __instance.ItemType != ShockItem.Type
            || player == null
            || !__instance.TryGetComponent(out Landmine landmine))
        {
            return;
        }

        if (landmine.IsArmed && landmine.Owner != player)
        {
            pendingDisarmer = player;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PhysicalItem), "UserCode_CmdGiveToPlayer__PlayerInventory__NetworkConnectionToClient")]
    private static void GivePostfix() => pendingDisarmer = null;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.ServerTryAddItem))]
    private static void AddPostfix(PlayerInventory __instance, ItemType itemToAdd, bool __result)
    {
        if (!__result || itemToAdd != ShockItem.Type || pendingDisarmer != __instance)
        {
            return;
        }

        pendingDisarmer = null;
        if (NetworkServer.active)
        {
            __instance.PlayerInfo.RpcInformDisarmedLandmine();
            Trace.Log($"server: {__instance.PlayerInfo.name} disarmed a Shock Mine");
        }
    }
}
