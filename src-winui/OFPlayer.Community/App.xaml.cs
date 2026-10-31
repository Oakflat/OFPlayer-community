using Microsoft.UI.Xaml;
using System.Security.Principal;

namespace OFPlayer.Community;
public partial class App : Application
{
    private Window? window;
    private readonly Mutex instance;
    public App()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Windows user unavailable");
        instance = new Mutex(false, @"Local\OFPlayer.Community.Instance." + sid);
        bool acquired;
        try { acquired = instance.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { OFPlayer_WinUI.Services.TrayIconHost.ActivateExisting(); Environment.Exit(0); return; }
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
    }
}
