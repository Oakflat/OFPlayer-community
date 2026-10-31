using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using OFPlayer.WinUI.Core.Protocol;

namespace OFPlayer.WinUI.Core;

/// <summary>
/// The plain sentence a listener can act on when something fails. An exception's own text
/// is an engineering detail (English, HTTP statuses, protocol words): it goes to the log,
/// and the window shows one of these instead. Anything unrecognised is the generic retry
/// sentence, so a new kind of failure can never leak its raw text.
/// </summary>
public static class FailureReason
{
    public const string Network = "failure.network";
    public const string Credentials = "failure.credentials";
    public const string NotFound = "failure.notFound";
    public const string Audio = "failure.audio";
    public const string LocalFile = "failure.file";
    public const string Generic = "failure.generic";
    public const string HostTimeout = "failure.hostTimeout";
    public const string HostUnavailable = "failure.hostUnavailable";
    public const string Protocol = "failure.protocol";
    public const string InvalidInput = "failure.invalidInput";
    public const string Permission = "failure.permission";
    public const string Conflict = "failure.conflict";
    public const string RateLimited = "failure.rateLimited";
    public const string ServiceUnavailable = "failure.serviceUnavailable";
    public const string Timeout = "failure.timeout";
    public const string Cancelled = "failure.cancelled";
    public const string DiskFull = "failure.diskFull";

    public static string LocalizationKey(Exception error) => error switch
    {
        AggregateException aggregate when aggregate.Flatten().InnerExceptions.Count == 1 =>
            LocalizationKey(aggregate.Flatten().InnerExceptions[0]),
        HostRequestTimeoutException => HostTimeout,
        HostConnectionException or HostClosedException => HostUnavailable,
        HostProtocolException => Protocol,
        HostCommandException command => ByHostError(command),
        HttpRequestException http when http.StatusCode is { } status => ByHttpStatus((int)status),
        // An HTTP timeout is cancellation with a timeout cause; explicit cancellation
        // is a separate outcome and must not be misreported as a network failure.
        OperationCanceledException when error.InnerException is TimeoutException => Timeout,
        OperationCanceledException cancelled when cancelled.CancellationToken.IsCancellationRequested => Cancelled,
        OperationCanceledException => Generic,
        TimeoutException => Timeout,
        IOException when TryCloudStatus(error.Message, out var status) => ByHttpStatus(status),
        IOException when error.Message.StartsWith("cloud-request-", StringComparison.Ordinal) => Protocol,
        IOException when error.Message == "cloud-organization-invalid" => Protocol,
        IOException when (error.HResult & 0xffff) is 0x70 or 0x27 => DiskFull,
        HttpRequestException or SocketException => Network,
        UnauthorizedAccessException => Permission,
        FileNotFoundException or DirectoryNotFoundException => NotFound,
        IOException => LocalFile,
        ArgumentException => InvalidInput,
        JsonException => Protocol,
        _ => ByMessage(error.Message),
    };

    private static string ByHostError(HostCommandException error) => error.Code switch
    {
        HostProtocol.ErrorCodes.ProtocolViolation or HostProtocol.ErrorCodes.UnsupportedProtocolVersion
            or HostProtocol.ErrorCodes.MissingProtocolVersion or HostProtocol.ErrorCodes.UnknownCommand => Protocol,
        HostProtocol.ErrorCodes.InvalidArguments => InvalidInput,
        "permission-denied" or "access-denied" or "forbidden" => Permission,
        "not-found" => NotFound,
        "conflict" or "stale-revision" => Conflict,
        _ => ByMessage(error.Message),
    };

    private static bool TryCloudStatus(string message, out int status)
    {
        const string prefix = "cloud-request-";
        status = 0;
        return message.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(message.AsSpan(prefix.Length), out status) && status is >= 100 and <= 599;
    }

    private static string ByHttpStatus(int status) => status switch
    {
        400 or 422 => InvalidInput,
        401 => Credentials,
        403 => Permission,
        404 or 410 => NotFound,
        408 or 504 => Timeout,
        409 or 412 => Conflict,
        429 => RateLimited,
        >= 500 => ServiceUnavailable,
        _ => Network,
    };

    /// <summary>
    /// The native host reports external-library failures as English sentences. They are only
    /// used to pick a category; the sentence itself is never shown.
    /// </summary>
    private static string ByMessage(string message)
    {
        var http = Regex.Match(message, @"\bHTTP\s+(\d{3})\b", RegexOptions.CultureInvariant);
        if (http.Success && int.TryParse(http.Groups[1].Value, out var status)) return ByHttpStatus(status);
        if (message.Contains("wrong username or password", StringComparison.OrdinalIgnoreCase)) return Credentials;
        if (message.Contains("request failed:", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Failed to read WebDAV response", StringComparison.Ordinal)
            || message.Contains("download failed", StringComparison.OrdinalIgnoreCase))
            return Network;
        if (message.Contains("not supported or the file is damaged", StringComparison.Ordinal)
            || message.Contains("invalid container header", StringComparison.Ordinal))
            return Audio;
        return Generic;
    }
}
