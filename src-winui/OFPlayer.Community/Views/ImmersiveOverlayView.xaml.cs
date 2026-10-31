using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using OFPlayer_WinUI.Services;
using OFPlayer_WinUI.ViewModels;
using Windows.Graphics;

namespace OFPlayer_WinUI.Views;
/// <summary>
/// Immersive player as an in-window overlay (old front-end semantics: the main
/// window switches to a full-screen immersive view - it is NOT a separate OS
/// window). Hosts the dedicated Vue build with the strict bridge allowlist.
/// </summary>
public sealed partial class ImmersiveOverlayView : UserControl
{
    private const long MaxArtworkBytes = 4 * 1024 * 1024;
    private const string PageUrl = "https://ofplayer.local/immersive.html";
    private MainViewModel? viewModel;
    private bool webViewConfigured;
    private bool started;
    private bool closed;
    private bool presentationActive;
    private bool pageReady;
    private string? publishedTrackMetadata;
    private long trackMetadataRevision;
    private Task initialization = Task.CompletedTask;
    private bool backgroundMode;
    private CoreWebView2MemoryUsageTargetLevel? appliedMemoryTarget;
    internal void SetBackgroundMode(bool background)
    {
        backgroundMode = background;
        ApplyMemoryTarget();
    }

    private void ApplyMemoryTarget()
    {
        if (closed || !pageReady || Web.CoreWebView2 is not { } core)
            return;
        try
        {
            var target = presentationActive ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
            core.MemoryUsageTargetLevel = target;
            if (appliedMemoryTarget != target)
                PreviewLog.Write($"immersive memory target: {target}; windowBackground={backgroundMode}");
            appliedMemoryTarget = target;
        }
        catch (Exception error)
        {
            PreviewLog.Write($"immersive memory hint unavailable: {error.Message}");
        }
    }

#if DEBUG
    internal bool IsReadyForVerification => pageReady;
    internal async Task<string> ProbePageForVerificationAsync() => await Web.CoreWebView2.ExecuteScriptAsync(
        "window.__ofplayerWarmProbe ||= crypto.randomUUID()");
#endif
    public event EventHandler? CloseRequested;
    public event EventHandler? ToggleTaskbarRequested;
    public ImmersiveOverlayView()
    {
        InitializeComponent();
        ApplyFailureLocalization();
        Localization.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => OnClosedCleanup();
    }

    /// <summary>Called by the shell right after the overlay becomes visible.</summary>
    public void Start()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.Start");
        if (started)
        {
            return;
        }

