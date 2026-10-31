using Microsoft.Web.WebView2.Core;

namespace OFPlayer_WinUI.Services;
/// <summary>Create one browser environment on demand, shared by both UI surfaces.</summary>
internal static class WebViewEnvironmentProvider
{
    private static Task<CoreWebView2Environment>? pending;
    // Both callers run on the UI dispatcher. Never create environments in parallel:
    // the native capsule and XAML control must use the same initialization task.
    public static async Task<CoreWebView2Environment> GetAsync()
    {
        using var trace = OFPlayer.WinUI.Core.Diagnostics.PerformanceTrace.Begin("webview.environment");
        if (pending is null || pending.IsFaulted || pending.IsCanceled)
            pending = CreateAsync();
        // A timeout leaves the original initialization in place. Retrying may await
        // it, but cannot race a second creation against a still-pending operation.
        return await pending.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ofplayer-community-webview2"), null);
        return environment;
    }
}
