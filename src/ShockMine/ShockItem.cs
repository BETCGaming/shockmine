using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace ShockMine;

/// <summary>
/// Marks a spawned device as ours rather than one of the game's landmines.
///
/// The clone keeps the game's <see cref="Landmine"/> component — that is the whole point, it is
/// what makes <c>LandmineManager</c>'s job system drive our detection for free — so every patch
/// on Landmine runs for real mines too and needs a way to tell them apart. A marker component
/// rides along on the prefab, so every networked copy carries it, and the check is a
/// <c>TryGetComponent</c> rather than a lookup against the item type the patch cannot see.
/// </summary>
internal sealed class ShockMarker : MonoBehaviour
{
}

/// <summary>
/// Registers the Shock Mine as a real item in the game's <see cref="ItemCollection"/>.
///
/// The game's ItemType enum runs None..JumboBurger (0..17). We claim a value far above that so an
/// official item added in a future update cannot collide with ours. 200 is taken by the companion
/// BallSwapItem mod's Switcheroo, so this one takes 201 and the two can be installed together.
/// </summary>
internal static class ShockItem
{
    public const ItemType Type = (ItemType)201;

    public const string DisplayName = "Shock Mine";

    /// <summary>
    /// The seize duration match setup rule, clear of the game's own Rule range
    /// (Countdown..SmallTeamBoost, 0..30) and of the Switcheroo's 200. Safe as a modded value
    /// because every rule lookup is a dictionary keyed by Rule rather than an array indexed by
    /// it.
    /// </summary>
    public const MatchSetupRules.Rule SeizeDurationRule = (MatchSetupRules.Rule)202;

    /// <summary>Steel blue #5ABEDC, so the mine reads as a different device to a landmine.</summary>
    internal static readonly Color DeviceColor = new(90f / 255f, 190f / 255f, 220f / 255f, 1f);

    /// <summary>
    /// Arbitrary but fixed, and far from the range Unity generates, so every player's client
    /// agrees on which prefab this id means. Distinct from the Switcheroo's 0xB5A50001.
    /// </summary>
    private const uint NetworkAssetId = 0xB5A50101;

    private static ItemData? itemData;
    private static bool failedOnce;
    private static GameObject? prefabHolder;

    /// <summary>Deactivated parent that keeps runtime prefab templates out of the world.</summary>
    private static GameObject PrefabHolder
    {
        get
        {
            if (prefabHolder == null)
            {
                prefabHolder = new GameObject("ShockMinePrefabs");
                prefabHolder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(prefabHolder);
            }

            return prefabHolder;
        }
    }

    /// <summary>
    /// Called after every <see cref="ItemCollection.Initialize"/>. Initialize() rebuilds its
    /// dictionary from the backing array, so we make sure we are present in both.
    /// </summary>
    public static void Register(ItemCollection collection)
    {
        if (!Plugin.Enabled.Value || failedOnce)
        {
            return;
        }

        try
        {
            itemData ??= BuildItemData(collection);
            if (itemData is null)
            {
                return;
            }

            if (Array.IndexOf(collection.items, itemData) < 0)
            {
                ItemData[] items = collection.items;
                Array.Resize(ref items, items.Length + 1);
                items[^1] = itemData;
                collection.items = items;
            }

            collection.allItemData[Type] = itemData;
        }
        catch (Exception e)
        {
            // Registering is all-or-nothing: a half-registered item would hand the game an
            // ItemType it cannot resolve, so give up loudly rather than limp on.
            failedOnce = true;
            Plugin.Log.LogError($"Failed to register the Shock Mine, it will not appear: {e}");
        }
    }

