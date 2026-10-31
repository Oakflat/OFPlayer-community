using OFPlayer.WinUI.Core;

namespace OFPlayer_WinUI.Services;
/// <summary>
/// What the window says about a failure. The exception goes to the log with its context;
/// the listener reads one plain sentence chosen by <see cref = "FailureReason"/>.
/// </summary>
internal static class FailureText
{
    public static event Action<FailureReport>? Reported;
    public static FailureReport Capture(Exception error, string context, bool notify = true)
    {
        var report = FailureReport.Create(error, context);
        PreviewLog.Write(report.Details);
        if (notify)
        {
            try
            {
                Reported?.Invoke(report);
            }
            catch (Exception presentationError)
            {
                // Reporting must not replace the original failure with a UI/dispatcher error.
                PreviewLog.Write(FailureReport.Create(presentationError, "Failure presentation").Details);
            }
        }

        return report;
    }

    /// <summary>Logs the failure once and returns the string-table key of its plain reason.
    /// Use it where the text is rebuilt on a language change, so the log is written only once.</summary>
    public static string Key(Exception error, string context)
    {
        return Capture(error, context).ReasonKey;
    }

    public static string Reason(Exception error, string context) => Summary(Capture(error, context));
    public static string Summary(FailureReport report) => Localization.Format("failure.withReference", ("reason", Localization.Get(report.ReasonKey)), ("reference", report.Reference));
    public static string Operation(FailureReport report)
    {
        var operation = report.Operation.ToLowerInvariant();
        var key = operation switch
        {
            var value when value.Contains("navigation") => "navigation",
            var value when value.Contains("diagnostic") => "diagnostics",
            var value when value.Contains("license") || value.Contains("licensing") => "licensing",
            var value when value.Contains("account") || value.Contains("login") => "account",
            var value when value.Contains("lyric") => "lyrics",
            var value when value.Contains("preference") || value.Contains("settings") => "settings",
            var value when value.Contains("import") || value.Contains("scan") => "import",
            var value when value.Contains("playback") || value.StartsWith("play") || value.Contains("output") => "playback",
            var value when value.Contains("catalog") || value.Contains("library") || value.Contains("playlist") || value.Contains("track") => "library",
            _ => "action",
        };
        return Localization.Get("failure.operation." + key);
    }
}
