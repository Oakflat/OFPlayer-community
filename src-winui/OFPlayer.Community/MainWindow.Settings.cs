using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OFPlayer_WinUI.Services;
using OFPlayer_WinUI.Views;
using Windows.Storage.Pickers;

namespace OFPlayer.Community;
public sealed partial class MainWindow
{
    internal void OpenCapsuleSettings()
    {
        Activate();
        _ = ShowSettings();
    }

    private async Task ShowSettings()
    {
        var changed = SettingsPage.Visibility != Visibility.Visible;
        LibraryPage.Visibility = VideoPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Visible;
        if (changed && SettingsPage.IsLoaded) Motion.Show(SettingsPage, offsetY: 16, durationMs: Motion.SlowMs, restart: true);
        BuildNavigation();
        SettingsContent.Children.Clear();
        SettingsContent.Children.Add(new TextBlock { Text = "设置", FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        Section("外观");
        Select("主题", new[] { "跟随系统", "浅色", "深色" }, Array.IndexOf(new[] { "system", "light", "dark" }, model.ThemePreference), async i => await model.SetThemeAsync(new[] { "system", "light", "dark" }[i]));
        Select("语言", new[] { "跟随系统", "English", "简体中文", "日本語", "Français", "Deutsch" }, Array.IndexOf(new[] { "system", "en", "zh-CN", "ja", "fr", "de" }, model.LanguagePreference), async i => await model.SetLanguageAsync(new[] { "system", "en", "zh-CN", "ja", "fr", "de" }[i]));
        Select(Localization.Get("nav.smartPlaylists"), new[] { "展开显示", "收起到分组" }, model.SmartPlaylistsPreference == "collapsed" ? 1 : 0,
            async i => await model.SetSmartPlaylistsAsync(i == 1 ? "collapsed" : "expanded"));
        Select("内容背景", new[] { "纯色", "专辑封面" }, model.ShownContentBackdrop.Kind == "cover" ? 1 : 0, async i => await model.ChooseContentBackdropAsync(model.ContentBackdrop with { Kind = i == 1 ? "cover" : "plain" }));
        SliderRow("背景强度", 10, 100, model.ContentBackdrop.Strength, async v => await model.ChooseContentBackdropAsync(model.ContentBackdrop with { Strength = (int)v }));
        SliderRow("播放栏透明度", 0, 100, model.ContentBackdrop.BarClarity, async v => await model.ChooseContentBackdropAsync(model.ContentBackdrop with { BarClarity = (int)v }));
        Section("播放");
        Switch("记住音量", model.RememberVolume, async b => await model.SetRememberVolumeAsync(b));
        await model.RefreshOutputDevicesAsync();
        var devices = new[]
        {
            "跟随系统默认设备"
        }.Concat(model.OutputDevices.Select(d => d.Name)).ToArray();
        Select("音频输出", devices, Math.Max(0, model.OutputDevices.ToList().FindIndex(d => d.Id == model.ActiveOutputDeviceId) + 1), async i => await model.SetOutputDeviceAsync(i == 0 ? null : model.OutputDevices[i - 1].Id));
        Select("关闭窗口时", new[] { "每次询问", "最小化", "退出" }, Array.IndexOf(new[] { "ask", "minimize", "exit" }, model.CloseBehaviorPreference), async i => await model.SetCloseBehaviorAsync(new[] { "ask", "minimize", "exit" }[i]));
        Section("曲库");
        Switch("新添加的歌曲显示在前面", model.NewTracksFirst, async b => await model.SetNewTracksFirstAsync(b));
        Switch("启动时扫描音乐目录", model.AutoScanOnLaunch, async b => await model.SetAutoScanOnLaunchAsync(b));
        foreach (var dir in model.LibraryScanDirectories)
        {
            var path = dir;
            ButtonRow(path, "移除", async () =>
            {
                await model.RemoveLibraryScanDirectoryAsync(path);
                await ShowSettings();
            });
        }

        ButtonRow("音乐目录", "添加文件夹", async () =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            if (await picker.PickSingleFolderAsync()is { } folder)
            {
                await model.AddLibraryScanDirectoryAsync(folder.Path);
                await ShowSettings();
            }
        });
        ButtonRow("重新扫描已添加的目录", "立即扫描", async () => await model.RunManualLibraryScanAsync());
        Section("歌词");
        ButtonRow("桌面歌词胶囊", "打开", () =>
        {
            CapsuleWindow.ShowOrActivate();
            return Task.CompletedTask;
        });
        var capsule = model.CapsuleAppearance;
        Select("胶囊主题", new[] { "深色", "浅色", "随封面" }, Array.IndexOf(new[] { "dark", "light", "cover" }, capsule.Theme), async i => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { Theme = new[] { "dark", "light", "cover" }[i] }));
        Select("歌词字号", new[] { "小", "中", "大" }, Array.IndexOf(new[] { "small", "medium", "large" }, capsule.FontSize), async i => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { FontSize = new[] { "small", "medium", "large" }[i] }));
        Select("第二行", new[] { "歌曲信息", "下一句", "翻译", "不显示" }, Array.IndexOf(new[] { "meta", "next", "translation", "none" }, capsule.SecondLine), async i => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { SecondLine = new[] { "meta", "next", "translation", "none" }[i] }));
        Select("律动效果", new[] { "回声", "频谱", "关闭" }, Array.IndexOf(new[] { "echo", "spectrum", "off" }, capsule.Wave), async i => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { Wave = new[] { "echo", "spectrum", "off" }[i] }));
        SliderRow("胶囊不透明度", 60, 100, capsule.Opacity, async v => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { Opacity = (int)v }));
        Switch("封面旋转", capsule.Spin, async value => await model.SetCapsuleAppearanceAsync(model.CapsuleAppearance with { Spin = value }));
        await model.RefreshLyricsScanDirectoriesAsync();
        foreach (var dir in model.LyricsScanDirectories)
        {
            var path = dir;
            ButtonRow(path, "移除", async () =>
            {
                await model.RemoveLyricsScanDirectoryAsync(path);
                await ShowSettings();
            });
        }

        ButtonRow("歌词目录", "添加文件夹", async () =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
            if (await picker.PickSingleFolderAsync()is { } folder)
            {
                await model.AddLyricsScanDirectoryAsync(folder.Path);
                await ShowSettings();
            }
        });
        Section("存储与关于");
        ButtonRow("存储分析", "查看", async () => await new StorageAnalysisDialog(model) { XamlRoot = Root.XamlRoot }.ShowAsync());
        SettingsContent.Children.Add(new TextBlock { Text = "OFPlayer Community · WinUI 本机音乐 、视频与第三方媒体库 。", TextWrapping = TextWrapping.Wrap, Opacity = .65 });
        SettingsContent.Children.Add(new TextBlock { Text = "包含必需的最小每日活跃统计，不发送曲名、媒体路径或账号信息。", TextWrapping = TextWrapping.Wrap, Opacity = .65 });
    }

    void SliderRow(string label, double min, double max, double value, Func<double, Task> action)
    {
        var card = Card(label);
        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = 220
        };
        Grid.SetColumn(slider, 1);
        card.Children.Add(slider);
        slider.ValueChanged += async (_, e) => await action(e.NewValue);
    }

    void Section(string text) => SettingsContent.Children.Add(new TextBlock { Text = text, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new(0, 14, 0, 0) });
    Grid Card(string label)
    {
        var grid = new Grid
        {
            Padding = new(16),
            ColumnSpacing = 20,
            CornerRadius = new(8)
        };
        OFPlayer_WinUI.WindowTheme.Brush(grid, "Background", "CardBackgroundFillColorDefaultBrush");
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        SettingsContent.Children.Add(grid);
        return grid;
    }

    void Select(string label, string[] values, int index, Func<int, Task> action)
    {
        var card = Card(label);
        var combo = new ComboBox
        {
            ItemsSource = values,
            SelectedIndex = Math.Max(0, index),
            MinWidth = 220,
            MaxWidth = 400
        };
        Grid.SetColumn(combo, 1);
        card.Children.Add(combo);
        combo.SelectionChanged += async (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
                await action(combo.SelectedIndex);
        };
    }

    void Switch(string label, bool value, Func<bool, Task> action)
    {
        var card = Card(label);
        var toggle = new ToggleSwitch
        {
            IsOn = value
        };
        Grid.SetColumn(toggle, 1);
        card.Children.Add(toggle);
        toggle.Toggled += async (_, _) => await action(toggle.IsOn);
    }

    void ButtonRow(string label, string text, Func<Task> action)
    {
        var card = Card(label);
        var button = new Button
        {
            Content = text
        };
        Grid.SetColumn(button, 1);
        card.Children.Add(button);
        button.Click += async (_, _) => await action();
    }
}
