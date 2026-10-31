using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OFPlayer_WinUI.ViewModels;
using OFPlayer_WinUI.Services;

namespace OFPlayer_WinUI.Views;
/// <summary>
/// Library switcher at the top of the sidebar: the active library stays visible,
/// the menu lists this computer's libraries and saved network libraries. In the
/// compact pane it is the same button reduced to its icon.
/// </summary>
public sealed partial class SourcePickerView : UserControl
{
    private MainViewModel? viewModel;
    private bool compact;
    public event EventHandler? NewLibraryRequested;
    public event EventHandler<RemoteConnectionItem>? RemoteConnectionSelected;
    public event EventHandler? AddRemoteRequested;
    public event EventHandler<RemoteConnectionItem>? RemoveRemoteRequested;
    public Func<string, MenuFlyout>? LibraryContextMenuFactory { get; set; }

    /// <summary>Icon-only presentation for the compact pane.</summary>
    public bool Compact
    {
        get => compact;
        set
        {
            if (compact == value)
                return;
            compact = value;
            LabelPanel.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            Refresh();
        }
    }

    public SourcePickerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("SourcePickerView.xaml.OnLoaded");
        viewModel = MainViewModel.Active;
        if (viewModel is null)
        {
            return;
        }

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.NavigationChanged += Refresh;
        Localization.LanguageChanged += Refresh;
        // If the core is already connected at load time, fetch connections now;
        // otherwise the IsConnected handler above reloads once it comes up.
        if (viewModel.IsConnected)
        {
            _ = viewModel.LoadRemoteConnectionsAsync();
        }

        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("SourcePickerView.xaml.OnUnloaded");
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.NavigationChanged -= Refresh;
            Localization.LanguageChanged -= Refresh;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("SourcePickerView.xaml.OnViewModelPropertyChanged");
        switch (args.PropertyName)
        {
            case nameof(MainViewModel.ActiveLibraryId):
            case nameof(MainViewModel.IsConnected):
            case nameof(MainViewModel.IsRemoteMode):
            case nameof(MainViewModel.ActiveRemoteName):
                if (args.PropertyName == nameof(MainViewModel.IsConnected) && viewModel?.IsConnected == true)
                {
                    // The picker loads before the core connects; reload once it is up.
                    _ = viewModel.LoadRemoteConnectionsAsync();
                }

                Refresh();
                break;
        }
    }

    private void Refresh()
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("SourcePickerView.xaml.Refresh");
        // The cloud library has nothing beside it to switch to; the place switch above is
        // the way back. Icon-only, the heading would just repeat that switch's cloud.
        Chevron.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PickerButton.IsHitTestVisible = PickerButton.IsTabStop = true;
        PickerButton.Visibility = Visibility.Visible;
        if (viewModel is null)
        {
            return;
        }

        var name = viewModel.IsRemoteMode ? viewModel.ActiveRemoteName : viewModel.SourceDisplayName;
        LibraryText.Text = name;
        IdentityText.Text = Localization.Get(!viewModel.IsConnected ? "source.notReady" : viewModel.IsRemoteMode ? "source.network" : "source.local");
        SourceIcon.Glyph = "";
        PickerButton.IsEnabled = viewModel.IsConnected;
        var label = Localization.Format("source.switch.named", ("name", name));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PickerButton, label);
        // The open pane shows the name itself; only the icon-only form needs it as a tip.
        ToolTipService.SetToolTip(PickerButton, compact ? label : Localization.Get("source.switch"));
    }

    private void OpenPicker(object sender, RoutedEventArgs args)
    {
        using var __perf = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.BeginOperation("SourcePickerView.xaml.OpenPicker");
        if (viewModel is null)
        {
            return;
        }

        if (viewModel.RemoteConnections.Count == 0)
        {
            // Fallback: connections may not be loaded yet; fetch for the next open.
            _ = viewModel.LoadRemoteConnectionsAsync();
        }

        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuFlyoutItem { Text = Localization.Get("source.section.local"), IsEnabled = false });
        foreach (var library in viewModel.Libraries)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = MainViewModel.LibraryDisplayName(library),
                GroupName = "source",
                IsChecked = !viewModel.IsRemoteMode && library.Id == viewModel.ActiveLibraryId,
                ContextFlyout = LibraryContextMenuFactory?.Invoke(library.Id),
            };
            var libraryId = library.Id;
            item.Click += (_, _) => viewModel.ActivateLibrary(libraryId);
            flyout.Items.Add(item);
        }

        var create = new MenuFlyoutItem
        {
            Text = Localization.Get("source.newLibrary"),
            Icon = new FontIcon
            {
                Glyph = ""
            }
        };
        create.Click += (_, _) => NewLibraryRequested?.Invoke(this, EventArgs.Empty);
        flyout.Items.Add(create);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(new MenuFlyoutItem { Text = Localization.Get("source.section.network"), IsEnabled = false });
        foreach (var connection in viewModel.RemoteConnections)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = string.IsNullOrWhiteSpace(connection.Provider) ? connection.Name : $"{connection.Name} · {ProviderName(connection.Provider)}",
                GroupName = "source",
                IsChecked = viewModel.IsRemoteMode && connection.Name == viewModel.ActiveRemoteName,
            };
            var captured = connection;
            item.Click += (_, _) => RemoteConnectionSelected?.Invoke(this, captured);
            var remove = new MenuFlyoutItem
            {
                Text = Localization.Get("network.remove")
            };
            remove.Click += (_, _) => RemoveRemoteRequested?.Invoke(this, captured);
            item.ContextFlyout = new MenuFlyout
            {
                Items =
                {
                    remove
                }
            };
            flyout.Items.Add(item);
        }

        var addRemote = new MenuFlyoutItem
        {
            Text = Localization.Get("source.addNetwork"),
            Icon = new FontIcon
            {
                Glyph = ""
            }
        };
        addRemote.Click += (_, _) => AddRemoteRequested?.Invoke(this, EventArgs.Empty);
        flyout.Items.Add(addRemote);
        flyout.Placement = compact ? Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.RightEdgeAlignedTop : Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft;
        flyout.ShowAt(PickerButton);
    }

    internal static string ProviderName(string provider) => provider.ToLowerInvariant() switch
    {
        "webdav" => "WebDAV",
        "subsonic" => "Subsonic",
        "ftp" => "FTP",
        _ => provider,
    };
}
