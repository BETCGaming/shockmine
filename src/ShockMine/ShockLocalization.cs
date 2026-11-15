using System;
using HarmonyLib;
using UnityEngine.Localization.Settings;

namespace ShockMine;

/// <summary>
/// Gives the Shock Mine a readable name everywhere the game asks for one.
///
/// Item names are looked up in the Data string table under "ITEM_&lt;type&gt;". Our type has no
/// entry, so Unity Localization falls back to printing the table and key — a "Data/ITEM_201" on
/// the pickup prompt. Adding the entry at runtime fixes every place that name appears at once,
/// rather than patching each piece of UI.
/// </summary>
internal static class ShockLocalization
{
    private static bool applied;

    public static void Initialize()
    {
        // Each locale has its own table, so the entry has to be re-added when the player
        // switches language.
        LocalizationManager.LanguageChanged += () => applied = false;
    }

    /// <summary>Retried from <see cref="ModRunner"/> until the table has finished loading.</summary>
    public static void EnsureApplied()
    {
        if (applied)
        {
            return;
        }

        try
        {
            // Fully qualified: the game has its own StringTable enum in the global namespace.
            UnityEngine.Localization.Tables.StringTable? table =
                LocalizationSettings.StringDatabase?.GetTable(global::StringTable.Data.ToString());

            if (table == null)
            {
                return;
            }

            string key = $"ITEM_{(int)ShockItem.Type}";
            table.AddEntry(key, ShockItem.DisplayName);

            applied = true;
            Plugin.Log.LogInfo($"Registered the name '{ShockItem.DisplayName}' as {key}.");
        }
        catch (Exception e)
        {
            applied = true;
            Plugin.Log.LogWarning($"Could not name the Shock Mine, its name may show as a key: {e.Message}");
        }
    }
}

/// <summary>
/// The player animator selects hold and use animations from an "Equipped item" integer set to the
/// ItemType. Our value matches no state, which would leave the mine sitting in the hand in the
/// default pose with no planting or tossing animation. Presenting it to the animator as the
/// landmine gives it the device's own animations, which is what it is built from.
/// </summary>
[HarmonyPatch(typeof(PlayerAnimatorIo), nameof(PlayerAnimatorIo.SetEquippedItem))]
internal static class AnimatorEquippedItemPatch
{
    private static void Prefix(ref ItemType equippedItem)
    {
        if (equippedItem == ShockItem.Type)
        {
            equippedItem = ItemType.Landmine;
        }
    }
}
