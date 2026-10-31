using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using OFPlayer_WinUI.Services;
using OFPlayer_WinUI.ViewModels;
using OFPlayer.WinUI.Core.Playback;

namespace OFPlayer_WinUI.Views;
/// <summary>
/// M2 lyric capsule: a 560x112 transparent, borderless, top-most WebView2 window
/// hosting the dedicated lyric-capsule.html build. The page and window exchange
/// allowlisted requests and events through WebView2 messages.
/// </summary>
public sealed class CapsuleWindow
{
    private const double WidthDip = 560.0;
    private const double HeightDip = 112.0;
    private const double TopMarginDip = 12.0;
    // Old capsule protection-region geometry (capsule_window_region.rs).
    private const double CapsuleMinWidthDip = 320.0;
    private const double CapsuleMaxWidthDip = 540.0;
    private const double RegionTopDip = 4.0;
    private const double RegionCollapsedBottomDip = 56.0;
    private const double RegionExpandedBottomDip = 84.0;
    private const string PageUrl = "https://ofplayer.local/lyric-capsule.html";
    private static CapsuleWindow? current;
    private static bool backgroundMode;
    internal static void SetBackgroundMode(bool background)
    {
        backgroundMode = background;
        current?.Web.SetBackgroundMode(background);
    }

    private bool webConfigured;
    private bool closed;
    private bool pageReady;
    private bool positionResetPending;
    private CapsuleArtworkResource? artworkResource;
    private long stateGeneration;
    private readonly object meterGate = new();
    private (JsonElement Data, long Sequence) pendingMeter;
    private bool meterQueued;
    private long webPerfSequence;
    private readonly Dictionary<long, (double Started, long? Parent, string Category)> pendingWebPerf = new();
    private readonly NativeCapsuleHost Web;
    private AppWindow AppWindow => Web.AppWindow;
    private DispatcherQueue DispatcherQueue => Web.DispatcherQueue;

    private void Activate()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.Activate");
        Web.Activate();
        LogPresence("activate");
        if (webConfigured)
        {
            _ = PushFullStateAsync();
            _ = ProbeDomSoonAsync();
        }
    }

    /// <summary>One settled DOM snapshot after the state lands (C1 evidence).</summary>
    private async Task ProbeDomSoonAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.ProbeDomSoonAsync");
        await Task.Delay(1500);
        if (!closed)
        {
            await ProbeDomAsync();
        }
    }

    private void Close() => Web.Close();
    /// <summary>C0: window/controller/listener census for the reuse-vs-recreate log.</summary>
    private void LogPresence(string phase) => PreviewLog.Write($"capsule {phase}: liveWindows={NativeCapsuleHost.LiveWindowCount} " + $"controller={(Web.CoreWebView2 is not null ? "retained" : "none")}");
    private string lastTrackId = string.Empty;
    private int fullStateQueued;
    private void QueueFullStateRefresh()
    {
        if (closed || !Web.IsRequested || Interlocked.Exchange(ref fullStateQueued, 1) != 0)
            return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref fullStateQueued, 0);
            if (!closed && Web.IsRequested)
                _ = PushFullStateAsync();
        }))
            Interlocked.Exchange(ref fullStateQueued, 0);
    }

    private long lastProgressAtMs;
    private string lastPlaybackStatus = string.Empty;
    private double lastPlaybackPosition;
    public static bool IsOpen => current?.Web.IsRequested == true;
    internal static bool IsOpening => IsOpen && current?.pageReady != true;
    internal static bool IsVisible => current?.Web.IsShown == true;

#if DEBUG
    internal static bool IsReadyForVerification => current?.pageReady == true;
    internal static bool HasHostForVerification => current is not null;
    internal static object? HostForVerification => current;
    internal static async Task<string> ProbePageForVerificationAsync() => await current!.Web.CoreWebView2!.ExecuteScriptAsync(
        "window.__ofplayerWarmProbe ||= crypto.randomUUID()");
