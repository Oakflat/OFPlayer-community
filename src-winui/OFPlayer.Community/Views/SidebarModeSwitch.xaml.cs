using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using OFPlayer_WinUI.Services;
using Windows.System;

namespace OFPlayer_WinUI.Views;
/// <summary>
/// The places at the top of the sidebar, as segments of one track: the music library
/// and local video. Picking one asks the shell to go there; the shell reports the
/// place it is in through <see cref = "SidebarModeSwitch.SelectedPlace"/>. In the compact pane the track stands.
/// </summary>
public enum SidebarPlace
{
    Music,
    Video
}

public sealed partial class SidebarModeSwitch : UserControl
{
    /// <summary>Raised when a place is picked, even the current one: the selected library.</summary>
    public event Action<SidebarPlace>? PlaceChosen;
    public SidebarModeSwitch()
    {
        InitializeComponent();
        // A radio button may handle the arrow keys itself; listen to handled ones too.
        Choices.AddHandler(KeyDownEvent, new KeyEventHandler(ChoicesKeyDown), true);
        Loaded += (_, _) =>
        {
            ApplyStrings();
            Localization.LanguageChanged += ApplyStrings;
        };
        Unloaded += (_, _) => Localization.LanguageChanged -= ApplyStrings;
    }

    /// <summary>The shell's current place. Does not raise <see cref = "PlaceChosen"/>.</summary>
    public SidebarPlace SelectedPlace { get => VideoChoice.IsChecked == true ? SidebarPlace.Video : SidebarPlace.Music; set => (value == SidebarPlace.Video ? VideoChoice : LibraryChoice).IsChecked = true; }

    /// <summary>A standing track for the compact pane.</summary>
    public bool Compact
    {
        set
        {
            Choices.Orientation = value ? Orientation.Vertical : Orientation.Horizontal;
            // The compact pane leaves 40 for the whole track; the segments fill it instead of keeping their own width.
            if (value)
                Track.Width = 40;
            else
                Track.ClearValue(WidthProperty);
            foreach (var choice in Choices.Children.OfType<RadioButton>())
            {
                if (value)
                    choice.Width = double.NaN;
                else
                    choice.ClearValue(WidthProperty);
            }

            Divider.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ApplyStrings()
    {
        AutomationProperties.SetName(Choices, Localization.Get("source.switch"));
        Label(LibraryChoice, Localization.Get("nav.library"));
        Label(VideoChoice, Localization.Get("source.video"));
        static void Label(RadioButton choice, string text)
        {
            AutomationProperties.SetName(choice, text);
            ToolTipService.SetToolTip(choice, text);
        }
    }

    private void ChoiceClick(object sender, RoutedEventArgs args) => PlaceChosen?.Invoke(ReferenceEquals(sender, VideoChoice) ? SidebarPlace.Video : SidebarPlace.Music);
    // Before the first layout the thumb has nowhere to go; the size change that follows places it.
    private void ChoiceChecked(object sender, RoutedEventArgs args)
    {
        if (Thumb is not null && Thumb.ActualWidth > 0)
            MoveThumb(animate: true);
    }

    // The pane changed between open and compact: the thumb jumps to the chosen segment's new place.
    private void ChoicesSizeChanged(object sender, SizeChangedEventArgs args) => MoveThumb(animate: false);
    private void MoveThumb(bool animate)
    {
        var chosen = VideoChoice.IsChecked == true ? VideoChoice : LibraryChoice;
        if (chosen.ActualWidth == 0)
            return;
        Thumb.Width = chosen.ActualWidth;
        Thumb.Height = chosen.ActualHeight;
        var at = chosen.TransformToVisual(Choices).TransformPoint(default);
        var target = new Vector3((float)at.X, (float)at.Y, 0);
        ElementCompositionPreview.SetIsTranslationEnabled(Thumb, true);
        var visual = ElementCompositionPreview.GetElementVisual(Thumb);
        if (!animate || !Motion.Enabled)
        {
            visual.StopAnimation("Translation");
            visual.Properties.InsertVector3("Translation", target);
            return;
        }

        var slide = visual.Compositor.CreateVector3KeyFrameAnimation();
        slide.InsertExpressionKeyFrame(0, "this.StartingValue");
        slide.InsertKeyFrame(1, target, Motion.Ease(visual.Compositor, Motion.Decelerate));
        slide.Duration = TimeSpan.FromMilliseconds(Motion.NormalMs);
        visual.StartAnimation("Translation", slide);
    }

    private void ChoicesKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var direction = args.Key switch
        {
            VirtualKey.Left or VirtualKey.Up => -1,
            VirtualKey.Right or VirtualKey.Down => 1,
            _ => 0
        };
        if (direction == 0)
            return;
        var choices = new[]
        {
            LibraryChoice,
            VideoChoice
        };
        var selected = Array.FindIndex(choices, choice => choice.IsChecked == true);
        var target = choices[(selected + direction + choices.Length) % choices.Length];
        args.Handled = true;
        target.Focus(FocusState.Keyboard);
        if (target.IsChecked == true)
            return;
        target.IsChecked = true;
        ChoiceClick(target, args);
    }
}
