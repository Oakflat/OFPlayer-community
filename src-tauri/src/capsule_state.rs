use crate::{
    capsule_artwork_cache::CapsuleArtworkRef,
    lyrics::{self, LyricsLine, ResolveTrackLyricsRequest, ResolvedTrackLyrics},
    playback::{PlaybackSnapshot, PlaybackStatus},
};
use serde::Serialize;
use serde_json::{json, Value};
use std::{
    collections::{HashMap, VecDeque},
    time::{SystemTime, UNIX_EPOCH},
};

pub const CAPSULE_LABEL: &str = "lyric-capsule";
pub const CAPSULE_STATE_EVENT: &str = "capsule://state";
pub const CAPSULE_PROGRESS_ANCHOR_EVENT: &str = "capsule://progress-anchor";

const CAPSULE_DIAGNOSTICS_VERSION: &str = "2026-05-09.3";
const CAPSULE_DIAGNOSTICS_RING_LIMIT: usize = 96;
const CAPSULE_PRESSURED_MS: u64 = 2_000;
const CAPSULE_DEGRADED_MS: u64 = 5_000;
const CAPSULE_PAUSED_MS: u64 = 5_000;
const CAPSULE_SLOW_SEND_MS: u64 = 50;
const CAPSULE_PRESSURED_SEND_MS: u64 = 100;
const CAPSULE_DEGRADED_SEND_MS: u64 = 500;
const CAPSULE_PAUSED_SEND_MS: u64 = 1_500;
const CAPSULE_LARGE_PAYLOAD_BYTES: usize = 16 * 1024;
const CAPSULE_MAX_TEXT_CHARS: usize = 180;
const CAPSULE_TIMELINE_MAX_TEXT_CHARS: usize = 120;
const CAPSULE_LYRIC_TIMELINE_BEHIND_SECONDS: f64 = 0.35;
const CAPSULE_LYRIC_TIMELINE_AHEAD_SECONDS: f64 = 18.0;
const CAPSULE_LYRIC_TIMELINE_MAX_LINES: usize = 10;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CapsuleBootState {
    pub seq: u64,
    pub track_id: Option<String>,
    pub title: String,
    pub artist: String,
    pub lyric_line: String,
    pub lyric_version: u64,
    pub lyric_index: Option<usize>,
    pub lyric_timeline: Vec<CapsuleLyricTimelineLine>,
    pub is_playing: bool,
    pub duration_ms: u64,
    pub position_ms: u64,
    pub sent_at_ms: u64,
    pub artwork_key: Option<String>,
    pub artwork_src: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CapsuleProgressAnchor {
    pub seq: u64,
    pub track_id: Option<String>,
    pub is_playing: bool,
    pub duration_ms: u64,
    pub position_ms: u64,
    pub sent_at_ms: u64,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CapsuleLyricTimelineLine {
    pub index: usize,
    pub text: String,
    pub start_ms: Option<u64>,
    pub end_ms: Option<u64>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum CapsuleBackpressureMode {
    Normal,
    Pressured,
    Degraded,
    Paused,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CapsuleBackpressureSnapshot {
    pub mode: CapsuleBackpressureMode,
    pub pressure_until_ms: u64,
    pub degraded_until_ms: u64,
    pub paused_until_ms: u64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CapsuleDiagnosticEntry {
    pub timestamp_ms: u64,
    pub event: String,
    pub send_kind: String,
    pub elapsed_ms: u64,
    pub payload_bytes: usize,
    pub ok: bool,
    pub mode: CapsuleBackpressureMode,
}

#[derive(Debug, Clone)]
struct CachedLyrics {
    text: String,
    lines: Vec<LyricsLine>,
    version: u64,
}

#[derive(Debug, Clone)]
struct ResolvedCapsuleLyric {
    line: String,
    version: u64,
    index: Option<usize>,
    timeline: Vec<CapsuleLyricTimelineLine>,
}

#[derive(Debug)]
pub struct CapsuleStateStore {
    seq: u64,
    ready: bool,
    pressure_until_ms: u64,
    degraded_until_ms: u64,
    paused_until_ms: u64,
    diagnostics: VecDeque<CapsuleDiagnosticEntry>,
    lyrics_cache: HashMap<String, CachedLyrics>,
    next_lyric_version: u64,
}

impl Default for CapsuleStateStore {
    fn default() -> Self {
        Self {
            seq: 0,
            ready: false,
            pressure_until_ms: 0,
            degraded_until_ms: 0,
            paused_until_ms: 0,
            diagnostics: VecDeque::new(),
            lyrics_cache: HashMap::new(),
            next_lyric_version: 1,
        }
    }
}

impl CapsuleStateStore {
    pub fn mark_ready(&mut self) {
        self.ready = true;
    }

    pub fn mark_closed(&mut self) {
        self.ready = false;
        self.pressure_until_ms = 0;
        self.degraded_until_ms = 0;
        self.paused_until_ms = 0;
    }

    pub fn is_ready(&self) -> bool {
        self.ready
    }

    pub fn next_seq(&mut self) -> u64 {
        self.seq = self.seq.saturating_add(1);
        self.seq
    }

    pub fn backpressure_snapshot(&self) -> CapsuleBackpressureSnapshot {
        CapsuleBackpressureSnapshot {
            mode: self.backpressure_mode(),
            pressure_until_ms: self.pressure_until_ms,
            degraded_until_ms: self.degraded_until_ms,
            paused_until_ms: self.paused_until_ms,
        }
    }

    pub fn backpressure_mode(&self) -> CapsuleBackpressureMode {
        let now = now_ms();

        if self.paused_until_ms > now {
            CapsuleBackpressureMode::Paused
        } else if self.degraded_until_ms > now {
            CapsuleBackpressureMode::Degraded
        } else if self.pressure_until_ms > now {
            CapsuleBackpressureMode::Pressured
        } else {
            CapsuleBackpressureMode::Normal
        }
    }

    pub fn allows_meter(&self) -> bool {
        matches!(
            self.backpressure_mode(),
            CapsuleBackpressureMode::Normal | CapsuleBackpressureMode::Pressured
        )
    }

    pub fn meter_interval_ms(&self) -> u64 {
        match self.backpressure_mode() {
            CapsuleBackpressureMode::Normal => 80,
            CapsuleBackpressureMode::Pressured => 250,
            CapsuleBackpressureMode::Degraded | CapsuleBackpressureMode::Paused => 1_000,
        }
    }

    pub fn allows_regular_state(&self) -> bool {
        matches!(self.backpressure_mode(), CapsuleBackpressureMode::Normal)
    }

    pub fn allows_priority_state(&self) -> bool {
        !matches!(self.backpressure_mode(), CapsuleBackpressureMode::Paused)
    }

    pub fn allows_progress_anchor(&self, send_kind: &str) -> bool {
        match self.backpressure_mode() {
            CapsuleBackpressureMode::Normal | CapsuleBackpressureMode::Pressured => true,
            CapsuleBackpressureMode::Degraded => {
                send_kind.contains("play") || send_kind.contains("pause")
            }
            CapsuleBackpressureMode::Paused => false,
        }
    }

    pub fn record_send_result(
        &mut self,
        send_kind: &str,
        elapsed_ms: u64,
        payload_bytes: usize,
        ok: bool,
    ) {
        let now = now_ms();

        if elapsed_ms > CAPSULE_PAUSED_SEND_MS {
            self.paused_until_ms = self.paused_until_ms.max(now + CAPSULE_PAUSED_MS);
        } else if elapsed_ms > CAPSULE_DEGRADED_SEND_MS {
            self.degraded_until_ms = self.degraded_until_ms.max(now + CAPSULE_DEGRADED_MS);
        } else if elapsed_ms > CAPSULE_PRESSURED_SEND_MS {
            self.pressure_until_ms = self.pressure_until_ms.max(now + CAPSULE_PRESSURED_MS);
        }

        if ok && elapsed_ms <= CAPSULE_SLOW_SEND_MS && payload_bytes <= CAPSULE_LARGE_PAYLOAD_BYTES
        {
            return;
        }

        self.push_diagnostic(CapsuleDiagnosticEntry {
            timestamp_ms: now,
            event: if ok {
                String::from("capsule_send_slow_or_large")
            } else {
                String::from("capsule_send_failed")
            },
            send_kind: String::from(send_kind),
            elapsed_ms,
            payload_bytes,
            ok,
            mode: self.backpressure_mode(),
        });
    }

    pub fn record_artwork_cache_result(&mut self, cache_ms: u64, cache_miss: bool) {
        if !cache_miss || cache_ms <= 100 {
            return;
        }

        self.push_diagnostic(CapsuleDiagnosticEntry {
            timestamp_ms: now_ms(),
            event: String::from("capsule_artwork_cache_miss_slow"),
            send_kind: String::from("artwork-cache"),
            elapsed_ms: cache_ms,
            payload_bytes: 0,
            ok: true,
            mode: self.backpressure_mode(),
        });
    }

    pub fn drain_diagnostics_summary(&mut self) -> Option<Value> {
        if self.diagnostics.is_empty() {
            return None;
        }

        let entries = self.diagnostics.drain(..).collect::<Vec<_>>();

        Some(json!({
            "diagnosticsVersion": CAPSULE_DIAGNOSTICS_VERSION,
            "entryCount": entries.len(),
            "backpressure": self.backpressure_snapshot(),
            "entries": entries,
        }))
    }

    pub fn build_boot_state(
        &mut self,
        playback: &PlaybackSnapshot,
        track: Option<&Value>,
        artwork: CapsuleArtworkRef,
    ) -> CapsuleBootState {
        let title = truncate_text(resolve_title(track), CAPSULE_MAX_TEXT_CHARS);
        let artist = truncate_text(resolve_artist(track), CAPSULE_MAX_TEXT_CHARS);
        let lyric = self.resolve_lyric_state(track, playback.current_time, &title);

        CapsuleBootState {
            seq: self.next_seq(),
            track_id: playback
                .active_track_id
                .clone()
                .or_else(|| track.and_then(|value| text_field(value, "id"))),
            title,
            artist,
            lyric_line: lyric.line,
            lyric_version: lyric.version,
            lyric_index: lyric.index,
            lyric_timeline: lyric.timeline,
            is_playing: playback.status == PlaybackStatus::Playing,
            duration_ms: seconds_to_ms(playback.duration),
            position_ms: seconds_to_ms(playback.current_time),
            sent_at_ms: now_ms(),
            artwork_key: artwork.artwork_key,
            artwork_src: artwork.artwork_src,
        }
    }

    pub fn build_progress_anchor(&mut self, playback: &PlaybackSnapshot) -> CapsuleProgressAnchor {
        CapsuleProgressAnchor {
            seq: self.next_seq(),
            track_id: playback.active_track_id.clone(),
            is_playing: playback.status == PlaybackStatus::Playing,
            duration_ms: seconds_to_ms(playback.duration),
            position_ms: seconds_to_ms(playback.current_time),
            sent_at_ms: now_ms(),
        }
    }

    fn resolve_lyric_state(
        &mut self,
        track: Option<&Value>,
        position_seconds: f64,
        fallback_title: &str,
    ) -> ResolvedCapsuleLyric {
        let Some(track) = track else {
            return ResolvedCapsuleLyric::fallback(fallback_title);
        };
        let Some(audio_path) = source_text_field(track, "path") else {
            return ResolvedCapsuleLyric::fallback(fallback_title);
        };

        let cache_key = format!(
            "{}::{}::{}",
            text_field(track, "id").unwrap_or_default(),
            audio_path,
            text_field(track, "lyricsPath").unwrap_or_default(),
        );

        if !self.lyrics_cache.contains_key(&cache_key) {
            let version = self.next_lyric_version;
            self.next_lyric_version = self.next_lyric_version.saturating_add(1);
            let cached = resolve_track_lyrics(track, &audio_path, position_seconds, version);
            self.lyrics_cache.insert(cache_key.clone(), cached);
        }

        let Some(cached) = self.lyrics_cache.get(&cache_key) else {
            return ResolvedCapsuleLyric::fallback(fallback_title);
        };

        let active_line_index = lyrics::find_active_line_index(&cached.lines, position_seconds);
        let active_line = active_line_index
            .and_then(|index| cached.lines.iter().find(|line| line.index == index))
            .map(|line| line.text.trim().to_string())
            .filter(|value| !value.is_empty())
            .or_else(|| first_non_empty_line(&cached.text));

        ResolvedCapsuleLyric {
            line: truncate_text(
                active_line.unwrap_or_else(|| String::from(fallback_title)),
                CAPSULE_MAX_TEXT_CHARS,
            ),
            version: cached.version,
            index: active_line_index,
            timeline: build_lyric_timeline(&cached.lines, active_line_index, position_seconds),
        }
    }

    fn push_diagnostic(&mut self, entry: CapsuleDiagnosticEntry) {
        self.diagnostics.push_back(entry);

        while self.diagnostics.len() > CAPSULE_DIAGNOSTICS_RING_LIMIT {
            self.diagnostics.pop_front();
        }
    }
}

impl ResolvedCapsuleLyric {
    fn fallback(fallback_title: &str) -> Self {
        Self {
            line: String::from(fallback_title),
            version: 0,
            index: None,
            timeline: Vec::new(),
        }
    }
}

fn resolve_track_lyrics(
    track: &Value,
    audio_path: &str,
    position_seconds: f64,
    version: u64,
) -> CachedLyrics {
    let request = ResolveTrackLyricsRequest {
        track_id: text_field(track, "id"),
        audio_path: String::from(audio_path),
        origin_path: source_text_field(track, "originPath"),
        title: text_field(track, "title").or_else(|| text_field(track, "displayTitle")),
        artist: text_field(track, "artist").or_else(|| text_field(track, "albumArtist")),
        album: text_field(track, "album"),
        file_name: text_field(track, "fileName"),
        lyrics_path: text_field(track, "lyricsPath"),
        lyrics_directories: None,
        position_seconds: Some(position_seconds),
    };

    match lyrics::resolve_track_lyrics(request) {
        Ok(resolved) => cached_lyrics_from_resolved(resolved, version),
        Err(_) => CachedLyrics {
            text: String::new(),
            lines: Vec::new(),
            version,
        },
    }
}

fn cached_lyrics_from_resolved(resolved: ResolvedTrackLyrics, version: u64) -> CachedLyrics {
    CachedLyrics {
        text: resolved.text,
        lines: resolved.lines,
        version,
    }
}

fn resolve_title(track: Option<&Value>) -> String {
    track
        .and_then(|value| {
            text_field(value, "displayTitle")
                .or_else(|| text_field(value, "title"))
                .or_else(|| text_field(value, "fileName"))
        })
        .unwrap_or_else(|| String::from("Music is ready"))
}

fn resolve_artist(track: Option<&Value>) -> String {
    track
        .and_then(|value| text_field(value, "artist").or_else(|| text_field(value, "albumArtist")))
        .unwrap_or_else(|| String::from("OFPlayer"))
}

pub fn track_artwork(track: Option<&Value>) -> Option<String> {
    track.and_then(|value| text_field(value, "artwork"))
}

fn text_field(value: &Value, key: &str) -> Option<String> {
    value
        .get(key)
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(String::from)
}

fn source_text_field(value: &Value, key: &str) -> Option<String> {
    value
        .get("source")
        .and_then(|source| source.get(key))
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(String::from)
}

fn first_non_empty_line(text: &str) -> Option<String> {
    text.lines()
        .map(str::trim)
        .find(|line| !line.is_empty())
        .map(String::from)
}

fn build_lyric_timeline(
    lines: &[LyricsLine],
    active_line_index: Option<usize>,
    position_seconds: f64,
) -> Vec<CapsuleLyricTimelineLine> {
    if lines.is_empty() {
        return Vec::new();
    }

    let safe_position_seconds = if position_seconds.is_finite() && position_seconds >= 0.0 {
        position_seconds
    } else {
        0.0
    };
    let keep_after_seconds =
        (safe_position_seconds - CAPSULE_LYRIC_TIMELINE_BEHIND_SECONDS).max(0.0);
    let keep_until_seconds = safe_position_seconds + CAPSULE_LYRIC_TIMELINE_AHEAD_SECONDS;
    let mut timeline = Vec::new();

    for line in lines {
        let Some(start_time) = line.start_time else {
            continue;
        };

        let is_active = active_line_index == Some(line.index);
        if !is_active
            && line
                .end_time
                .is_some_and(|end_time| end_time < keep_after_seconds)
        {
            continue;
        }
        if !is_active && start_time > keep_until_seconds {
            break;
        }

        let text = truncate_text(
            line.text.trim().to_string(),
            CAPSULE_TIMELINE_MAX_TEXT_CHARS,
        );
        if text.is_empty() {
            continue;
        }

        timeline.push(CapsuleLyricTimelineLine {
            index: line.index,
            text,
            start_ms: optional_seconds_to_ms(Some(start_time)),
            end_ms: optional_seconds_to_ms(line.end_time),
        });

        if timeline.len() >= CAPSULE_LYRIC_TIMELINE_MAX_LINES {
            break;
        }
    }

    timeline
}

fn truncate_text(value: String, max_chars: usize) -> String {
    if value.chars().count() <= max_chars {
        return value;
    }

    value.chars().take(max_chars).collect()
}

fn optional_seconds_to_ms(value: Option<f64>) -> Option<u64> {
    value
        .filter(|seconds| seconds.is_finite() && *seconds >= 0.0)
        .map(seconds_to_ms)
}

fn seconds_to_ms(value: f64) -> u64 {
    if !value.is_finite() || value <= 0.0 {
        return 0;
    }

    (value * 1_000.0).round().min(u64::MAX as f64) as u64
}

pub fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_millis().min(u128::from(u64::MAX)) as u64)
        .unwrap_or_default()
}