    private static ItemData? BuildItemData(ItemCollection collection)
    {
        if (!collection.TryGetItemData(ItemType.Landmine, out ItemData donor))
        {
            Plugin.Log.LogError("Landmine item data not found; cannot build the Shock Mine from it.");
            return null;
        }

        if (donor.Prefab == null)
        {
            // Asset not loaded yet. Not cached and not fatal: the next Initialize retries.
            return null;
        }

        ItemData data = new();

        // Copy every field off the donor, then override only what makes the mine its own item.
        // Field-copying rather than property assignment keeps this working if the game adds new
        // ItemData fields in an update.
        foreach (FieldInfo field in typeof(ItemData).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            field.SetValue(data, field.GetValue(donor));
        }

        data.Type = Type;
        data.Icon = BuildIcon();
        data.Prefab = BuildPrefab(donor.Prefab);

        // Everything else is deliberately left as the landmine has it. MaxUses, NonAimUse and the
        // aiming flags are what give the mine the mine's plant-and-toss handling; the hit flags
        // are what let it reach balls and carts, which a mine that seizes them still needs.

        // The info feed refuses to post a line for an item whose icon is None, and the landmine's
        // item data carries exactly that: the mine credits itself through
        // EliminationReason.Landmine and ShowEliminationMessage rather than through
        // ShowItemHitMessage. A seize eliminates nobody, so the mine takes the other route and
        // needs an icon of its own — the mine's own feed icon, which exists in the enum even
        // though the item data never references it.
        data.HitInfoFeedMessageIcon = InfoFeedIconType.Landmine;

        // Null so LocalizedName resolves for our own key rather than inheriting "Landmine".
        data.name = null;
        data.Initialize();

        Plugin.Log.LogInfo($"Built Shock Mine item data as ItemType {(int)Type}.");
        return data;
    }

    private static GameObject BuildPrefab(GameObject donorPrefab)
    {
        // Instantiating an active prefab runs the clone's Awake immediately, and PhysicalItem
        // .Awake throws when it runs outside the game's own spawn flow. Deactivating the source
        // across the Instantiate keeps the clone dormant until the game spawns it properly.
        bool wasActive = donorPrefab.activeSelf;
        donorPrefab.SetActive(false);

        GameObject prefab;
        try
        {
            prefab = UnityEngine.Object.Instantiate(donorPrefab);
        }
        finally
        {
            donorPrefab.SetActive(wasActive);
        }

        prefab.name = "ShockMineItem";

        // The clone carries the donor's serialized item type, so picking a dropped mine back up
        // would hand the player an actual landmine.
        if (prefab.TryGetComponent(out PhysicalItem physical))
        {
            physical.itemType = Type;
        }
        else
        {
            Plugin.Log.LogWarning("Shock Mine pickup has no PhysicalItem component.");
        }

        // What every Landmine patch keys off. Added before the prefab is registered with Mirror
        // so remote copies are built with it already attached.
        if (prefab.GetComponent<Landmine>() == null)
        {
            Plugin.Log.LogError(
                "The landmine prefab has no Landmine component; the mine will never arm or trip.");
        }

        prefab.AddComponent<ShockMarker>();

        // A runtime template has to live in a scene, unlike a real prefab asset, so parking it
        // under a deactivated holder is what keeps it dormant. Leaving the template itself
        // inactive instead would make every dropped copy inherit activeSelf = false and spawn
        // invisible.
        prefab.transform.SetParent(PrefabHolder.transform, worldPositionStays: false);
        prefab.SetActive(true);

        Recolour(prefab);
        RegisterNetworkPrefab(prefab);
        return prefab;
    }

    /// <summary>
    /// Dropped items are spawned across the network, and Mirror identifies the prefab to build by
    /// asset id. The clone inherits the landmine's id, which would make every other player's
    /// client build the game's own mine instead. Giving it a distinct id and registering that on
    /// every peer means everyone sees the same device on the ground.
    /// </summary>
    private static void RegisterNetworkPrefab(GameObject prefab)
    {
        if (!prefab.TryGetComponent(out NetworkIdentity identity))
        {
            Plugin.Log.LogWarning("Shock Mine pickup has no NetworkIdentity; it will not spawn when dropped.");
            return;
        }

        // RegisterPrefab refuses an id change when the prefab already carries one, and our clone
        // inherited the landmine's. Mirror exposes assetId as read-only, so the backing field is
        // cleared directly before handing Mirror the id we want.
        FieldInfo? assetIdField = AccessTools.Field(typeof(NetworkIdentity), "_assetId");
        if (assetIdField is null)
        {
            Plugin.Log.LogWarning("Mirror's assetId field was not found; dropped mines may appear as ordinary landmines.");
            return;
        }

        assetIdField.SetValue(identity, 0u);
        NetworkClient.RegisterPrefab(prefab, NetworkAssetId);

        Plugin.Log.LogInfo($"Registered the Shock Mine pickup as network asset {identity.assetId}.");
    }

