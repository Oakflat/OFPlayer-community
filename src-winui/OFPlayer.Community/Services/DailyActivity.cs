using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace OFPlayer.Community.Services;

/// <summary>One anonymous daily event. Network work never runs on the playback thread.</summary>
internal sealed class DailyActivity : IAsyncDisposable
{
    private sealed record State(string Day, string DailyId, bool Sent, DateTimeOffset AttemptAt, DateTimeOffset PausedUntil);
    private readonly string path;
    private readonly TimeProvider clock;
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly HttpClient http;
    private Task? work;
    private long nextCheckTicks;
    private bool disposed;

    public DailyActivity(string dataRoot) : this(dataRoot, TimeProvider.System,
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    { }

    internal DailyActivity(string dataRoot, TimeProvider clock, HttpMessageHandler handler)
    {
        path = Path.Combine(dataRoot, "daily-activity.json");
        this.clock = clock;
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }

    public void Record() => _ = RecordAsync();

    internal Task RecordAsync()
    {
        lock (sync)
        {
            if (disposed) return Task.CompletedTask;
            if (work is { IsCompleted: false }) return work;
            // Playback ticks can call this safely: no task, file I/O or HTTP before due.
            if (clock.GetUtcNow().UtcTicks < Interlocked.Read(ref nextCheckTicks)) return Task.CompletedTask;
            return work = Task.Run(SendAsync);
        }
    }

    private void Defer(DateTimeOffset until) => Interlocked.Exchange(ref nextCheckTicks, until.UtcTicks);

    private async Task SendAsync()
    {
        var now = clock.GetUtcNow();
        Defer(now.AddHours(1));
        try
        {
            var day = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            State? state = null;
            if (File.Exists(path) && new FileInfo(path).Length <= 4096)
            {
                try { state = JsonSerializer.Deserialize<State>(await File.ReadAllBytesAsync(path, lifetime.Token)); }
                catch (JsonException) { /* Recover an interrupted/corrupt local state file. */ }
            }
            if (state is not null)
            {
                if (state.PausedUntil > now) { Defer(state.PausedUntil); return; }
                if (string.CompareOrdinal(state.Day, day) > 0) return; // Do not duplicate after clock rollback.
                if (state.Day == day && state.Sent) { Defer(now.UtcDateTime.Date.AddDays(1)); return; }
                if (state.Day == day && now - state.AttemptAt < TimeSpan.FromHours(1)) { Defer(state.AttemptAt.AddHours(1)); return; }
            }
            if (state?.Day != day || state.DailyId is not { Length: 64 } || state.DailyId.Any(c => !Uri.IsHexDigit(c)))
                state = new(day, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), false, now, default);
            state = state with { AttemptAt = now };
            await SaveAsync(state);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.ofplayer.org/v1/telemetry/activity")
            {
                Content = JsonContent.Create(new { day, dailyId = state.DailyId, platform = "windows", appVersion = "0.0.0-community.dev", releaseChannel = "dev" })
            };
            request.Headers.Add("X-OFPlayer-Trace-Id", Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant());
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            if (response.IsSuccessStatusCode)
            {
                await SaveAsync(state with { Sent = true });
                Defer(now.UtcDateTime.Date.AddDays(1));
            }
            else if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests)
            {
                var retry = response.Headers.RetryAfter;
                var wait = retry?.Delta ?? (retry?.Date is { } date ? date - now : TimeSpan.FromHours(24));
                var until = now.AddSeconds(Math.Clamp(wait.TotalSeconds, 3600, 7 * 86400));
                await SaveAsync(state with { PausedUntil = until });
                Defer(until);
            }
        }
        catch (Exception) { /* Retry on later real activity; never fail playback or send an idle heartbeat. */ }
    }

    private async Task SaveAsync(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(state), lifetime.Token);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (sync) { if (disposed) return; disposed = true; pending = work; }
        lifetime.Cancel();
        if (pending is not null) await pending;
        http.Dispose();
        lifetime.Dispose();
    }
}
