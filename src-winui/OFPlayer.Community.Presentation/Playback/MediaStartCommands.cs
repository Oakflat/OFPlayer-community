namespace OFPlayer.WinUI.Core.Playback;

/// <summary>Commands that can acquire an audible output, including remote starts.</summary>
public static class MediaStartCommands
{
    public static bool RequiresOutput(string command) => command is
        "playback_session_select_track" or "playback_session_play_current"
        or "playback_session_next" or "playback_session_previous" or "playback_session_handle_ended"
        or "playback_play_loaded" or "playback_remote_load" or "playback_play_external"
        or "playback_seek" or "playback_seek_loaded"
        or "playback_recover_output" or "playback_set_audio_engine" or "playback_set_output_device"
        or "together_v2_preconnect" or "together_v2_start"
        or "mobile_handoff_start_host" or "mobile_handoff_take_back_playback";
}