    /// <summary>
    /// Mirror clears every registered prefab when a client shuts down, so a registration made at
    /// startup is gone by the time the player joins a lobby and the spawned mine silently fails
    /// to appear. Re-registering whenever the client starts keeps it available. Registering the
    /// same prefab under the id it already holds is a no-op for Mirror.
    /// </summary>
    public static void EnsureNetworkPrefabRegistered()
    {
        if (itemData?.Prefab == null)
        {
            return;
        }

        // Checked rather than re-registered blindly. RegisterPrefab logs a warning every time it
        // replaces an entry, and this runs each frame — the first test run produced 8,702 of
        // them in a single session.
        if (NetworkClient.prefabs.TryGetValue(NetworkAssetId, out GameObject existing)
            && existing == itemData.Prefab)
        {
            return;
        }

        NetworkClient.RegisterPrefab(itemData.Prefab, NetworkAssetId);
    }

    /// <summary>The registered prefab, or null before the item data has been built.</summary>
    public static GameObject? Prefab => itemData?.Prefab;

    /// <summary>Tints every renderer on a cloned device.</summary>
    internal static void Recolour(GameObject target)
    {
        foreach (Renderer renderer in target.GetComponentsInChildren<Renderer>(includeInactive: true))
        {
            foreach (Material material in renderer.materials)
            {
                if (material.HasProperty("_BaseColor"))
                {
                    material.SetColor("_BaseColor", DeviceColor);
                }

                if (material.HasProperty("_Color"))
                {
                    material.SetColor("_Color", DeviceColor);
                }
            }
        }
    }

    private static Sprite? BuildIcon()
    {
        using Stream? stream = typeof(ShockItem).Assembly
            .GetManifestResourceStream("ShockMine.Assets.shock-mine-icon.png");

        if (stream is null)
        {
            Plugin.Log.LogWarning("Embedded Shock Mine icon missing; the item will show no icon.");
            return null;
        }

        byte[] bytes = new byte[stream.Length];
        _ = stream.Read(bytes, 0, bytes.Length);

        Texture2D texture = new(2, 2, TextureFormat.RGBA32, mipChain: false);
        if (!texture.LoadImage(bytes))
        {
            Plugin.Log.LogWarning("Shock Mine icon failed to decode; the item will show no icon.");
            return null;
        }

        texture.name = "ShockMineIcon";
        UnityEngine.Object.DontDestroyOnLoad(texture);

        Sprite sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f));
        sprite.name = "ShockMineIcon";
        UnityEngine.Object.DontDestroyOnLoad(sprite);
        return sprite;
    }
}

[HarmonyPatch(typeof(ItemCollection), nameof(ItemCollection.Initialize))]
internal static class ItemCollectionInitializePatch
{
    private static void Postfix(ItemCollection __instance) => ShockItem.Register(__instance);
}

/// <summary>
/// The game builds item names from a localization key (ITEM_&lt;type&gt;) that does not exist for a
/// modded item, so supply the display name directly.
/// </summary>
[HarmonyPatch(typeof(ItemData), nameof(ItemData.Name), MethodType.Getter)]
internal static class ItemDataNamePatch
{
    private static bool Prefix(ItemData __instance, ref string __result)
    {
        if (__instance.Type != ShockItem.Type)
        {
            return true;
        }

        __result = ShockItem.DisplayName;
        return false;
    }
}
