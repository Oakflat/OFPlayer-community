using System.Globalization;
using System.Text.RegularExpressions;

namespace OFPlayer.WinUI.Core;

/// <summary>A failure keeps its cause and operation even when its public sentence is localized.</summary>
public sealed record FailureReport(string Reference, string Code, string Operation, string ReasonKey, string Details)
{
    public static FailureReport Create(Exception error, string context)
    {
        var cause = error is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Count == 1
            ? aggregate.Flatten().InnerExceptions[0] : error;
        var reference = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var operation = cause switch
        {
            HostCommandException { Command: { Length: > 0 } command } => command,
            HostRequestTimeoutException timeout => timeout.Command,
            _ => context,
        };
        var code = cause switch
        {
            HostCommandException command => command.Code,
            HostRequestTimeoutException => "host-request-timeout",
            HostProtocolException => "host-protocol-error",
            HostConnectionException or HostClosedException => "host-unavailable",
            HttpRequestException { StatusCode: { } status } => $"http-{(int)status}",
            _ => $"{cause.GetType().Name}:0x{cause.HResult:X8}",
        };
        var details = $"Reference: {reference}\nTime (UTC): {DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}\n" +
            $"Operation: {operation}\nCode: {code}\n\n{error}";
        return new(reference, Redact(code), Redact(operation), FailureReason.LocalizationKey(cause), Redact(details));
    }

    // Diagnostic details are local and user-copyable. Do not include request arguments;
    // scrub common secrets carried by exceptions while retaining paths and stack frames.
    public static string Redact(string text)
    {
        text = Regex.Replace(text, @"(?i)\bBearer\s+[^\s,;""']+", "Bearer [redacted]", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"(?i)(\b(?:password|passwd|access_token|refresh_token|id_token|token|api[_-]?key|client_secret|authorization)\b[""']?\s*[:=]\s*[""']?)[^\s&;,""']+", "$1[redacted]", RegexOptions.CultureInvariant);
        return Regex.Replace(text, @"(?i)(https?://)[^/\s:@]+:[^/\s@]+@", "$1[redacted]@", RegexOptions.CultureInvariant);
    }
}
