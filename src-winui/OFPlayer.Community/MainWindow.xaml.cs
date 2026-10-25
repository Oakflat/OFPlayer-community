using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Windowing;
using OFPlayer.Community.Services;
using OFPlayer.WindowsMedia;
using System.Text.Json.Nodes;
using Windows.Media;
using Windows.Storage.Pickers;

namespace OFPlayer.Community;

public sealed record LibraryRow(string Id, string Name);
public sealed record CollectionRow(string Id, string Name);
public sealed class TrackRow(string id, string libraryId, string title, string artist, string album, string path, double duration, bool favorite)
{
    public string Id { get; } = id;
    public string LibraryId { get; } = libraryId;
    public string Title { get; } = title;
    public string Artist { get; } = artist;
    public string Album { get; } = album;
    public string Path { get; } = path;
    public double Duration { get; } = duration;
    public bool Favorite { get; } = favorite;
    public string DurationText => MainWindow.FormatTime(Duration);
    public override string ToString() => $"{Title} · {Artist}";
}
public sealed record VideoRow(string Path, double Position = 0, double Duration = 0)
{
    public string Name => System.IO.Path.GetFileName(Path);
}
public sealed record SubtitleChoice(int Index, string Name);

public sealed partial class MainWindow : Window
{
    private readonly CommunityHost host = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly WindowsVideoBackend video;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DailyActivity? activity;
    private SystemMediaTransportControls? smtc;
    private bool ready, rendering, videoMode, fullScreen, closing, closeReady;
    private string activeLibrary = "", activeCollection = "all", lyricTrack = "", subtitleFingerprint = "";
    private JsonObject catalog = new(), preferences = new(), playback = new(), session = new();
    private List<TrackRow> allTracks = [];
    private List<VideoRow> videos = [];
    private Task? videoSave;
    private Task? musicImport;
    private DateTime lastVideoSave = DateTime.UtcNow;
    private CancellationTokenSource? seek;
    private long videoEndedGeneration = -1;
    private static readonly string[] AudioExtensions = [".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".aif", ".aiff", ".ape", ".dsf", ".dff", ".wv", ".wma"];
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".ts", ".m2ts", ".mpeg", ".mpg"];
    private nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);
    private static string Text(JsonNode? node, string key, string fallback = "") => node?[key] is JsonValue v && v.TryGetValue<string>(out var value) ? value : fallback;
    private static double Number(JsonNode? node, string key, double fallback = 0) => node?[key] is JsonValue v && v.TryGetValue<double>(out var value) && double.IsFinite(value) ? value : fallback;
    private static bool Flag(JsonNode? node, string key) => node?[key] is JsonValue v && v.TryGetValue<bool>(out var value) && value;
    internal static string FormatTime(double seconds) { var time = TimeSpan.FromSeconds(double.IsFinite(seconds) ? Math.Clamp(seconds, 0, 315576000) : 0); return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}"; }

    public MainWindow()
    {
        InitializeComponent();
        Title = "OFPlayer Community";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets/AppIcon.ico"));
        try { SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop(); } catch (Exception) { }
        video = new WindowsVideoBackend(action => DispatcherQueue.TryEnqueue(() => action()));
        video.PlayerChanged += player => VideoSurface.SetMediaPlayer(player);
        video.StateChanged += state => { if (videoMode) ApplyVideo(state); };
        video.Diagnostic += text => System.Diagnostics.Trace.WriteLine(text);
        host.PlaybackChanged += state => DispatcherQueue.TryEnqueue(() => ApplyMusic(state));
        host.Disconnected += message => DispatcherQueue.TryEnqueue(() => ShowError(message));
        timer.Tick += (_, _) => Tick();
        var space = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Space };
        space.Invoked += (_, args) =>
        {
            if (FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or AutoSuggestBox) return;
            args.Handled = true; PlayPauseClicked(this, new RoutedEventArgs());
        };
        var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, args) => { if (fullScreen) { ToggleFullScreen(); args.Handled = true; } };
        Root.KeyboardAccelerators.Add(space); Root.KeyboardAccelerators.Add(escape);
        AppWindow.Closing += WindowClosing;
        Root.Loaded += async (_, _) => { if (!ready) await Run(InitializeAsync); };
        bool startupSizeApplied = false;
        Activated += (_, args) =>
        {
            if (startupSizeApplied || args.WindowActivationState == WindowActivationState.Deactivated) return;
            startupSizeApplied = true;
            // Resize after activation connects the XAML composition target. Sizing
            // the native HWND in the constructor can leave its first frame blank.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (closing) return;
                var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
                AppWindow.Resize(new Windows.Graphics.SizeInt32(Math.Min(1280, (int)(area.Width * .9)), Math.Min(850, (int)(area.Height * .9))));
            });
        };
        Root.SizeChanged += (_, _) =>
        {
            var visible = Root.ActualWidth >= 1200;
            LyricsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            MusicPage.ColumnDefinitions[1].Width = new GridLength(visible ? 280 : 0);
        };
    }
    private async Task InitializeAsync()
    {
        await host.StartAsync();
        preferences = await host.CallAsync("preferences.load") as JsonObject ?? new();
        rendering = true;
        Volume.Value = (preferences["rememberVolume"]?.GetValue<bool>() == false ? .5 : Math.Clamp(Number(preferences, "volume", .5), 0, 1)) * 100;
        Root.RequestedTheme = Text(preferences, "theme") switch { "dark" => ElementTheme.Dark, "light" => ElementTheme.Light, _ => ElementTheme.Default };
        if (preferences["video"] is JsonArray entries) videos = entries.OfType<JsonObject>().Where(v => Text(v, "path").Length > 0).Select(v => new VideoRow(Text(v, "path"), Number(v, "position"), Number(v, "duration"))).DistinctBy(v => v.Path, StringComparer.OrdinalIgnoreCase).ToList();
        rendering = false;
        await ReloadCatalog();
        if (await host.CallAsync("session.snapshot") is JsonObject state) ApplyMusic(state);
        ready = true;
        if (!host.IsFixture) InitializeMediaControls();
        if (!host.IsFixture) { activity = new DailyActivity(host.DataRoot); activity.Record(); }
        else ShowMessage("临时样例库 · 用于界面检查，退出后不影响现有曲库。");
        timer.Start();
    }
    private async Task Run(Func<Task> action)
    {
        if (closing) return;
        if (!await operation.WaitAsync(0)) return;
        try { await action(); activity?.Record(); }
        catch (Exception error) { ShowError(error.Message); }
        finally { operation.Release(); }
    }
    private void ShowError(string text) { Notice.Severity = InfoBarSeverity.Error; Notice.Message = text; Notice.IsOpen = true; }
    private void ShowMessage(string text) { Notice.Severity = InfoBarSeverity.Informational; Notice.Message = text; Notice.IsOpen = true; }
    private async Task ReloadCatalog()
    {
        catalog = await host.CallAsync("catalog.snapshot") as JsonObject ?? new();
        allTracks = (catalog["tracks"] as JsonArray ?? []).OfType<JsonObject>().Select(t => new TrackRow(Text(t, "id"), Text(t, "libraryId"), Text(t, "title", "未命名曲目"), Text(t, "artist", "未知艺术家"), Text(t, "album"), Text(t["source"], "path"), Number(t, "duration"), Flag(t, "favorite"))).ToList();
        var libraries = (catalog["libraries"] as JsonArray ?? []).OfType<JsonObject>().Select(l => new LibraryRow(Text(l, "id"), Text(l, "name"))).ToList();
        if (!libraries.Any(l => l.Id == activeLibrary)) activeLibrary = libraries.FirstOrDefault()?.Id ?? "";
        rendering = true;
        Libraries.ItemsSource = libraries;
        Libraries.SelectedItem = libraries.Find(l => l.Id == activeLibrary);
        RebuildCollections();
        rendering = false;
        RefreshTracks();
    }
    private void RebuildCollections()
    {
        var rows = new List<CollectionRow> { new("all", "所有歌曲"), new("favorites", "收藏") };
        rows.AddRange((catalog["playlists"] as JsonArray ?? []).OfType<JsonObject>().Where(p => Text(p, "libraryId") == activeLibrary && Text(p, "kind") != "system").Select(p => new CollectionRow(Text(p, "id"), Text(p, "name"))));
        if (!rows.Any(r => r.Id == activeCollection)) activeCollection = "all";
        Collections.ItemsSource = rows;
        Collections.SelectedItem = rows.Find(r => r.Id == activeCollection);
    }
    private void RefreshTracks()
    {
        if (videoMode) { PageTitle.Text = "视频"; CountText.Text = $"{videos.Count} 个本地视频"; return; }
        var search = Search.Text.Trim();
        IEnumerable<TrackRow> rows = allTracks.Where(t => t.LibraryId == activeLibrary);
        if (activeCollection == "favorites") rows = rows.Where(t => t.Favorite);
        else if (activeCollection != "all")
        {
            var ids = (catalog["playlistTrackRelations"] as JsonArray ?? []).OfType<JsonObject>().Where(r => Text(r, "playlistId") == activeCollection).Select(r => Text(r, "trackId")).ToHashSet();
            rows = rows.Where(t => ids.Contains(t.Id));
        }
        if (search.Length > 0) rows = rows.Where(t => $"{t.Title}\n{t.Artist}\n{t.Album}".Contains(search, StringComparison.CurrentCultureIgnoreCase));
        var visible = rows.ToArray();
        Tracks.ItemsSource = visible;
        PageTitle.Text = (Collections.SelectedItem as CollectionRow)?.Name ?? "所有歌曲";
        CountText.Text = $"{visible.Length} 首歌曲";
    }
    private void LibraryChanged(object sender, SelectionChangedEventArgs args) { if (rendering || !ready || Libraries.SelectedItem is not LibraryRow library) return; activeLibrary = library.Id; activeCollection = "all"; rendering = true; RebuildCollections(); rendering = false; RefreshTracks(); }
    private void CollectionChanged(object sender, SelectionChangedEventArgs args) { if (rendering || !ready || Collections.SelectedItem is not CollectionRow collection) return; activeCollection = collection.Id; RefreshTracks(); }
    private void SearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) { if (ready && args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) RefreshTracks(); }
    private async Task<IReadOnlyList<string>> PickFiles(string[] extensions)
    {
        var picker = new FileOpenPicker();
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        using var bounds = new FilePickerWindowScope(Hwnd);
        return (await picker.PickMultipleFilesAsync()).Select(f => f.Path).ToArray();
    }
    private async void AddFilesClicked(object sender, RoutedEventArgs args) => await Run(async () => { if (!ready) return; var files = await PickFiles(videoMode ? VideoExtensions : AudioExtensions); await ImportFiles(files); });
    private async void AddFolderClicked(object sender, RoutedEventArgs args) => await Run(async () =>
    {
        if (!ready) return;
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
        using var bounds = new FilePickerWindowScope(Hwnd);
        var folder = await picker.PickSingleFolderAsync(); if (folder is null) return;
        var extensions = videoMode ? VideoExtensions : AudioExtensions;
        var files = await Task.Run(() => Directory.EnumerateFiles(folder.Path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System }).Where(f => extensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)).Take(20001).ToArray());
        if (files.Length > 20000) throw new InvalidOperationException("一次最多添加 20,000 个文件，请分批添加。");
        await ImportFiles(files);
    });
    private async Task ImportFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return;
        if (videoMode)
        {
            foreach (var file in files) { LocalMediaPath.Validate(file); if (!videos.Any(v => string.Equals(v.Path, file, StringComparison.OrdinalIgnoreCase))) videos.Add(new(file)); }
            RefreshVideos(); await SaveVideos();
        }
        else
        {
            if (musicImport is { IsCompleted: false }) { ShowMessage("正在导入上一批文件，请稍候。"); return; }
            ShowMessage($"正在导入 {files.Count} 个本地文件…");
            musicImport = CompleteMusicImport(activeLibrary, files);
        }
    }
    private async Task CompleteMusicImport(string library, IReadOnlyList<string> files)
    {
        AddFiles.IsEnabled = AddFolder.IsEnabled = false;
        try
        {
            await host.CallAsync("import.files", new { libraryId = library, files = files.Select(f => new { sourcePath = f, fileName = System.IO.Path.GetFileName(f), originalPath = f }) });
            if (closing) return;
            await ReloadCatalog(); ShowMessage("本地文件导入完成。");
        }
        catch (Exception error) { if (!closing) ShowError(error.Message); }
        finally { AddFiles.IsEnabled = AddFolder.IsEnabled = true; }
    }
    private async Task PlayTrack(TrackRow track)
    {
        if (videoMode) await SwitchMode(false);
        video.Stop();
        var queue = (Tracks.ItemsSource as IEnumerable<TrackRow> ?? allTracks).Select(t => t.Id).ToArray();
        if (await host.CallAsync("session.select", new { trackId = track.Id, queueTrackIds = queue, autoplay = true }) is JsonObject state) ApplyMusic(state);
    }
    private async void TrackDoubleTapped(object sender, DoubleTappedRoutedEventArgs args) => await Run(async () => { if (Tracks.SelectedItem is TrackRow track) await PlayTrack(track); });
    private async void PlaySelectedClicked(object sender, RoutedEventArgs args) => await Run(async () => { if (Tracks.SelectedItem is TrackRow track) await PlayTrack(track); });
    private async void PlayAllClicked(object sender, RoutedEventArgs args) => await Run(async () => { if (!ready) return; if (videoMode) { if (videos.FirstOrDefault() is { } entry) await PlayVideo(entry); } else if ((Tracks.ItemsSource as IEnumerable<TrackRow>)?.FirstOrDefault() is { } track) await PlayTrack(track); });
    private async void FavoriteClicked(object sender, RoutedEventArgs args) => await Run(async () => { if (Tracks.SelectedItem is TrackRow track) { await host.CallAsync("track.favorite", new { trackId = track.Id, favorite = !track.Favorite }); await ReloadCatalog(); } });
    private async void RemoveTracksClicked(object sender, RoutedEventArgs args) => await Run(async () => { var ids = Tracks.SelectedItems.OfType<TrackRow>().Select(t => t.Id).ToArray(); if (ids.Length == 0) return; await host.CallAsync("track.delete", new { trackIds = ids }); await ReloadCatalog(); ShowMessage("已从曲库移除，原始文件保留。"); });
    private async Task<string?> AskName(string title)
    {
        var input = new TextBox { PlaceholderText = "名称", MaxLength = 200 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = input, PrimaryButtonText = "创建", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text) ? input.Text.Trim() : null;
    }
    private async void CreateLibraryClicked(object sender, RoutedEventArgs args) => await Run(async () => { if (!ready || videoMode) return; var name = await AskName("新建曲库"); if (name is null) return; await host.CallAsync("library.create", new { name }); await ReloadCatalog(); });
    private async void CreatePlaylistClicked(object sender, RoutedEventArgs args) => await Run(async () => { if (!ready || videoMode) return; var name = await AskName("新建歌单"); if (name is null) return; await host.CallAsync("playlist.create", new { libraryId = activeLibrary, name }); await ReloadCatalog(); });
    private async void AddToPlaylistClicked(object sender, RoutedEventArgs args) => await Run(async () =>
    {
        var tracks = Tracks.SelectedItems.OfType<TrackRow>().ToArray(); if (tracks.Length == 0) return;
        var choices = (Collections.ItemsSource as IEnumerable<CollectionRow> ?? []).Where(c => c.Id is not ("all" or "favorites")).ToArray();
        if (choices.Length == 0) { ShowMessage("请先新建一个歌单。"); return; }
        var picker = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "添加到歌单", Content = picker, PrimaryButtonText = "添加", CloseButtonText = "取消" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || picker.SelectedItem is not CollectionRow choice) return;
        foreach (var track in tracks) await host.CallAsync("playlist.add", new { playlistId = choice.Id, trackId = track.Id });
        await ReloadCatalog();
    });
}
