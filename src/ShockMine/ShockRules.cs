using HarmonyLib;
using UnityEngine;

namespace ShockMine;

/// <summary>
/// Reads the mine's seize duration rule.
///
/// The rules machinery takes a modded value easily — every lookup is a dictionary keyed by Rule
/// rather than an array indexed by it — but <c>GetDefaultValue</c> ends in a default arm
/// returning zero, which is the wrong answer here: the duration's stored value is a dropdown
/// index, so unpatched it would default to the first option rather than to five seconds. Hence
/// <see cref="DefaultValuePatch"/>, which is preferable to reordering the dropdown so 5 sits
/// first — a list reading 5 / 3 / 7 / 9 is a strange thing to ship.
/// </summary>
internal static class ShockRules
{
    /// <summary>Selectable hold lengths, in the order they appear in the dropdown.</summary>
    internal static readonly float[] DurationOptions = { 3f, 5f, 7f, 9f };

    /// <summary>Index into <see cref="DurationOptions"/> for the five-second default.</summary>
    internal const int DefaultDurationIndex = 1;

    /// <summary>How long a seize lasts, as the host has set it for this match.</summary>
    public static float SeizeSeconds
    {
        get
        {
            int index = Mathf.Clamp(
                (int)MatchSetupRules.GetValue(ShockItem.SeizeDurationRule),
                0,
                DurationOptions.Length - 1);

            return DurationOptions[index];
        }
    }
}

/// <summary>
/// Supplies the duration rule's default. Without this it lands on the first dropdown option
/// rather than on five seconds.
/// </summary>
[HarmonyPatch(typeof(MatchSetupRules), "GetDefaultValue")]
internal static class DefaultValuePatch
{
    private static bool Prefix(MatchSetupRules.Rule rule, ref float __result)
    {
        if (rule == ShockItem.SeizeDurationRule)
        {
            __result = ShockRules.DefaultDurationIndex;
            return false;
        }

        return true;
    }
}
