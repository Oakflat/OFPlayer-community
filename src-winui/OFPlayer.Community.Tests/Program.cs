using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using OFPlayer.Community.Services;
using OFPlayer.WindowsMedia;

var root = Path.Combine(Path.GetTempPath(), "ofplayer-community-managed-" + Guid.NewGuid());
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
try {
    var clock = new Clock { Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z") };
    var success = new Handler();
    await using (var activity = new DailyActivity(Path.Combine(root, "success"), clock, success)) {
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => activity.RecordAsync()));
        Check(success.Payloads.Count == 1, "Concurrent activity sends once per UTC day");
        for (var i = 0; i < 1000; ++i) await activity.RecordAsync();
        Check(success.Payloads.Count == 1, "Playing ticks use the cached daily gate");
        var body = success.Payloads[0];
        Check(body.Select(p => p.Key).Order().SequenceEqual(new[] { "appVersion", "dailyId", "day", "platform", "releaseChannel" }), "Payload contains only the declared minimal fields");
        Check(body["dailyId"]!.GetValue<string>().Length == 64 && success.CookieSeen == false, "Daily identity is random and no cookie is attached");
        clock.Now = clock.Now.AddDays(1);
        await activity.RecordAsync();
        Check(success.Payloads.Count == 2 && body["dailyId"]!.ToString() != success.Payloads[1]["dailyId"]!.ToString(), "Next UTC day rotates identity during activity");
    }
    var restart = new Handler();
    await using (var activity = new DailyActivity(Path.Combine(root, "success"), clock, restart)) {
        await activity.RecordAsync();
        Check(restart.Payloads.Count == 0, "Restart does not resend the same day");
    }
    var offline = new Handler { Offline = true };
    await using (var activity = new DailyActivity(Path.Combine(root, "offline"), clock, offline)) {
        await activity.RecordAsync(); await activity.RecordAsync();
        Check(offline.Payloads.Count == 1, "Offline failure is contained and backs off");
        offline.Offline = false; clock.Now = clock.Now.AddHours(1); await activity.RecordAsync();
        Check(offline.Payloads.Count == 2 && offline.Payloads[0]["dailyId"]!.ToString() == offline.Payloads[1]["dailyId"]!.ToString(), "Retry retains daily identity for server deduplication");
    }
    var busy = new Handler { Status = HttpStatusCode.ServiceUnavailable, Retry = TimeSpan.FromHours(24) };
    await using (var activity = new DailyActivity(Path.Combine(root, "busy"), clock, busy)) {
        await activity.RecordAsync(); clock.Now = clock.Now.AddHours(12); await activity.RecordAsync();
        Check(busy.Payloads.Count == 1, "Server backoff persists across UTC day boundary");
    }
    var afterBusy = new Handler();
    await using (var activity = new DailyActivity(Path.Combine(root, "busy"), clock, afterBusy)) {
        await activity.RecordAsync(); Check(afterBusy.Payloads.Count == 0, "Server pause survives process restart");
    }
    var local = Path.Combine(root, "local.mp4"); await File.WriteAllBytesAsync(local, [0]);
    Check(LocalMediaPath.Validate(local) == local, "Existing local drive path accepted");
    foreach (var path in new[] { "https://example.invalid/video.mp4", @"\\server\share\video.mp4", @"\\?\C:\video.mp4", @"C:video.mp4", @"C:\video.mp4:stream", "relative.mp4" }) {
        try { LocalMediaPath.Validate(path); throw new Exception("Accepted non-local path: " + path); }
        catch (ArgumentException) { }
    }
    Check(true, "Remote, UNC, device, relative and alternate-stream video inputs rejected");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { passed = checks.Count, failed = 0, checks, scope = "temporary fixtures and fake HTTP only" }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
} finally { Directory.Delete(root, true); }

sealed class Clock : TimeProvider { public DateTimeOffset Now; public override DateTimeOffset GetUtcNow() => Now; }
sealed class Handler : HttpMessageHandler
{
    public List<JsonObject> Payloads { get; } = [];
    public bool Offline, CookieSeen;
    public HttpStatusCode Status = HttpStatusCode.NoContent;
    public TimeSpan? Retry;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri!.AbsoluteUri != "https://api.ofplayer.org/v1/telemetry/activity") throw new Exception("Unexpected endpoint");
        CookieSeen |= request.Headers.Contains("Cookie");
        Payloads.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!.AsObject());
        if (Offline) throw new HttpRequestException("Offline fixture");
        var response = new HttpResponseMessage(Status);
        if (Retry is { } delay) response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }
}
