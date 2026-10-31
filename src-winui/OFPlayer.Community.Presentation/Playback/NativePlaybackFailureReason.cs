namespace OFPlayer.WinUI.Core.Playback;

public static class NativePlaybackFailureReason
{
    // Emit only fixed codes: native errors can contain paths or peer credentials.
    public static string Classify(string message) => message switch
    {
        "XAudio2 remote source authorization is unavailable." => "playback-source-revoked",
        _ when message.StartsWith("XAudio2 pause failed:", StringComparison.Ordinal) => "playback-pause-failed",
        "XAudio2 output worker stopped." => "playback-output-worker-stopped",
        "XAudio2 PCM supply stopped progressing." => "playback-pcm-stalled",
        "XAudio2 PCM producer exited before end of stream." => "playback-pcm-producer-failed",
        "XAudio2 voice stopped progressing; output device may be unavailable." => "playback-output-stalled",
        "XAudio2 output is not paused." => "playback-pause-unconfirmed",
        _ => "native-playback-command-failed"
    };
}
