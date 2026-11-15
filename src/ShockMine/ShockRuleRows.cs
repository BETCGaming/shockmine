using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.Tables;

namespace ShockMine;

/// <summary>
/// Puts the seize duration in the match setup's Battle section, as a row beside the game's own
/// battle rules, rather than leaving it a host-only config key nobody in the lobby can see.
///
/// The rules machinery takes a modded value far more easily than the item pools did: every
/// lookup is a dictionary keyed by Rule rather than an array indexed by it. So the only real
/// work is the row itself, which unlike the item sliders is authored in the scene rather than
/// generated, and therefore has to be cloned from one of its neighbours.
/// </summary>
[HarmonyPatch(typeof(MatchSetupRules), nameof(MatchSetupRules.Initialize))]
internal static class MatchSetupRuleRowPatch
{
    private const string DurationKey = "RULE_SHOCK_MINE_SEIZE_DURATION";
    private const string DurationText = "Shock Mine seize duration";

    /// <summary>The row we built, so entering the lobby again does not stack up copies.</summary>
    private static DropdownOption? durationRow;

    /// <summary>Which screen that row belongs to, so a rebuilt screen gets a row of its own.</summary>
    private static int ownerId;

    /// <summary>Where the rule labels live, learned from the row we cloned rather than assumed.</summary>
    private static TableReference labelTable;
    private static bool labelTableKnown;
    private static bool languageHookInstalled;

    /// <summary>
    /// A postfix, unlike the item probabilities: the game's own InitDropdown calls happen inside
    /// Initialize, and ours has to follow them.
    /// </summary>
    private static void Postfix(MatchSetupRules __instance)
    {
        if (!Plugin.Enabled.Value)
        {
            return;
        }

        try
        {
            Build(__instance);
        }
        catch (Exception e)
        {
            // Nothing to undo and no gameplay path to restore: without the row the rule simply
            // resolves to its default of five seconds.
            Plugin.Log.LogError(
                $"Could not add the Shock Mine row to the match setup: {e} The seize duration "
                + "stays at its default.");
        }
    }

    private static void Build(MatchSetupRules rules)
    {
        // Anything that groups rules by section should see ours in the right one.
        MatchSetupRules.CategoryPerRule[ShockItem.SeizeDurationRule] = MatchSetupRules.RuleCategory.Battle;

        // A destroyed row reads as null through Unity's own comparison, and a screen rebuilt
        // from scratch needs a row of its own rather than one pointing into the old screen.
        if (durationRow == null || ownerId != rules.GetInstanceID())
        {
            durationRow = Clone(rules, "ShockMineSeizeDuration", DurationKey, DurationText);
        }

        ownerId = rules.GetInstanceID();

        if (durationRow == null)
        {
            return;
        }

        // Re-asserted on every visit, and always *before* InitDropdown: Initialize applies the
        // stored value as an index into whatever options the control currently holds, so the
        // four durations have to be in place first or the value lands on the wrong one.
        SetDurationOptions(durationRow);

        // Re-initialized on every visit, exactly as the game does with its own rows: this is
        // what registers it in the screen's lookups and sets the control from the current value,
        // so a second visit does not show a stale one.
        AccessTools.Method(typeof(MatchSetupRules), "InitDropdown")
            .Invoke(rules, new object[] { durationRow, ShockItem.SeizeDurationRule, null! });

        if (durationRow.OptionsCount != ShockRules.DurationOptions.Length)
        {
            Plugin.Log.LogWarning(
                $"The seize duration row has {durationRow.OptionsCount} options rather than "
                + $"{ShockRules.DurationOptions.Length}; something is repopulating it.");
        }
    }