        started = true;
        // The overlay is created during MainWindow.InitializeComponent, before the
        // view model exists; resolve it lazily on first entry.
        viewModel ??= MainViewModel.Active;
        initialization = ReloadWebViewAsync();
    }

    /// <summary>
    /// Warms the WebView2 shortly after app startup (hidden) so the first entry
    /// renders instantly instead of paying runtime + navigation cost on open.
    /// </summary>
    public void Preload() => Start();
    private void ExitClicked(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("ImmersiveOverlayView.xaml.ExitClicked");
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---- failure page, retry, renderer-failure recovery ----
    private string failureTitleKey = "immersive.failure.navigation.title";
    private string failureDetail = string.Empty;
    private string? failureDetailKey;
    private (string Name, string Value)[] failureArguments = [];
    private void ApplyFailureLocalization()
    {
        FailureTitle.Text = Localization.Get(failureTitleKey);
        FailureDetail.Text = failureDetailKey is null ? failureDetail : Localization.Format(failureDetailKey, failureArguments);
        RetryButton.Content = Localization.Get("common.retry");
        ExitButton.Content = Localization.Get("immersive.exit");
    }

    private void OnLanguageChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (closed)
            return;
        ApplyFailureLocalization();
        if (pageReady)
            PostEvent("web.locale", new { language = Localization.Resolved });
    });
    private void ShowFailure(string titleKey, string detail, string? detailKey = null, params (string Name, string Value)[] arguments)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.ShowFailure");
        if (closed)
        {
            return;
        }

        failureTitleKey = titleKey;
        failureDetail = detail;
        failureDetailKey = detailKey;
        failureArguments = arguments;
        ApplyFailureLocalization();
        PreviewLog.Write($"immersive failure: {FailureTitle.Text} :: {FailureDetail.Text}");
        FailureOverlay.Visibility = Visibility.Visible;
    }

    private void HideFailure()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.HideFailure");
        FailureOverlay.Visibility = Visibility.Collapsed;
    }

    private void RetryClicked(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("ImmersiveOverlayView.xaml.RetryClicked");
        if (closed || !initialization.IsCompleted)
            return;
        initialization = ReloadWebViewAsync();
    }

    private async Task ReloadWebViewAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.ReloadWebViewAsync");
        try
        {
            var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "web");
            if (!File.Exists(Path.Combine(webRoot, "immersive.html")))
            {
                PreviewLog.Write($"immersive page missing: {Path.Combine(webRoot, "immersive.html")}");
                ShowFailure("immersive.failure.resources.title", string.Empty, "immersive.failure.resources.detail");
                return;
            }

            var environment = await WebViewEnvironmentProvider.GetAsync();
            if (closed)
                return;
            await Web.EnsureCoreWebView2Async(environment);
            if (closed)
                return;
            var core = Web.CoreWebView2;
            if (core is null)
            {
                ShowFailure("immersive.failure.container.title", string.Empty, "immersive.failure.container.detail");
                return;
            }

            if (!webViewConfigured)
            {
                ConfigureCoreWebView(core, webRoot);
                webViewConfigured = true;
            }

            HideFailure();
            pageReady = false;
            core.Navigate(PageUrl);
        }
        catch (Exception error)
        {
            if (closed)
                return;
            PreviewLog.Write($"immersive init failed: {error.GetType().Name}: {error.Message}");
            ShowFailure("immersive.failure.init", string.Empty, OFPlayer.WinUI.Core.FailureReason.Generic);
        }
    }

    private void ConfigureCoreWebView(CoreWebView2 core, string webRoot)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.ConfigureCoreWebView");
        core.SetVirtualHostNameToFolderMapping("ofplayer.local", webRoot, CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += OnWebMessageReceived;
        PlaybackEventRelay.PlaybackChanged += OnPlaybackEvent;
        AttachLyricsRelay();
        core.ProcessFailed += (_, failedArgs) =>
        {
            pageReady = false;
            PreviewLog.Write($"immersive renderer failed: kind={failedArgs.ProcessFailedKind}");
            ShowFailure("immersive.failure.process.title", string.Empty, "immersive.failure.process.detail");
        };
        core.NavigationCompleted += (navSender, completedArgs) =>
        {
            if (completedArgs.IsSuccess)
            {
                HideFailure();
            }
            else
            {
                PreviewLog.Write($"immersive navigation failed: status={completedArgs.WebErrorStatus}");
                ShowFailure("immersive.failure.navigation.title", string.Empty, "immersive.failure.navigation.detail");
            }
        };
    }

    private void OnPlaybackEvent(JsonElement snapshot)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.OnPlaybackEvent");
        if (closed || !presentationActive || !pageReady)
            return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closed || !presentationActive || !pageReady || Web.CoreWebView2 is null)
            {
                return;
            }

            try
            {
                Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { @event = "playback", data = snapshot }));
                PublishTrackMetadata();
            }
            catch (Exception error)
            {
                PreviewLog.Write($"immersive push failed: {error.GetType().Name}: {error.Message}");
            }
        });
    }

    /// <summary>
    /// R4-3: relays same-track lyric edits to the page. A playback event only carries
    /// the trackId, so bind/clear/reparse on the *current* track would otherwise never
    /// reach the immersive view. The (trackId, version) pair lets the page dedupe.
    /// </summary>
    private void AttachLyricsRelay()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.AttachLyricsRelay");
        lyricsRelayTarget ??= viewModel ??= MainViewModel.Active;
        if (lyricsRelayTarget is not null)
        {
            lyricsRelayTarget.LyricsChanged -= OnLyricsChanged;
            lyricsRelayTarget.LyricsChanged += OnLyricsChanged;
            lyricsRelayTarget.PropertyChanged -= OnTrackMetadataChanged;
            lyricsRelayTarget.PropertyChanged += OnTrackMetadataChanged;
        }
    }

    private void OnTrackMetadataChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MainViewModel.CurrentTrackDetail) or nameof(MainViewModel.PlaybackArtwork))
            DispatcherQueue.TryEnqueue(PublishTrackMetadata);
    }

    private void PublishTrackMetadata()
    {
        if (closed || !presentationActive || !pageReady || viewModel is not { } model)
            return;
        var id = model.Playback.ActiveTrackId;
        if (!model.UsesCurrentPlaybackPresentation(id))
        {
            publishedTrackMetadata = null;
            return;
        }

        var detail = BuildWebTrackDetail(RemotePlaybackPresentation.WebDetail(model.CurrentTrackDetail, id!, true));
        var signature = model.PlaybackPresentationScope + "\n" + id + "\n" + detail.GetRawText();
        if (signature == publishedTrackMetadata)
            return;
        publishedTrackMetadata = signature;
        PostEvent("track.metadata", new { trackId = id, revision = ++trackMetadataRevision, track = detail });
    }

    private void OnLyricsChanged()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.OnLyricsChanged");
        var target = lyricsRelayTarget;
        // The page refreshes lyrics on each entry, including edits made while hidden.
        if (closed || !presentationActive || !pageReady || target is null)
        {
            return;
        }

        var payload = new
        {
            trackId = target.ActiveLyricsTrackId,
            version = target.LyricsVersion
        };
        DispatcherQueue.TryEnqueue(() =>
        {
            if (closed || !presentationActive || !pageReady || Web.CoreWebView2 is null)
            {
                return;
            }

            try
            {
                Web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { @event = "lyrics.changed", data = payload }));
            }
            catch (Exception error)
            {
                PreviewLog.Write($"immersive lyrics push failed: {error.GetType().Name}: {error.Message}");
            }
        });
    }

    private MainViewModel? lyricsRelayTarget;
    private async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.OnWebMessageReceived");
        long requestId = 0;
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("event", out var eventName))
            {
                var name = eventName.GetString();
                if (name == "web.ready")
                {
                    pageReady = true;
                    ApplyMemoryTarget();
                    PostEvent("web.locale", new { language = Localization.Resolved });
                    PostEvent("web.taskbar", new { hidden = taskbarHidden });
                    // Replay the latest shell state when navigation completes after
                    // an enter/exit. The visible page requests exactly one snapshot.
                    PostEvent(presentationActive ? "web.enter" : "web.hidden");
                }
                else if (name == "web.activity")
                {
                    PreviewLog.Write($"immersive activity: {root.GetProperty("data")}");
                    OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Mark("immersive.web.activity", root.GetProperty("data").Clone());
                }
                else if (name == "web.error")
                {
                    PreviewLog.Write($"immersive page error: {root.GetProperty("data")}");
                }
                else if (name == "web.close-immersive")
                {
                    CloseRequested?.Invoke(this, EventArgs.Empty);
                }
                else if (name == "web.toggle-taskbar")
                {
                    ToggleTaskbarRequested?.Invoke(this, EventArgs.Empty);
                }

                return;
            }

            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number)
            {
                return;
            }

            requestId = idElement.GetInt64();
            var command = root.TryGetProperty("command", out var commandElement) ? commandElement.GetString() : null;
            var commandArgs = root.TryGetProperty("args", out var argsElement) ? argsElement : default;
            var result = await DispatchBridgeCommandAsync(command, commandArgs);
            Respond(requestId, result, null);
            PreviewLog.Write($"immersive bridge ok: {command}");
        }
        catch (Exception error)
        {
            PreviewLog.Write($"immersive bridge failed: {error.GetType().Name}: {error.Message}");
            if (requestId > 0)
            {
                Respond(requestId, null, error.Message);
            }
        }
    }

    private async Task<JsonElement> DispatchBridgeCommandAsync(string? command, JsonElement args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.DispatchBridgeCommandAsync");
        if (command == "web_locale_get")
            return JsonSerializer.SerializeToElement(new { language = Localization.Resolved });
        var activeViewModel = (viewModel ??= MainViewModel.Active) ?? throw new InvalidOperationException("主视图模型不可用");
        switch (command)
        {
            case "playback_snapshot":
            {
                var snapshot = await SendToHostAsync("playback_snapshot", null);
                var text = snapshot.ToString();
                PreviewLog.Write($"immersive snapshot: {(text.Length > 160 ? text[..160] : text)}");
                return snapshot;
            }

            case "playback_seek":
            {
                var seconds = ReadNumber(args, "seconds") ?? throw new InvalidOperationException("seconds 无效");
                if (seconds < 0 || seconds > 604_800)
                {
                    throw new InvalidOperationException("seconds 超出范围");
                }

                await activeViewModel.SeekAsync(seconds);
                return await SendToHostAsync("playback_snapshot", null);
            }

            case "desktop_catalog_get_track":
            {
                var trackId = ReadString(args, "trackId") ?? throw new InvalidOperationException("trackId 无效");
                var includeArtwork = ReadBool(args, "includeArtwork") ?? false;
                if (activeViewModel.UsesCurrentPlaybackPresentation(trackId))
                {
                    return BuildWebTrackDetail(RemotePlaybackPresentation.WebDetail(activeViewModel.CurrentTrackDetail, trackId, includeArtwork));
                }

                var track = await SendToHostAsync("desktop_catalog_get_track", new { trackId, includeArtwork });
                return BuildWebTrackDetail(track);
            }

            case "lyrics_resolve_track":
            {
                var trackId = ReadString(args, "trackId") ?? throw new InvalidOperationException("trackId 无效");
                double? position = ReadNumber(args, "positionSeconds");
                return await activeViewModel.ResolvePlaybackLyricsAsync(trackId, position);
            }

            case "ui.toggle_play":
                await activeViewModel.TogglePlaybackAsync();
                return EmptyObject();
            case "ui.play_next":
                await activeViewModel.PlayNextAsync();
                return EmptyObject();
            case "ui.play_previous":
                await activeViewModel.PlayPreviousAsync();
                return EmptyObject();
            case "ui.set_volume":
            {
                var volume = ReadNumber(args, "volume") ?? throw new InvalidOperationException("volume 无效");
                if (volume < 0 || volume > 1)
                {
                    throw new InvalidOperationException("volume 超出范围");
                }

                await activeViewModel.SetVolumePersistedAsync(volume);
                return EmptyObject();
            }

            default:
                throw new InvalidOperationException($"命令不在白名单：{command}");
        }
    }

    private async Task<JsonElement> SendToHostAsync(string command, object? args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.SendToHostAsync");
        viewModel ??= MainViewModel.Active;
        var client = viewModel?.HostClientForBridge ?? throw new InvalidOperationException("播放核心未连接");
        return (await client.SendAsync(command, args, CancellationToken.None)).Clone();
    }

    private static JsonElement BuildWebTrackDetail(JsonElement track)
    {
        if (track.ValueKind != JsonValueKind.Object)
        {
            return JsonSerializer.SerializeToElement<object?>(null);
        }

        return JsonSerializer.SerializeToElement(new { id = ReadString(track, "id"), displayTitle = ReadString(track, "displayTitle"), title = ReadString(track, "title"), artist = ReadString(track, "artist"), album = ReadString(track, "album"), artwork = ResolveArtworkForWeb(ReadString(track, "artwork")), });
    }

    private static string? ResolveArtworkForWeb(string? artwork)
    {
        if (string.IsNullOrWhiteSpace(artwork))
        {
            return null;
        }

        if (artwork.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return artwork;
        }

        try
        {
            var info = new FileInfo(artwork);
            if (!info.Exists || info.Length > MaxArtworkBytes)
            {
                return null;
            }

            var extension = info.Extension.TrimStart('.').ToLowerInvariant();
            var mimeType = extension switch
            {
                "png" => "image/png",
                "gif" => "image/gif",
                "webp" => "image/webp",
                "bmp" => "image/bmp",
                _ => "image/jpeg",
            };
            var payload = Convert.ToBase64String(File.ReadAllBytes(info.FullName));
            return $"data:{mimeType};base64,{payload}";
        }
        catch (Exception error)
        {
            PreviewLog.Write($"immersive artwork failed: {error.GetType().Name}: {error.Message}");
            return null;
        }
    }

    private void Respond(long id, JsonElement? result, string? error)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.Respond");
        if (closed || Web.CoreWebView2 is null)
        {
            return;
        }

        var payload = result.HasValue ? JsonSerializer.Serialize(new { id, result = result.Value }) : JsonSerializer.Serialize(new { id, error = new { code = "bridge-failed", message = error ?? "桥接失败" } });
        Web.CoreWebView2.PostWebMessageAsJson(payload);
    }

    private static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(new { });
    private static string? ReadString(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? ReadNumber(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    private static bool? ReadBool(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value) ? value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    } : null;
    /// <summary>Entry motion for the immersive page (old staged enter animation).</summary>
    public void NotifyWebEnter()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.NotifyWebEnter");
        presentationActive = true;
        ApplyMemoryTarget();
        if (pageReady)
            PostEvent("web.enter");
    }

    /// <summary>Exit motion: the page plays its leave animation before the overlay fades.</summary>
    public void NotifyWebExit()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.NotifyWebExit");
        presentationActive = false;
        ApplyMemoryTarget();
        if (pageReady)
            PostEvent("web.exit");
    }

    private bool taskbarHidden;
    public void SetTaskbarHidden(bool hidden)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.SetTaskbarHidden");
        taskbarHidden = hidden;
        PostEvent("web.taskbar", new { hidden });
    }

    private void PostEvent(string name)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.PostEvent");
        try
        {
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { @event = name, data = new { } }));
        }
        catch (Exception error)
        {
            PreviewLog.Write($"immersive event post failed: {error.GetType().Name}: {error.Message}");
        }
    }

    private void PostEvent(string name, object data)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.PostEvent");
        try
        {
            Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { @event = name, data }));
        }
        catch (Exception error)
        {
            PreviewLog.Write($"immersive event post failed: {error.GetType().Name}: {error.Message}");
        }
    }

    internal async Task ReleaseAsync()
    {
        if (closed)
            return;
        OnClosedCleanup();
        // Closing while EnsureCoreWebView2Async is pending can create a controller
        // after Close has already returned. Keep the control attached until that
        // operation settles, then close before XAML unload tears down its state.
        await initialization;
        Web.Close();
    }

    private void OnClosedCleanup()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("ImmersiveOverlayView.xaml.OnClosedCleanup");
        closed = true;
        Localization.LanguageChanged -= OnLanguageChanged;
        PlaybackEventRelay.PlaybackChanged -= OnPlaybackEvent;
        if (lyricsRelayTarget is not null)
        {
            lyricsRelayTarget.LyricsChanged -= OnLyricsChanged;
            lyricsRelayTarget.PropertyChanged -= OnTrackMetadataChanged;
            lyricsRelayTarget = null;
        }
    // Do not touch the WebView here. During XAML unload the control is already
    // in its destroy sequence and calling CoreWebView2/.Stop() throws 0x8007139F
    // ("state not valid for the requested operation", seen on every exit). The
    // control and its browser process are released with the window; unhooking
    // the relay above is all this cleanup needs.
    }
}
