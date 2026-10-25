using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using OFPlayer.Community.Services;
using OFPlayer.WindowsMedia;
using System.Text.Json.Nodes;
using Windows.Media;

namespace OFPlayer.Community;

public sealed partial class MainWindow
{
    private string mediaTitle = "", lastVideoError = "";
    private CancellationTokenSource? volumeChange;
    private bool IsPlaying => videoMode ? video.Snapshot.Loaded && !video.Snapshot.Paused : Text(playback, "status") == "playing";
    private void ApplyMusic(JsonObject state)
    {
        playback = state["playback"]?.DeepClone() as JsonObject ?? new();
        session = state["session"]?.DeepClone() as JsonObject ?? new();
        if (videoMode) return;
        var id = Text(playback, "activeTrackId", Text(session, "currentTrackId"));
        var track = allTracks.Find(t => t.Id == id);
        var loaded = Text(playback, "activeTrackId").Length > 0;
        SetTransport(track?.Title ?? "还没有播放内容", track?.Artist ?? "选择本地音乐或视频", loaded ? Number(playback, "currentTime") : Number(session, "currentTime"), loaded ? Number(playback, "duration") : track?.Duration ?? 0, IsPlaying);
        if (lyricTrack != id) { lyricTrack = id; _ = LoadLyrics(track); }
    }
    private async Task LoadLyrics(TrackRow? track)
    {
        Lyrics.Text = "正在读取本地歌词…";
        try
        {
            if (track is null) { Lyrics.Text = "播放歌曲后，在这里显示本地歌词。"; return; }
            var lyrics = await host.CallAsync("lyrics.resolve", new { trackId = track.Id, audioPath = track.Path, title = track.Title, artist = track.Artist, album = track.Album });
            if (lyricTrack != track.Id) return;
            Lyrics.Text = (lyrics?["lines"] as JsonArray) is { Count: > 0 } lines
                ? string.Join("\n", lines.Select(l => Text(l, "text") + (Text(l, "translatedLyric").Length > 0 ? "\n" + Text(l, "translatedLyric") : "")))
                : Text(lyrics, "text", "未找到本地歌词。");
            if (string.IsNullOrWhiteSpace(Lyrics.Text)) Lyrics.Text = "未找到本地歌词。";
        }
        catch (Exception) { if (track?.Id == lyricTrack) Lyrics.Text = "未找到可用的本地歌词。"; }
    }
    private void SetTransport(string title, string artist, double position, double duration, bool playing)
    {
        rendering = true;
        NowTitle.Text = title; NowArtist.Text = artist;
        PlayGlyph.Glyph = playing ? "\uE769" : "\uE768";
        Timeline.Maximum = Math.Max(1, duration); Timeline.Value = Math.Clamp(position, 0, Timeline.Maximum);
        PositionText.Text = FormatTime(position); DurationText.Text = FormatTime(duration);
        rendering = false;
        UpdateMediaControls(title, artist, position, duration, playing);
    }
    private async void PlayPauseClicked(object sender, RoutedEventArgs args) => await Run(async () =>
    {
        if (!ready) return;
        if (videoMode) { if (video.Snapshot.Loaded) video.SetPaused(!video.Snapshot.Paused); else if (VideoFiles.SelectedItem is VideoRow entry) await PlayVideo(entry); else if (videos.FirstOrDefault() is { } first) await PlayVideo(first); }
        else if (await host.CallAsync(IsPlaying ? "playback.pause" : "playback.play") is JsonObject state) ApplyMusic(state);
    });
    private async void PreviousClicked(object sender, RoutedEventArgs args) => await Run(async () => { if (!ready) return; if (videoMode) await AdjacentVideo(-1); else if (await host.CallAsync("playback.previous") is JsonObject s) ApplyMusic(s); });
    private async void NextClicked(object sender, RoutedEventArgs args) => await Run(async () => { if (!ready) return; if (videoMode) await AdjacentVideo(1); else if (await host.CallAsync("playback.next") is JsonObject s) ApplyMusic(s); });
    private async void TimelineChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!ready || rendering || closing) return;
        seek?.Cancel(); seek?.Dispose(); var cancel = seek = new();
        try { await Task.Delay(140, cancel.Token); if (videoMode) video.Seek(args.NewValue); else await host.CallAsync("playback.seek", new { seconds = args.NewValue }); activity?.Record(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error.Message); }
    }
    private async void VolumeChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!ready || rendering || closing) return;
        volumeChange?.Cancel(); volumeChange?.Dispose(); var cancel = volumeChange = new();
        try { var volume = Math.Clamp(args.NewValue / 100, 0, 1); video.SetVolume(volume); await Task.Delay(80, cancel.Token); await host.CallAsync("playback.volume", new { volume }); activity?.Record(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { ShowError(error.Message); }
    }
    private async void MusicClicked(object sender, RoutedEventArgs args) => await Run(() => SwitchMode(false));
    private async void VideoClicked(object sender, RoutedEventArgs args) => await Run(() => SwitchMode(true));
    private async Task SwitchMode(bool next)
    {
        if (!ready || next == videoMode) { MusicMode.IsChecked = !videoMode; VideoMode.IsChecked = videoMode; return; }
        if (next) await host.CallAsync("playback.suspend");
        else { await SaveVideos(); video.Stop(); }
        videoMode = next; MusicMode.IsChecked = !next; VideoMode.IsChecked = next;
        MusicPage.Visibility = next ? Visibility.Collapsed : Visibility.Visible;
        VideoPage.Visibility = next ? Visibility.Visible : Visibility.Collapsed;
        Libraries.IsEnabled = Collections.IsEnabled = !next;
        Search.Visibility = LyricsButton.Visibility = next ? Visibility.Collapsed : Visibility.Visible;
        RefreshTracks(); RefreshVideos();
        if (next) ApplyVideo(video.Snapshot); else if (await host.CallAsync("session.snapshot") is JsonObject state) ApplyMusic(state);
    }
    private void RefreshVideos() { var selected = (VideoFiles.SelectedItem as VideoRow)?.Path; VideoFiles.ItemsSource = videos.ToArray(); VideoFiles.SelectedItem = videos.Find(v => v.Path == selected); if (videoMode) { PageTitle.Text = "视频"; CountText.Text = $"{videos.Count} 个本地视频"; } }
    private async Task PlayVideo(VideoRow entry)
    {
        LocalMediaPath.Validate(entry.Path);
        if (!videoMode) await SwitchMode(true);
        await host.CallAsync("playback.suspend");
        await SaveVideos();
        video.SetVolume(Volume.Value / 100);
        await video.OpenAsync(entry.Path);
        if (entry.Position > 0 && entry.Position < video.Snapshot.Duration - 3) video.Seek(entry.Position);
        VideoFiles.SelectedItem = videos.Find(v => v.Path == entry.Path);
    }
    private async Task AdjacentVideo(int step)
    {
        if (videos.Count == 0) return;
        var current = videos.FindIndex(v => v.Path == video.Snapshot.Path); if (current < 0) current = 0;
        var next = (current + step + videos.Count) % videos.Count; await PlayVideo(videos[next]);
    }
    private async void VideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs args) => await Run(async () => { if (VideoFiles.SelectedItem is VideoRow entry) await PlayVideo(entry); });
    private void ApplyVideo(VideoSnapshot state)
    {
        if (!videoMode) return;
        SetTransport(state.Path.Length > 0 ? System.IO.Path.GetFileName(state.Path) : "还没有播放内容", state.Path.Length > 0 ? "本地视频" : "选择一个本地视频", state.Position, state.Duration, state.Loaded && !state.Paused);
        if (state.Error.Length > 0 && lastVideoError != state.Error) ShowError(state.Error);
        lastVideoError = state.Error;
        var choices = new[] { new SubtitleChoice(-1, "关闭字幕") }.Concat(state.Tracks.Where(t => t.Kind == "subtitle").Select(t => new SubtitleChoice(t.Index, t.Label))).ToArray();
        var fingerprint = string.Join('|', choices.Select(c => $"{c.Index}:{c.Name}"));
        if (fingerprint != subtitleFingerprint) { rendering = true; subtitleFingerprint = fingerprint; SubtitleTracks.ItemsSource = choices; SubtitleTracks.DisplayMemberPath = "Name"; var selected = state.Tracks.FirstOrDefault(t => t.Kind == "subtitle" && t.Selected)?.Index ?? -1; SubtitleTracks.SelectedItem = choices.FirstOrDefault(c => c.Index == selected); rendering = false; }
    }
    private async Task SaveVideos()
    {
        if (!ready) return;
        var snapshot = video.Snapshot;
        if (snapshot.Loaded) { var index = videos.FindIndex(v => v.Path == snapshot.Path); if (index >= 0) videos[index] = videos[index] with { Position = snapshot.Position, Duration = snapshot.Duration }; }
        await host.CallAsync("preferences.patch", new { video = videos.Select(v => new { path = v.Path, position = v.Position, duration = v.Duration }) });
    }
    private void Tick()
    {
        if (!ready || closing) return;
        if (IsPlaying) activity?.Record();
        video.Refresh();
        if (videoMode) { var state = video.Snapshot; if (state.Phase == "ended" && videos.Count > 1 && videoEndedGeneration != state.Generation) { videoEndedGeneration = state.Generation; _ = Run(() => AdjacentVideo(1)); } }
        if (videoMode && DateTime.UtcNow - lastVideoSave > TimeSpan.FromSeconds(5) && videoSave is not { IsCompleted: false }) { lastVideoSave = DateTime.UtcNow; videoSave = SaveVideoQuietly(); }
    }
    private async Task SaveVideoQuietly() { try { await SaveVideos(); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error.Message); } }
    private async void AddSubtitleClicked(object sender, RoutedEventArgs args) => await Run(async () => { if (!video.Snapshot.Loaded) return; var paths = await PickFiles([".srt", ".vtt", ".ssa", ".ass", ".ttml"]); foreach (var path in paths) await video.AddSubtitleAsync(LocalMediaPath.Validate(path)); });
    private void SubtitleChanged(object sender, SelectionChangedEventArgs args) { if (ready && !rendering && SubtitleTracks.SelectedItem is SubtitleChoice choice) { try { video.SelectSubtitle(choice.Index); } catch (Exception error) { ShowError(error.Message); } } }
    private void VideoRateChanged(object sender, SelectionChangedEventArgs args) { if (ready && VideoRate.SelectedItem is double rate) { try { video.SetRate(rate); } catch (Exception error) { ShowError(error.Message); } } }
    private void FullScreenClicked(object sender, RoutedEventArgs args) => ToggleFullScreen();
    private void ToggleFullScreen()
    {
        fullScreen = !fullScreen;
        AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        TitleBar.Visibility = SidePane.Visibility = PageHeader.Visibility = PlaybackFooter.Visibility = VideoOptions.Visibility = VideoFiles.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        Notice.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        Root.RowDefinitions[0].Height = new GridLength(fullScreen ? 0 : 48);
        Root.ColumnDefinitions[0].Width = new GridLength(fullScreen ? 0 : 220);
        VideoPage.RowDefinitions[2].Height = new GridLength(fullScreen ? 0 : 140);
        PageLayout.RowSpacing = fullScreen ? 0 : 14;
        VideoPage.RowSpacing = fullScreen ? 0 : 10;
        PageSurface.CornerRadius = fullScreen ? new CornerRadius(0) : new CornerRadius(12, 0, 0, 0);
        PageSurface.Padding = fullScreen ? new Thickness(0) : new Thickness(24, 18, 24, 0);
    }
    private async void LyricsClicked(object sender, RoutedEventArgs args) => await Run(async () =>
    {
        if (videoMode) return;
        await new ContentDialog { XamlRoot = Root.XamlRoot, Title = "本地歌词", Content = new ScrollViewer { MaxHeight = 500, Content = new TextBlock { Text = Lyrics.Text, TextWrapping = TextWrapping.Wrap, LineHeight = 30, MaxWidth = 480 } }, CloseButtonText = "关闭" }.ShowAsync();
    });
    private async void SettingsClicked(object sender, RoutedEventArgs args) => await Run(async () =>
    {
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = "OFPlayer Community · 开发预览", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var theme = new ComboBox { Header = "外观", ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = Root.RequestedTheme == ElementTheme.Light ? 1 : Root.RequestedTheme == ElementTheme.Dark ? 2 : 0, HorizontalAlignment = HorizontalAlignment.Stretch }; panel.Children.Add(theme);
        var output = new ComboBox { Header = "音乐输出设备", HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Name" };
        var devices = await host.CallAsync("output.devices");
        var options = new List<LibraryRow> { new("", "系统默认") };
        options.AddRange((devices?["devices"] as JsonArray ?? []).OfType<JsonObject>().Select(d => new LibraryRow(Text(d, "id"), Text(d, "name")))); output.ItemsSource = options; output.SelectedIndex = Math.Max(0, options.FindIndex(d => d.Id == Text(devices, "preferredDeviceId"))); panel.Children.Add(output);
        panel.Children.Add(new TextBlock { Text = "隐私说明", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Community 收集必需的每日活跃统计：每天更换的随机标识、UTC 日期、应用版本、发行渠道与平台。该统计不需要账号，不包含曲名、路径、媒体内容或硬件标识；网络不可用不影响本地播放。\n\n曲库、歌单、歌词与视频播放记录保存在本机。", TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Opacity = .75 });
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "设置", Content = panel, PrimaryButtonText = "保存", CloseButtonText = "取消" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Root.RequestedTheme = theme.SelectedIndex switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        await host.CallAsync("preferences.patch", new { theme = theme.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" } });
        if (output.SelectedItem is LibraryRow device) await host.CallAsync("output.select", new { deviceId = device.Id.Length == 0 ? null : device.Id });
    });
    private void InitializeMediaControls()
    {
        try { smtc = SystemMediaTransportControlsInterop.GetForWindow(Hwnd); smtc.ButtonPressed += MediaButton; smtc.PlaybackPositionChangeRequested += MediaSeek; smtc.IsPlayEnabled = smtc.IsPauseEnabled = smtc.IsNextEnabled = smtc.IsPreviousEnabled = true; }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error.Message); }
    }
    private void UpdateMediaControls(string title, string artist, double position, double duration, bool playing)
    {
        if (smtc is null || closing) return;
        try
        {
            smtc.IsEnabled = duration > 0; smtc.PlaybackStatus = duration <= 0 ? MediaPlaybackStatus.Closed : playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            var key = $"{videoMode}:{title}:{artist}"; if (mediaTitle != key) { mediaTitle = key; smtc.DisplayUpdater.Type = videoMode ? MediaPlaybackType.Video : MediaPlaybackType.Music; if (videoMode) smtc.DisplayUpdater.VideoProperties.Title = title; else { smtc.DisplayUpdater.MusicProperties.Title = title; smtc.DisplayUpdater.MusicProperties.Artist = artist; } smtc.DisplayUpdater.Update(); }
            if (duration > 0) smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties { StartTime = TimeSpan.Zero, MinSeekTime = TimeSpan.Zero, EndTime = TimeSpan.FromSeconds(duration), MaxSeekTime = TimeSpan.FromSeconds(duration), Position = TimeSpan.FromSeconds(Math.Clamp(position, 0, duration)) });
        }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error.Message); }
    }
    private void MediaButton(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args) => DispatcherQueue.TryEnqueue(async () => await Run(async () =>
    {
        if (!ready) return;
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play: if (!IsPlaying) { if (videoMode) video.SetPaused(false); else await host.CallAsync("playback.play"); } break;
            case SystemMediaTransportControlsButton.Pause: if (IsPlaying) { if (videoMode) video.SetPaused(true); else await host.CallAsync("playback.pause"); } break;
            case SystemMediaTransportControlsButton.Next: if (videoMode) await AdjacentVideo(1); else await host.CallAsync("playback.next"); break;
            case SystemMediaTransportControlsButton.Previous: if (videoMode) await AdjacentVideo(-1); else await host.CallAsync("playback.previous"); break;
        }
    }));
    private void MediaSeek(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) => DispatcherQueue.TryEnqueue(async () => await Run(async () => { if (videoMode) video.Seek(args.RequestedPlaybackPosition.TotalSeconds); else await host.CallAsync("playback.seek", new { seconds = args.RequestedPlaybackPosition.TotalSeconds }); }));
    private async void WindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closeReady) return; args.Cancel = true; if (closing) return; closing = true; timer.Stop(); seek?.Cancel(); volumeChange?.Cancel();
        try { await SaveVideos(); video.Dispose(); if (activity is not null) await activity.DisposeAsync(); await host.DisposeAsync(); }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine(error.Message); }
        finally { if (smtc is not null) { smtc.ButtonPressed -= MediaButton; smtc.PlaybackPositionChangeRequested -= MediaSeek; try { smtc.IsEnabled = false; smtc.DisplayUpdater.ClearAll(); smtc.DisplayUpdater.Update(); } catch (Exception) { } } closeReady = true; Close(); }
    }
}
