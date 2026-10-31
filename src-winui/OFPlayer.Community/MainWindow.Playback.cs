using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using OFPlayer.WindowsMedia;
using OFPlayer_WinUI.Views;
using System.Text.Json;
using Windows.Storage.Pickers;

namespace OFPlayer.Community;
public sealed record VideoRow(string Path, double Position = 0, double Duration = 0)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed record SubtitleChoice(int Index, string Name);
public sealed partial class MainWindow
{
    private WindowsVideoBackend? video;
    private List<VideoRow> videos = [];
    private ImmersiveOverlayView? immersive;
    private bool fullScreen;
    private DispatcherTimer? videoTimer;
    private void InitializeVideo()
    {
        video = new WindowsVideoBackend(action => DispatcherQueue.TryEnqueue(() => action()));
        video.PlayerChanged += p => VideoSurface.SetMediaPlayer(p);
        video.StateChanged += state =>
        {
            if (!videoMode)
                return;
            var choices = new[]
            {
                new SubtitleChoice(-1, "关闭字幕")
            }.Concat(state.Tracks.Where(t => t.Kind == "subtitle").Select(t => new SubtitleChoice(t.Index, t.Label))).ToArray();
            if (SubtitleTracks.Items.Count != choices.Length)
            {
                SubtitleTracks.ItemsSource = choices;
                SubtitleTracks.SelectedIndex = 0;
            }
        };
        _ = LoadVideos();
        videoTimer = new()
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        videoTimer.Tick += async (_, _) =>
        {
            if (videoMode)
                await SaveVideos();
        };
        videoTimer.Start();
    }

    async Task LoadVideos()
    {
        var prefs = await model.CommunityCallAsync("preferences.load");
        if (prefs.TryGetProperty("video", out var array) && array.ValueKind == JsonValueKind.Array)
            videos = array.EnumerateArray().Where(v => v.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String).Select(v => new VideoRow(v.GetProperty("path").GetString()!, v.TryGetProperty("position", out var pos) ? pos.GetDouble() : 0)).ToList();
        VideoFiles.ItemsSource = videos.ToArray();
    }

    async Task SaveVideos()
    {
        if (video is null)
            return;
        var state = video.Snapshot;
        var i = videos.FindIndex(v => v.Path == state.Path);
        if (i >= 0)
            videos[i] = videos[i] with
            {
                Position = state.Position,
                Duration = state.Duration
            };
        await model.CommunityCallAsync("preferences.patch", new { video = videos.Select(v => new { path = v.Path, position = v.Position, duration = v.Duration }).ToArray() });
    }

    async void AddVideoClicked(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        foreach (var ext in new[]
        {
            ".mp4",
            ".mkv",
            ".mov",
            ".avi",
            ".wmv",
            ".webm",
            ".m4v",
            ".ts"
        }

        )
            picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        var files = await picker.PickMultipleFilesAsync();
        foreach (var f in files)
            if (!videos.Any(v => v.Path == f.Path))
                videos.Add(new(f.Path));
        VideoFiles.ItemsSource = videos.ToArray();
        await SaveVideos();
    }

    async void VideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (video is null || VideoFiles.SelectedItem is not VideoRow row)
            return;
        await OpenVideo(row);
    }

    private async Task OpenVideo(VideoRow row)
    {
        if (video is null) return;
        try
        {
            await SaveVideos();
            await model.SuspendForVideoAsync();
            await video.OpenAsync(row.Path);
            if (row.Position > 0 && row.Position < video.Snapshot.Duration - 3) video.Seek(row.Position);
            VideoFiles.SelectedItem = row;
        }
        catch (Exception error) { OFPlayer_WinUI.Services.FailureText.Capture(error, "Open local video"); }
    }

    private Task ToggleCurrentPlayback()
    {
        if (!videoMode) return model.TogglePlaybackAsync();
        if (video?.Snapshot.Loaded == true) video.SetPaused(!video.Snapshot.Paused);
        return Task.CompletedTask;
    }

    private Task StepVideo(int direction)
    {
        if (video is null || videos.Count == 0) return Task.CompletedTask;
        var index = videos.FindIndex(v => v.Path == video.Snapshot.Path);
        return OpenVideo(videos[(Math.Max(0, index) + direction + videos.Count) % videos.Count]);
    }

    async void AddSubtitleClicked(object sender, RoutedEventArgs e)
    {
        if (video is null)
            return;
        var picker = new FileOpenPicker();
        foreach (var ext in new[]
        {
            ".srt",
            ".vtt",
            ".ass",
            ".ssa"
        }

        )
            picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        if (await picker.PickSingleFileAsync()is { } file)
            await video.AddSubtitleAsync(file.Path);
    }

    void SubtitleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SubtitleTracks.SelectedItem is SubtitleChoice choice)
            video?.SelectSubtitle(choice.Index);
    }

    void VideoRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VideoRate.SelectedItem is double rate)
            video?.SetRate(rate);
    }

    void VideoFullScreenClicked(object sender, RoutedEventArgs e)
    {
        fullScreen = !fullScreen;
        AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        if (fullScreen)
        {
            if (immersive is not null) immersive.Visibility = Visibility.Collapsed;
            VideoPage.Children.Remove(VideoSurface);
            Grid.SetRow(VideoSurface, 0);
            ImmersiveHost.Children.Add(VideoSurface);
            ImmersiveHost.Visibility = Visibility.Visible;
        }
        else
        {
            ImmersiveHost.Children.Remove(VideoSurface);
            Grid.SetRow(VideoSurface, 1);
            VideoPage.Children.Add(VideoSurface);
            ImmersiveHost.Visibility = Visibility.Collapsed;
        }
    }

    void ShowImmersive()
    {
        if (immersive is null)
        {
            immersive = new();
            immersive.CloseRequested += (_, _) => CloseImmersive();
            immersive.ToggleTaskbarRequested += (_, _) => AppWindow.SetPresenter(AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen ? AppWindowPresenterKind.Default : AppWindowPresenterKind.FullScreen);
            ImmersiveHost.Children.Add(immersive);
        }

        immersive.Visibility = Visibility.Visible;
        ImmersiveHost.Visibility = Visibility.Visible;
        immersive.Start();
        immersive.NotifyWebEnter();
    }

    void CloseImmersive()
    {
        if (fullScreen)
        {
            VideoFullScreenClicked(this, new());
            return;
        }

        ImmersiveHost.Visibility = Visibility.Collapsed;
        immersive?.NotifyWebExit();
        AppWindow.SetPresenter(AppWindowPresenterKind.Default);
    }
}