    private static DropdownOption? Clone(MatchSetupRules rules, string name, string key, string text)
    {
        DropdownOption source = rules.hitOtherPlayersBalls;
        if (source == null)
        {
            Plugin.Log.LogWarning($"No battle row to copy; '{text}' has no control.");
            return null;
        }

        GameObject clone = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent);
        clone.name = name;
        clone.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);

        DropdownOption option = clone.GetComponent<DropdownOption>();
        if (option == null)
        {
            UnityEngine.Object.Destroy(clone);
            Plugin.Log.LogWarning($"The copied battle row has no DropdownOption; leaving '{text}' out of the UI.");
            return null;
        }

        // Muzzle the clone's localizer before anything asks it to refresh.
        LocalizedDropdownSuppressor.Suppress(option.Localized);

        Label(clone, key, text);

        Plugin.Log.LogInfo($"Added '{text}' to the match setup's Battle section.");
        return option;
    }

    /// <summary>
    /// Replaces the cloned on/off row's options with the four durations.
    ///
    /// The row arrives wired to a <see cref="LocalizeDropdown"/> that repopulates it with On and
    /// Off. Destroying that component looked like the fix and was not: the field on
    /// <c>DropdownOption</c> still pointed at it, and <c>SetValue</c> calls
    /// <c>RefreshOptions</c> on it unguarded, so a destroyed component threw a
    /// NullReferenceException out of <c>InitDropdown</c>. It is left alive and silenced instead —
    /// see <see cref="LocalizedDropdownSuppressor"/>.
    ///
    /// The durations are bare numbers and read the same in any language.
    /// </summary>
    private static void SetDurationOptions(DropdownOption row)
    {
        LocalizedDropdownSuppressor.Suppress(row.Localized);

        List<string> options = new(ShockRules.DurationOptions.Length);
        foreach (float seconds in ShockRules.DurationOptions)
        {
            options.Add(seconds.ToString("0", CultureInfo.InvariantCulture) + "s");
        }

        row.SetOptions(options);
    }

    /// <summary>
    /// The clone arrives labelled as the row it was copied from, so only this label needs
    /// changing.
    /// </summary>
    private static void Label(GameObject clone, string key, string text)
    {
        LocalizeStringEvent? label = clone.GetComponentInChildren<LocalizeStringEvent>(includeInactive: true);
        if (label == null)
        {
            Plugin.Log.LogWarning("The copied row has no localized label; it will read as the row it was copied from.");
            return;
        }

        // The table is read off the row we copied rather than guessed: the item name lives in
        // the Data table, and there is no reason the rule labels have to.
        labelTable = label.StringReference.TableReference;
        labelTableKnown = true;

        if (AddEntry(key, text))
        {
            label.StringReference.TableEntryReference = key;
            label.RefreshString();
            InstallLanguageHook();
            return;
        }

        // The table was not available. Plain text on the label beats showing a raw key, and the
        // component has to go or it would put the copied row's text back.
        TMP_Text? tmp = label.GetComponent<TMP_Text>();
        UnityEngine.Object.Destroy(label);

        if (tmp != null)
        {
            tmp.text = text;
            Plugin.Log.LogWarning($"Localization was not ready; '{text}' is labelled in English only.");
        }
    }

    private static bool AddEntry(string key, string text)
    {
        if (!labelTableKnown)
        {
            return false;
        }

        try
        {
            // Fully qualified: the game has its own StringTable enum in the global namespace.
            UnityEngine.Localization.Tables.StringTable? table =
                LocalizationSettings.StringDatabase?.GetTable(labelTable);
            if (table == null)
            {
                return false;
            }

            table.AddEntry(key, text);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Could not name the rule '{text}': {e.Message}");
            return false;
        }
    }

    /// <summary>Each locale has its own table, so the entry has to be re-added on a switch.</summary>
    private static void InstallLanguageHook()
    {
        if (languageHookInstalled)
        {
            return;
        }

        LocalizationManager.LanguageChanged += () => AddEntry(DurationKey, DurationText);
        languageHookInstalled = true;
    }
}

/// <summary>
/// Stops a specific <see cref="LocalizeDropdown"/> from repopulating its control, without
/// destroying it.
///
/// Our duration row is a clone of an on/off row, and its localizer puts On and Off back every
/// time the control's value is set. The component cannot simply be removed: <c>DropdownOption</c>
/// holds a serialized reference to it and calls <c>RefreshOptions</c> through that reference with
/// no null guard, so a destroyed one throws. Silencing the method for just our instance leaves
/// every other row in the screen localizing normally.
/// </summary>
[HarmonyPatch(typeof(LocalizeDropdown), "RefreshOptions")]
internal static class LocalizedDropdownSuppressor
{
    private static readonly HashSet<int> Suppressed = new();

    public static void Suppress(LocalizeDropdown? localized)
    {
        if (localized != null)
        {
            Suppressed.Add(localized.GetInstanceID());
        }
    }

    private static bool Prefix(LocalizeDropdown __instance) =>
        !Plugin.Enabled.Value || !Suppressed.Contains(__instance.GetInstanceID());
}