#endif
    internal static void MoveForReview(int x, int y) => current?.AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
    /// <summary>Old behavior: the capsule toggle creates or re-focuses the window.</summary>
    public static void ShowOrActivate()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.ShowOrActivate");
        Preload();
        current?.Activate();
    }

    public static void Preload()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.Preload");
        if (current is not null)
            return;
        var window = new CapsuleWindow();
        current = window;
        window.Web.SetBackgroundMode(backgroundMode);
        window.PlaceOnPrimaryTopCenter();
        _ = window.InitWebAsync();
    }

    /// <summary>
    /// Settings' "restore default position". The page owns the saved position, so it
    /// moves itself; a capsule that is not ready yet gets the request once it is.
    /// </summary>
    public static void ResetPosition()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.ResetPosition");
        Preload();
        if (current is not { } window)
            return;
        if (window.pageReady)
            window.PushEvent("capsule://position-reset", new JsonObject());
        else
            window.positionResetPending = true;
    }

    public static void HideIfOpen()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.HideIfOpen");
        current?.Web.Hide();
        current?.LogPresence("hide");
    }

    public static void CloseIfOpen()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.CloseIfOpen");
        current?.Close();
    }

    private CapsuleWindow()
    {
        Web = new NativeCapsuleHost();
        Web.Closed += OnClosed;
        Web.ContentScaleChanged += (_, _) => ResizeToContentScale();
        Localization.LanguageChanged += OnLanguageChanged;
    }

    private void PlaceOnPrimaryTopCenter()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.PlaceOnPrimaryTopCenter");
        try
        {
            var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            if (display is null)
            {
                return;
            }

            // Sized in the page's CSS px so it always sees 560x112, even with Windows text size above 100%.
            var contentScale = Web.ContentScale;
            var width = (int)Math.Round(WidthDip * contentScale);
            var height = (int)Math.Round(HeightDip * contentScale);
            var area = display.WorkArea;
            var x = area.X + (area.Width - width) / 2;
            var y = area.Y + (int)Math.Round(TopMarginDip * RasterScale());
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
        }
        catch (Exception error)
        {
            PreviewLog.Write($"capsule placement failed: {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>Text size changed while open: keep 560x112 CSS px around the same centre.</summary>
    private void ResizeToContentScale()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.ResizeToContentScale");
        if (closed)
            return;
        var contentScale = Web.ContentScale;
        var width = (int)Math.Round(WidthDip * contentScale);
        var height = (int)Math.Round(HeightDip * contentScale);
        var bounds = AppWindow.Position;
        var size = AppWindow.Size;
        if (size.Width == width && size.Height == height)
            return;
        var x = bounds.X + (size.Width - width) / 2;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, bounds.Y, width, height));
    }

    private async Task InitWebAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.InitWebAsync");
        try
        {
            var webRoot = Path.Combine(AppContext.BaseDirectory, "Assets", "web");
            if (!File.Exists(Path.Combine(webRoot, "lyric-capsule.html")))
            {
                Web.ShowFailure(Localization.Get("capsule.failure.resources"));
                Close();
                return;
            }

            await Web.EnsureCoreWebView2Async();
            var core = Web.CoreWebView2;
            // Closing during environment/controller creation must not touch the
            // just-released WebView objects (late-callback race, C0).
            if (core is null || closed)
            {
                return;
            }

            if (!webConfigured)
            {
                // Transparency: the page paints its own capsule chrome; everything
                // else must let the desktop through.
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.SetVirtualHostNameToFolderMapping("ofplayer.local", webRoot, CoreWebView2HostResourceAccessKind.DenyCors);
                core.AddWebResourceRequestedFilter(CapsuleArtworkResource.Origin + "*", CoreWebView2WebResourceContext.Image);
                core.WebResourceRequested += OnArtworkRequested;
                core.WebMessageReceived += OnWebMessageReceived;
                core.NavigationCompleted += OnNavigationCompleted;
                core.ProcessFailed += OnProcessFailed;
                PlaybackEventRelay.PlaybackChanged += OnPlaybackEvent;
                PlaybackEventRelay.MeterChanged += OnMeterEvent;
                AttachLyricsRelay();
                webConfigured = true;
            }

            // Position the independent HWND after controller initialization; the
            // capsule now (top-center of the primary display, old 560x112 + 12dip).
            if (Environment.GetEnvironmentVariable("OFPLAYER_WINUI_CAPSULE_AUTOTEST") != "1")
                PlaceOnPrimaryTopCenter();
            core.Navigate(PageUrl);
        }
        catch (Exception error)
        {
            if (closed)
                return;
            PreviewLog.Write($"capsule web init failed: {error.GetType().Name}: {error.Message}");
            __perf?.Outcome("error");
            // The exception is in the log above; the message box shows one plain line.
            Web.ShowFailure(Localization.Get(OFPlayer.WinUI.Core.FailureReason.Generic));
            Close(); // Leave the next tray click able to retry initialization.
        }
    }

    // ---- WebView2 message bridge ----
    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.OnWebMessageReceived");
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (root.TryGetProperty("kind", out var perfKind) && perfKind.GetString() == "perf.web.applied")
            {
                if (root.TryGetProperty("id", out var perfId) && perfId.TryGetInt64(out var id) && pendingWebPerf.Remove(id, out var sent))
                {
                    OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Sample("capsule.web." + sent.Category + ".hostToRafAckMs", OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.NowMs - sent.Started, sent.Parent);
                    if (root.TryGetProperty("jsMs", out var js) && js.TryGetDouble(out var ms))
                        OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Sample("capsule.web.jsToRafMs", ms, sent.Parent);
                }

                return;
            }

            if (root.TryGetProperty("kind", out var readyKind) && readyKind.GetString() == "capsule.ready")
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (closed)
                        return;
                    pageReady = true;
                    PushLocale();
                    if (positionResetPending)
                    {
                        positionResetPending = false;
                        PushEvent("capsule://position-reset", new JsonObject());
                    }

                    Web.ShowReady();
                });
                return;
            }

            if (root.TryGetProperty("kind", out var pageKind) && pageKind.GetString() == "page.error")
            {
                PreviewLog.Write($"capsule page error: {root.GetProperty("message").GetString()}");
                return;
            }

            if (root.TryGetProperty("kind", out var debugKind) && debugKind.GetString() == "debug.boot")
            {
                var dump = root.TryGetProperty("data", out var dataElement) ? dataElement.GetString() : "";
                PreviewLog.Write($"capsule boot response: {dump}");
                return;
            }

            if (!root.TryGetProperty("id", out var requestId) || requestId.ValueKind != JsonValueKind.Number || !requestId.TryGetInt64(out var rid) || !root.TryGetProperty("command", out var commandElement) || commandElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var command = commandElement.GetString() ?? string.Empty;
            var commandArgs = root.TryGetProperty("args", out var argsElement) ? argsElement.Clone() : default;
            // Leave the WebView callback before changing HWND/controller state.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!closed)
                    _ = DispatchInvokeAsync(rid, command, commandArgs);
            });
        }
        catch (Exception error)
        {
            PreviewLog.Write($"capsule message failed: {error.GetType().Name}: {error.Message}");
        }
    }

    private async Task DispatchInvokeAsync(long rid, string command, JsonElement args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.DispatchInvokeAsync");
        try
        {
            JsonNode? value = null;
            switch (command)
            {
                case "web_locale_get":
                    value = new JsonObject
                    {
                        ["language"] = Localization.Resolved
                    };
                    break;
                case "capsule_get_boot_state":
                    value = await BuildBootStateAsync();
                    break;
                case "capsule_appearance_get":
                    value = (MainViewModel.Active?.CapsuleAppearance ?? CapsuleAppearance.Default).ToJson();
                    break;
                case "capsule_open_settings":
                    OFPlayer.Community.MainWindow.Current?.OpenCapsuleSettings();
                    break;
                case "capsule_apply_hit_region":
                    // Original protection region controls mouse passthrough only:
                    // pixels outside the pill remain part of the transparent canvas.
                    // through everything outside it (no acrylic frame remains).
                    ApplyHitRegion(args);
                    value = null;
                    break;
                case "capsule_release":
                    value = null;
                    break;
                case "capsule_close":
                    HideIfOpen();
                    break;
                case "diagnostics_log_event":
                    ForwardDiagnosticsEvent(args);
                    value = null;
                    break;
                case "capsule_control":
                    ForwardControl(args);
                    break;
                case "capsule_set_position":
                    ApplySetPosition(args);
                    break;
                case "capsule_start_dragging":
                    await Web.StartDraggingAsync(args.TryGetProperty("dragClientX", out var dragX) && dragX.TryGetDouble(out var x) ? x : null, args.TryGetProperty("dragClientY", out var dragY) && dragY.TryGetDouble(out var y) ? y : null);
                    break;
                case "capsule_scale_factor":
                    value = JsonValue.Create(RasterScale());
                    break;
                case "capsule_outer_position":
                    value = BuildWindowPosition();
                    break;
                case "capsule_primary_monitor":
                    value = BuildPrimaryMonitor();
                    break;
                default:
                    PreviewLog.Write($"capsule bridge: unmapped command '{command}'");
                    break;
            }

            PostCallback(rid, value);
        }
        catch (Exception error)
        {
            Services.FailureText.Capture(error, nameof(DispatchInvokeAsync));
            PostError(rid, error.Message);
            PreviewLog.Write($"capsule invoke failed ({command}): {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// Renderer trouble: WebView2 restarts a crashed render process and reloads
    /// the page by itself (document-created scripts re-apply, the page re-boots).
    /// A browser-process exit cannot be recovered in place: report it and tear the
    /// window down so a later open starts a fresh host.
    /// </summary>
    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.OnProcessFailed");
        PreviewLog.Write($"capsule process failed: kind={args.ProcessFailedKind}");
        if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            Web.ShowFailure(Localization.Get("capsule.failure.process"));
        }
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.OnNavigationCompleted");
        PreviewLog.Write($"capsule nav completed: success={args.IsSuccess} status={args.WebErrorStatus}");
        // Navigation completion precedes Vue boot state, placement and image decode.
        // Only capsule.ready may expose the HWND.
        if (!args.IsSuccess)
        {
            Web.ShowFailure(Localization.Get("capsule.failure.navigation"));
            return;
        }

        if (args.IsSuccess)
        {
            // Belt and braces: push the full state after the page settles, so the
            // capsule never stays on placeholder text if its boot pull fails.
            _ = PushStateAfterDelayAsync();
        }
    }

    private async Task PushStateAfterDelayAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.PushStateAfterDelayAsync");
        await Task.Delay(1500);
        if (!closed)
        {
            await PushFullStateAsync();
            await Task.Delay(800);
            await ProbeDomAsync();
            // Second probe at a settled point: the first can land before the page
            // finishes applying the pushed state (C0/C1 evidence timing).
            await Task.Delay(2400);
            await ProbeDomAsync();
        }
    }

    /// <summary>Reads what the capsule DOM actually shows (diagnostic).</summary>
    private async Task ProbeDomAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.ProbeDomAsync");
        try
        {
            var core = Web.CoreWebView2;
            if (core is null)
            {
                return;
            }

            var result = await core.ExecuteScriptAsync("JSON.stringify({line:document.querySelector('.lyric-capsule__line')?.textContent,artworkLoaded:(document.querySelector('.lyric-capsule__record img')?.naturalWidth||0)>0,bars:[...document.querySelectorAll('.lyric-capsule__wave span span, .lyric-capsule__wave--spectrum>span')].map(x=>x.style.height||x.style.transform),progress:document.querySelector('.lyric-capsule__seek-track>span')?.style.transform,seconds:document.querySelector('[role=slider]')?.getAttribute('aria-valuenow')})");
            PreviewLog.Write($"capsule dom: {result}");
        }
        catch (Exception error)
        {
            PreviewLog.Write($"capsule dom probe failed: {error.GetType().Name}: {error.Message}");
        }
    }

    private async Task<JsonNode?> BuildBootStateAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.BuildBootStateAsync");
        var viewModel = MainViewModel.Active;
        if (viewModel is null)
        {
            PreviewLog.Write("capsule boot state: view model unavailable");
            return null;
        }

        var state = await BuildWebStateAsync(viewModel);
        if (state is not null)
        {
            lastTrackId = state["trackId"]?.GetValue<string>() ?? string.Empty;
            PreviewLog.Write($"capsule boot state: track={lastTrackId} title={state["title"]}");
        }

        return state;
    }

    private async Task<JsonObject?> BuildWebStateAsync(MainViewModel viewModel)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.BuildWebStateAsync");
        var generation = ++stateGeneration;
        var state = await viewModel.BuildCapsuleStateAsync();
        if (state is null || closed || generation != stateGeneration)
            return null;
        CapsuleArtworkResource? resource = null;
        try
        {
            resource = await CapsuleArtworkResource.LoadAsync(state["artworkSrc"]?.GetValue<string>());
        }
        catch (Exception error)
        {
            PreviewLog.Write($"capsule artwork read failed: {error.GetType().Name}");
        }

        if (closed || generation != stateGeneration)
            return null;
        artworkResource = resource;
        state["artworkSrc"] = resource?.Url ?? string.Empty;
        state["artworkUrl"] = resource?.Url ?? string.Empty;
        return state;
    }

    private void OnArtworkRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.OnArtworkRequested");
        // Only the exact active image is addressable. No URL-to-path conversion,
        // broad disk mapping, or network request is needed by the web surface.
        var resource = artworkResource;
        var found = !closed && args.Request.Method == "GET" && resource?.Url == args.Request.Uri;
        var stream = found ? new MemoryStream(resource!.Bytes, writable: false).AsRandomAccessStream() : null;
        // The page reads the cover's pixels for its "follow cover" colour; only its own origin may.
        args.Response = sender.Environment.CreateWebResourceResponse(stream, found ? 200 : 404, found ? "OK" : "Not Found", $"Content-Type: {(found ? resource!.ContentType : "text/plain")}\r\nCache-Control: no-store\r\n" + "Access-Control-Allow-Origin: https://ofplayer.local\r\n");
    }

    private void ForwardControl(JsonElement args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.ForwardControl");
        if (!args.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var viewModel = MainViewModel.Active;
        if (viewModel is null)
        {
            return;
        }

        var action = payload.TryGetProperty("action", out var actionElement) ? actionElement.GetString() : null;
        switch (action)
        {
            case "previous":
                _ = viewModel.PlayPreviousAsync();
                break;
            case "next":
                _ = viewModel.PlayNextAsync();
                break;
            case "toggle-playback":
                _ = viewModel.TogglePlaybackAsync();
                break;
            case "seek":
                if (payload.TryGetProperty("seconds", out var seconds) && seconds.ValueKind == JsonValueKind.Number)
                {
                    _ = viewModel.SeekAsync(seconds.GetDouble());
                }

                break;
        }
    }

    /// <summary>
    /// Original protection region controls input, never the rendered shape.
    /// </summary>
    private void ApplyHitRegion(JsonElement args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.ApplyHitRegion");
        var request = args.TryGetProperty("request", out var requestElement) ? requestElement : args;
        var width = request.TryGetProperty("capsuleWidth", out var widthElement) && widthElement.ValueKind == JsonValueKind.Number ? widthElement.GetDouble() : CapsuleMaxWidthDip;
        var expanded = request.TryGetProperty("expanded", out var expandedElement) && expandedElement.ValueKind == JsonValueKind.True;
        double Viewport(string field) => request.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
        // Heights follow the page's font size, second line and paused mini form; 0 keeps the defaults.
        Web.SetHitRegion(width, expanded, Viewport("viewportWidth"), Viewport("viewportHeight"), Viewport("collapsedHeight"), Viewport("expandedHeight"));
    }

    private void ApplySetPosition(JsonElement args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.ApplySetPosition");
        if (!args.TryGetProperty("value", out var value))
            throw new ArgumentException("Capsule position is missing.");
        var(x, y) = CapsuleWindowPosition.ToPhysical(value, RasterScale());
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Mark("capsule.position.applied", new { x, y });
    }

    private double RasterScale()
    {
        try
        {
            var hwnd = Web.Handle;
            var dpi = NativeCapsule.GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private void ForwardDiagnosticsEvent(JsonElement args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.ForwardDiagnosticsEvent");
        var viewModel = MainViewModel.Active;
        if (viewModel is null)
        {
            return;
        }

        // The capsule page logs through the app-level diagnostics command; the
        // native host owns the log, so forward it there (fire and forget).
        var request = args.TryGetProperty("request", out var wrapped) ? wrapped.Clone() : args.Clone();
        _ = viewModel.LogCapsuleDiagnosticsEventAsync(request);
    }

    private JsonNode? BuildWindowPosition()
    {
        var position = AppWindow.Position;
        return new JsonObject
        {
            ["x"] = position.X,
            ["y"] = position.Y,
        };
    }

    private JsonNode? BuildWindowSize()
    {
        var size = AppWindow.Size;
        return new JsonObject
        {
            ["width"] = size.Width,
            ["height"] = size.Height,
        };
    }

    private JsonNode? BuildPrimaryMonitor()
    {
        var display = DisplayArea.Primary;
        if (display is null)
        {
            return null;
        }

        var scale = RasterScale();
        var area = display.OuterBounds;
        var workArea = display.WorkArea;
        var monitor = new JsonObject
        {
            ["name"] = null,
            ["size"] = new JsonObject
            {
                ["width"] = area.Width,
                ["height"] = area.Height,
            },
            ["position"] = new JsonObject
            {
                ["x"] = area.X,
                ["y"] = area.Y,
            },
            ["scaleFactor"] = scale,
            ["workArea"] = new JsonObject
            {
                ["position"] = new JsonObject
                {
                    ["x"] = workArea.X,
                    ["y"] = workArea.Y
                },
                ["size"] = new JsonObject
                {
                    ["width"] = workArea.Width,
                    ["height"] = workArea.Height
                },
            },
        };
        OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Mark("capsule.monitor.primary", monitor);
        return monitor;
    }

    private void PostCallback(long rid, JsonNode? value)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.PostCallback");
        var message = new JsonObject
        {
            ["id"] = rid,
            ["result"] = value,
        };
        PostToWeb(message);
    }

    private void PostError(long rid, string error)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.PostError");
        var message = new JsonObject
        {
            ["id"] = rid,
            ["error"] = new JsonObject
            {
                ["code"] = "capsule-command-failed",
                ["message"] = error
            },
        };
        PostToWeb(message);
    }

    private void PushEvent(string eventName, JsonNode payload)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.PushEvent");
        PostToWeb(OFPlayer.WinUI.Core.Playback.CapsuleEventEnvelope.Build(eventName, payload), eventName switch
        {
            "capsule://meter" => "meter",
            "capsule://progress-anchor" => "progress",
            _ => "state"
        });
        if (eventName != "capsule://meter")
            PreviewLog.Write($"capsule push: {eventName}");
    }

    private void PostToWeb(JsonObject message, string category = "callback")
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.PostToWeb");
        try
        {
            var core = Web.CoreWebView2;
            if (core is null || closed)
            {
                return;
            }

            var queued = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.NowMs;
            var operation = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.CurrentId;
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    if (closed)
                        return;
                    var now = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.NowMs;
                    OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Sample("capsule.web.queueMs", now - queued, operation);
                    if (OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Enabled && Web.IsShown)
                    {
                        foreach (var stale in pendingWebPerf.Where(p => now - p.Value.Started > 5000).Select(p => p.Key).ToArray())
                        {
                            pendingWebPerf.Remove(stale);
                            OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Sample("capsule.web.ackExpired", 1);
                        }

                        if (pendingWebPerf.Count < 256)
                        {
                            var id = ++webPerfSequence;
                            pendingWebPerf[id] = (now, operation, category);
                            message["perfId"] = id;
                        }
                    }

                    var started = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.NowMs;
                    core.PostWebMessageAsJson(message.ToJsonString());
                    OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Activity("capsule.web.postMessage", started);
                }
                catch (Exception error)
                {
                    PreviewLog.Write($"capsule post failed: {error.GetType().Name}: {error.Message}");
                }
            });
        }
        catch (Exception error)
        {
            PreviewLog.Write($"capsule post failed: {error.GetType().Name}: {error.Message}");
        }
    }

    // ---- playback relay -> capsule state / progress anchor ----
    private void OnMeterEvent(JsonElement data, long sequence)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.OnMeterEvent");
        // A preloaded/hidden capsule has no meter to paint. Avoid queuing a UI
        // callback for every audio frame; the next frame resumes it when shown.
        if (closed || !Web.IsShown)
            return;
        lock (meterGate)
        {
            pendingMeter = (data, sequence);
            if (meterQueued)
                return;
            meterQueued = true;
        }

        if (!DispatcherQueue.TryEnqueue(() =>
        {
            (JsonElement Data, long Sequence) frame;
            lock (meterGate)
            {
                frame = pendingMeter;
                meterQueued = false;
            }

            if (closed || !Web.IsShown)
                return;
            if (JsonNode.Parse(frame.Data.GetRawText())is JsonObject payload)
            {
                payload["seq"] = frame.Sequence;
                PushEvent("capsule://meter", payload);
            }
        }))
        {
            lock (meterGate)
                meterQueued = false;
        }
    }

    private void OnPlaybackEvent(JsonElement snapshot)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.OnPlaybackEvent");
        if (closed || !Web.IsShown)
        {
            return;
        }

        var trackId = snapshot.ValueKind == JsonValueKind.Object && snapshot.TryGetProperty("activeTrackId", out var activeElement) && activeElement.ValueKind == JsonValueKind.String ? activeElement.GetString() ?? string.Empty : string.Empty;
        if (trackId != lastTrackId)
        {
            // Track change: full state (track + lyrics) on the UI thread, plus one
            // settled DOM snapshot so a track switch is verifiable from the log.
            lastTrackId = trackId;
            QueueFullStateRefresh();
            DispatcherQueue.TryEnqueue(() => _ = ProbeDomSoonAsync());
            return;
        }

        // Same track: throttled progress anchor (old capsule keeps a light anchor
        // between full states).
        var now = Environment.TickCount64;
        var status = snapshot.TryGetProperty("status", out var statusValue) ? statusValue.GetString() ?? "" : "";
        var position = snapshot.TryGetProperty("currentTime", out var timeValue) ? timeValue.GetDouble() : 0;
        var discontinuity = status != lastPlaybackStatus || Math.Abs(position - lastPlaybackPosition) > 1;
        if (!discontinuity && now - lastProgressAtMs < 200)
        {
            return;
        }

        lastProgressAtMs = now;
        lastPlaybackStatus = status;
        lastPlaybackPosition = position;
        var viewModel = MainViewModel.Active;
        if (viewModel is not null)
        {
            PushEvent("capsule://progress-anchor", viewModel.BuildCapsuleProgressAnchor());
        }
    }

    /// <summary>
    /// R4-3: same-track lyric edits (bind / clear / reparse) must reach the capsule
    /// too. A playback event only carries the track id, so without this relay the
    /// capsule keeps rendering the previous lyric sheet until the next track change.
    /// </summary>
    private void AttachLyricsRelay()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.AttachLyricsRelay");
        lyricsRelayTarget ??= MainViewModel.Active;
        if (lyricsRelayTarget is not null)
        {
            lyricsRelayTarget.LyricsChanged -= OnLyricsChanged;
            lyricsRelayTarget.LyricsChanged += OnLyricsChanged;
            lyricsRelayTarget.PropertyChanged -= OnViewModelPropertyChanged;
            lyricsRelayTarget.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (lyricsRelayTarget is not { } viewModel)
            return;
        if (args.PropertyName == nameof(MainViewModel.CapsuleAppearance))
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!closed)
                    PushEvent("capsule://appearance", viewModel.CapsuleAppearance.ToJson());
            });
        // External local originals share one native ID, and cloud artwork arrives
        // asynchronously. Neither change is observable by comparing track IDs alone.
        if (viewModel.UsesCurrentPlaybackPresentation(viewModel.Playback.ActiveTrackId) && args.PropertyName is nameof(MainViewModel.CurrentTrackDetail) or nameof(MainViewModel.PlaybackArtwork) or nameof(MainViewModel.PlaybackTitle) or nameof(MainViewModel.PlaybackArtist))
            QueueFullStateRefresh();
    }

    private void OnLyricsChanged()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.OnLyricsChanged");
        if (lyricsRelayTarget is null)
        {
            return;
        }

        // A lyric edit must refresh the full state even if a progress anchor just went
        // out, so bypass the 200ms anchor throttle by pushing state directly.
        DispatcherQueue.TryEnqueue(() => _ = PushFullStateAsync());
    }

    private MainViewModel? lyricsRelayTarget;
    private async Task PushFullStateAsync()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.PushFullStateAsync");
        var viewModel = MainViewModel.Active;
        if (viewModel is null)
        {
            return;
        }

        var state = await BuildWebStateAsync(viewModel);
        if (state is not null && !closed)
        {
            PushEvent("capsule://state", state);
        }
    }

    private void PushLocale() => PushEvent("web.locale", new JsonObject { ["language"] = Localization.Resolved });
    private void OnLanguageChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (!closed)
            PushLocale();
    });
    /// <summary>Called by the shell after the page reports it is ready (web.ready).</summary>
    public void NotifyReady() => _ = PushFullStateAsync();
    private void OnClosed(object? sender, EventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("CapsuleWindow.OnClosed");
        closed = true;
        Localization.LanguageChanged -= OnLanguageChanged;
        PlaybackEventRelay.PlaybackChanged -= OnPlaybackEvent;
        PlaybackEventRelay.MeterChanged -= OnMeterEvent;
        artworkResource = null;
        if (lyricsRelayTarget is not null)
        {
            lyricsRelayTarget.LyricsChanged -= OnLyricsChanged;
            lyricsRelayTarget.PropertyChanged -= OnViewModelPropertyChanged;
            lyricsRelayTarget = null;
        }

        LogPresence("closed");
        PreviewLog.Write("capsule window closed");
        if (ReferenceEquals(current, this))
        {
            current = null;
        }
    }

    private static class NativeCapsule
    {
        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);
    }
}
