namespace ShockMine;

/// <summary>Step-by-step logging for diagnosing a mine that never fires or never lets go.</summary>
internal static class Trace
{
    public static void Log(string message)
    {
        if (Plugin.VerboseLogging.Value)
        {
            Plugin.Log.LogInfo($"[trace] {message}");
        }
    }
}
