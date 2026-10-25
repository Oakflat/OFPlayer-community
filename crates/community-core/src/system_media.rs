//! Native-host media bridge. WinUI owns Windows SMTC registration and display;
//! the Rust playback engine must not register a second media session.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct SystemMediaMetadata {
    pub track_id: String,
    pub title: String,
    pub artist: String,
    pub album: String,
    pub cover_url: String,
    pub duration: f64,
}

#[derive(Default)]
pub struct SystemMediaSession;

impl SystemMediaSession {
    pub fn sync(
        &mut self,
        _status: SystemPlaybackStatus,
        _current_time: f64,
        _metadata: Option<&SystemMediaMetadata>,
    ) {
        // The WinUI frontend receives playback snapshots over JSONL and owns SMTC.
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SystemPlaybackStatus {
    Stopped,
    Paused,
    Playing,
}
