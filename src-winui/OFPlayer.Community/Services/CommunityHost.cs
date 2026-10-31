using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OFPlayer.Community.Services;

internal sealed class CommunityHost : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> pending = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private Process? process;
    private Task? reader, errors;
    private long nextId;
    public string DataRoot { get; } = ResolveDataRoot();
    public bool IsFixture
    {
        get
        {
#if DEBUG
            return File.Exists(Path.Combine(DataRoot, ".community-fixture"));
#else
            return false;
#endif
        }
    }
    private static string ResolveDataRoot()
    {
#if DEBUG
        var fixture = Environment.GetEnvironmentVariable("OFPLAYER_COMMUNITY_FIXTURE_ROOT");
        if (!string.IsNullOrWhiteSpace(fixture))
        {
            var full = Path.GetFullPath(fixture);
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ofplayer-community-fixtures")) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(full, ".community-fixture")))
                throw new InvalidOperationException("Development fixture root must be a marked directory inside the temporary fixture workspace.");
            return full;
        }
#endif
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ofplayer-community");
    }
    public event Action<JsonObject>? PlaybackChanged;
    public event Action<string>? Disconnected;

    public async Task StartAsync()
    {
        await MigrateLegacyAsync();
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ofplayer-community-host.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("--data-root"); start.ArgumentList.Add(DataRoot);
        process = Process.Start(start) ?? throw new IOException("无法启动本地播放内核。");
        OwnedChildProcessGroup.Attach(process);
        reader = ReadAsync();
        errors = Task.Run(async () => { while (await process.StandardError.ReadLineAsync() is { } line) Trace.WriteLine(line); });
        var handshake = await CallAsync("host_handshake");
        if (handshake?["edition"]?.GetValue<string>() != "community") throw new InvalidDataException("Community 播放内核不匹配。");
    }
    public async Task MigrateLegacyAsync()
    {
        if (IsFixture) return;
        if (Directory.Exists(DataRoot)) return;
        var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ofplayer");
        if (!File.Exists(Path.Combine(source, "ofplayer-desktop.sqlite3"))) return;
        var old = Process.GetProcessesByName("ofplayer-tauri");
        try { if (old.Length > 0) throw new IOException("请先退出旧版 Tauri 播放器，再打开 Community 以迁入本地曲库。原数据将保留。"); }
        finally { foreach (var item in old) item.Dispose(); }
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ofplayer-community-host.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("--migrate-tauri"); start.ArgumentList.Add(source); start.ArgumentList.Add(DataRoot);
        using var migration = Process.Start(start) ?? throw new IOException("无法启动旧版曲库迁移。");
        OwnedChildProcessGroup.Attach(migration);
        var output = migration.StandardOutput.ReadToEndAsync();
        var error = migration.StandardError.ReadToEndAsync();
        await migration.WaitForExitAsync();
        if (migration.ExitCode != 0) throw new IOException("旧版曲库迁移未完成，原数据已保留。" + await error);
        _ = await output;
    }
    public async Task<JsonObject> SendAsync(string command, object? args = null)
    {
        ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this);
        var child = process ?? throw new InvalidOperationException("本地内核尚未连接。");
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (pending.Count >= 64) throw new IOException("操作队列已满，请稍候。");
        pending[id] = completion;
        try
        {
            var json = JsonSerializer.Serialize(new { protocolVersion = 1, id, command, args = args ?? new { } });
            if (Encoding.UTF8.GetByteCount(json) > 1024 * 1024 - 1) throw new ArgumentException("本次操作数据过多。");
            await writes.WaitAsync(lifetime.Token);
            try { await child.StandardInput.WriteLineAsync(json); await child.StandardInput.FlushAsync(); }
            finally { writes.Release(); }
            // Never replay timed-out mutations: their outcome may already be committed.
            var response = await completion.Task.WaitAsync(TimeSpan.FromMinutes(5), lifetime.Token);
            if (response["ok"]?.GetValue<bool>() != true) throw new IOException(response["error"]?["message"]?.GetValue<string>() ?? "操作失败。");
            return new JsonObject { ["value"] = response["data"]?.DeepClone() };
        }
        finally { pending.TryRemove(id, out _); }
    }
    public async Task<JsonNode?> CallAsync(string command, object? args = null) => (await SendAsync(command, args))["value"]?.DeepClone();
    private async Task ReadAsync()
    {
        try
        {
            while (await process!.StandardOutput.ReadLineAsync(lifetime.Token) is { } line)
            {
                if (line.Length > 32 * 1024 * 1024) throw new InvalidDataException("播放内核返回了过大的消息。");
                if (JsonNode.Parse(line) is not JsonObject message) throw new InvalidDataException("播放内核消息无效。");
                if (message["id"] is JsonValue id && id.TryGetValue<long>(out var number))
                {
                    if (pending.TryRemove(number, out var completion)) completion.TrySetResult(message);
                }
                else if (message["event"]?.GetValue<string>() == "playback.state" && message["data"] is JsonObject data) PlaybackChanged?.Invoke((JsonObject)data.DeepClone());
            }
        }
        catch (Exception error) when (error is IOException or JsonException or OperationCanceledException)
        {
            if (!lifetime.IsCancellationRequested) Disconnected?.Invoke(error.Message);
        }
        finally
        {
            foreach (var item in pending.Values) item.TrySetException(new IOException("本地播放内核已退出；未确认操作不会自动重试。"));
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (lifetime.IsCancellationRequested) return;
        if (process is { HasExited: false })
        {
            try { await CallAsync("host_shutdown").WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { }
            try { process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception) { if (!process.HasExited) process.Kill(true); }
        }
        lifetime.Cancel();
        if (reader is not null) try { await reader; } catch (Exception) { }
        process?.Dispose(); lifetime.Dispose();
    }
}
