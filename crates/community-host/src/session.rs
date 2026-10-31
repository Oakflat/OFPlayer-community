pub mod frontend;
use ofplayer_community_core::{
    desktop_state::DesktopStateStore, desktop_types::*, lyrics, playback::*, storage,
};
use serde::{de::DeserializeOwned, Serialize};
use serde_json::{json, Value};
use std::path::{Path, PathBuf};

fn decode<T: DeserializeOwned>(value: Value) -> Result<T, String> {
    serde_json::from_value(value).map_err(|e| e.to_string())
}
fn encode(value: impl Serialize) -> Result<Value, String> {
    serde_json::to_value(value).map_err(|e| e.to_string())
}
fn text<'a>(value: &'a Value, key: &str) -> Result<&'a str, String> {
    value
        .get(key)
        .and_then(Value::as_str)
        .filter(|s| !s.is_empty())
        .ok_or_else(|| format!("Missing {key}"))
}
fn local_file(path: &str) -> Result<PathBuf, String> {
    let path = path.strip_prefix("\\\\?\\").unwrap_or(path);
    let bytes = path.as_bytes();
    if bytes.len() < 3
        || !bytes[0].is_ascii_alphabetic()
        || bytes[1] != b':'
        || !matches!(bytes[2], b'/' | b'\\')
        || path[2..].contains(':')
    {
        return Err("Community accepts local media files only".into());
    }
    let canonical = Path::new(path)
        .canonicalize()
        .map_err(|e| format!("Media file unavailable: {e}"))?;
    if !canonical.is_file() || canonical.to_string_lossy().starts_with("\\\\?\\UNC\\") {
        return Err("Local media file required".into());
    }
    Ok(canonical)
}

pub struct Session {
    state: DesktopStateStore,
    player: PlaybackManager,
    ended: u64,
    music_suspended: bool,
    external_track: Option<Value>,
    watch: ofplayer_community_core::watcher::StorageWatchManager,
    watch_sender: std::sync::mpsc::Sender<Value>,
    watch_events: std::sync::mpsc::Receiver<Value>,
}
pub fn import_files(args: Value) -> Result<Value, String> {
    let request: storage::PrepareTrackImportsRequest = decode(args)?;
    if request.files.len() > 20000 {
        return Err("Too many files in one import".into());
    }
    for file in &request.files {
        local_file(&file.source_path)?;
    }
    let library = request.library_id.clone();
    let report = storage::prepare_track_imports_with_progress(request, |_| {})?;
    let tracks = report
        .tracks
        .into_iter()
        .map(encode)
        .collect::<Result<Vec<_>, _>>()?;
    let mut state = DesktopStateStore::default();
    state.initialize()?;
    encode(
        state.import_library_prepared_tracks(&LibraryPreparedTrackImportRequest {
            library_id: library,
            tracks,
        })?,
    )
}
impl Session {
    pub fn open() -> Result<Self, String> {
        let mut state = DesktopStateStore::default();
        state.initialize()?;
        if state
            .load_catalog_snapshot(Some("none"))?
            .libraries
            .is_empty()
        {
            state.create_library(&LibraryCreateRequest {
                name: "我的音乐".into(),
            })?;
        }
        state.sync_session_with_catalog()?;
        let mut player = PlaybackManager::new();
        let preferences = state.load_preferences()?.unwrap_or(json!({}));
        let volume = if preferences["rememberVolume"].as_bool() == Some(false) {
            0.5
        } else {
            preferences["volume"].as_f64().unwrap_or(0.5)
        };
        player.set_volume(VolumeRequest { volume });
        if let Some(id) = preferences["playbackOutputDeviceId"].as_str() {
            if let Err(error) = player.set_output_device_preference(OutputDevicePreferenceRequest {
                device_id: Some(id.into()),
            }) {
                eprintln!("[community-host] preferred audio output unavailable: {error}");
            }
        }
        let (watch_sender, watch_events) = std::sync::mpsc::channel();
        Ok(Self {
            state,
            player,
            ended: 0,
            music_suspended: false,
            external_track: None,
            watch: Default::default(),
            watch_sender,
            watch_events,
        })
    }
    pub fn snapshot(&mut self) -> Result<Value, String> {
        Ok(json!({"playback":self.player.snapshot(),"session":self.state.load_session_snapshot()?}))
    }
    pub fn persist(&mut self) -> Result<(), String> {
        if self.player.snapshot().active_track_id.as_deref() == Some("external") {
            return Ok(());
        }
        self.state
            .update_session_playback_state(&self.player.snapshot())?;
        Ok(())
    }
    pub fn drain_watch(&self) -> Vec<Value> {
        self.watch_events.try_iter().take(64).collect()
    }
    pub fn shutdown(&mut self) -> Result<(), String> {
        self.watch.stop();
        self.player.pause();
        self.persist()?;
        self.player.reset();
        Ok(())
    }
    pub fn import_completed(&mut self) -> Result<(), String> {
        let mut state = DesktopStateStore::default();
        state.initialize()?;
        self.state = state;
        self.reconcile()
    }
    fn history(&self, id: &str, kind: &str, position: f64) -> Result<(), String> {
        let duration = self
            .state
            .get_track(id, false)?
            .and_then(|t| t["duration"].as_f64())
            .unwrap_or(0.0);
        self.state.append_history_entry(&json!({"id":uuid::Uuid::new_v4().to_string(),"trackId":id,"type":kind,"position":position,"duration":duration,"recordedAt":chrono::Utc::now().to_rfc3339()}))
    }
    fn select(
        &mut self,
        id: &str,
        queue: Option<Vec<String>>,
        autoplay: bool,
        position: f64,
    ) -> Result<Value, String> {
        self.state.preview_session_track(id, queue.as_deref())?;
        let track = self
            .state
            .get_track(id, false)?
            .ok_or("Track is not in the local library")?;
        let path = local_file(text(&track["source"], "path")?)?;
        let request = LoadTrackRequest {
            track_id: id.into(),
            path: path.to_string_lossy().into(),
            autoplay,
            start_time: Some(position),
            duration_hint: track["duration"].as_f64(),
            sample_rate: track["sampleRate"].as_u64().map(|n| n as u32),
            bit_depth: track["bitDepth"].as_u64().map(|n| n as u32),
            volume: None,
            delete_on_release: Some(false),
            media: Some(LoadTrackMediaMetadata {
                title: track["title"].as_str().map(String::from),
                artist: track["artist"].as_str().map(String::from),
                album: track["album"].as_str().map(String::from),
                cover_url: None,
            }),
        };
        // Do not persist a new selection until the file has loaded successfully.
        self.player.load_track(request)?;
        self.music_suspended = false;
        self.ended = self.player.snapshot().ended_counter;
        self.state.select_session_track(id, queue.as_deref())?;
        self.persist()?;
        if autoplay {
            self.history(id, "played", position)?;
        }
        self.snapshot()
    }
    fn play_current(&mut self) -> Result<Value, String> {
        let saved = self.state.load_session_snapshot()?;
        let id = saved
            .current_track_id
            .or_else(|| saved.queue_track_ids.first().cloned())
            .ok_or("The queue is empty")?;
        if self.player.snapshot().active_track_id.as_deref() == Some(&id) {
            self.player.play()?;
            self.music_suspended = false;
            self.ended = self.player.snapshot().ended_counter;
            self.persist()?;
            self.snapshot()
        } else {
            self.select(&id, None, true, saved.current_time)
        }
    }
    fn adjacent(&mut self, delta: isize) -> Result<Value, String> {
        let saved = self.state.load_session_snapshot()?;
        if saved.queue_track_ids.is_empty() {
            return Err("The queue is empty".into());
        }
        let index = saved
            .queue_track_ids
            .iter()
            .position(|id| Some(id) == saved.current_track_id.as_ref())
            .unwrap_or(0) as isize;
        let next = (index + delta).rem_euclid(saved.queue_track_ids.len() as isize) as usize;
        self.select(&saved.queue_track_ids[next], None, true, 0.0)
    }
    pub fn tick(&mut self) -> Result<(), String> {
        let state = self.player.snapshot();
        if state.ended_counter > self.ended {
            self.ended = state.ended_counter;
            if let Some(id) = state.ended_track_id {
                self.history(&id, "ended", state.duration)?;
                let saved = self.state.load_session_snapshot()?;
                let prefs = self.state.load_preferences()?.unwrap_or(json!({}));
                let mode = prefs["repeatMode"].as_str().unwrap_or("all");
                if !self.music_suspended
                    && state.active_track_id.as_deref() == Some(&id)
                    && saved.current_track_id.as_deref() == Some(&id)
                {
                    let position = saved
                        .queue_track_ids
                        .iter()
                        .position(|t| t == &id)
                        .unwrap_or(0);
                    let next = if mode == "one" {
                        Some(id.clone())
                    } else if prefs["shuffleEnabled"].as_bool() == Some(true)
                        && saved.queue_track_ids.len() > 1
                    {
                        let offset = 1 + uuid::Uuid::new_v4().as_u128() as usize
                            % (saved.queue_track_ids.len() - 1);
                        Some(
                            saved.queue_track_ids
                                [(position + offset) % saved.queue_track_ids.len()]
                            .clone(),
                        )
                    } else if position + 1 < saved.queue_track_ids.len() {
                        Some(saved.queue_track_ids[position + 1].clone())
                    } else if mode == "all" {
                        saved.queue_track_ids.first().cloned()
                    } else {
                        None
                    };
                    if let Some(next) = next {
                        if let Err(error) = self.select(&next, None, true, 0.0) {
                            eprintln!("[community-host] next track unavailable: {error}");
                        }
                    }
                }
            }
        }
        Ok(())
    }
    pub fn command(&mut self, command: &str, args: Value) -> Result<Value, String> {
        match command {
            "host_shutdown" => {
                self.shutdown()?;
                Ok(json!({}))
            }
            "catalog.snapshot" => encode(self.state.load_catalog_snapshot(Some("none"))?),
            "catalog.query" => encode(self.state.query_collection_tracks(&decode(args)?)?),
            "catalog.navigation" => encode(self.state.resolve_navigation_summary(&decode(args)?)?),
            "library.create" => encode(self.state.create_library(&decode(args)?)?),
            "library.rename" => self.state.rename_library(&decode(args)?),
            "library.delete" => {
                let result = self.state.delete_library(&decode(args)?)?;
                self.reconcile()?;
                encode(result)
            }
            "playlist.create" => self.state.create_playlist(&decode(args)?),
            "playlist.rename" => self.state.rename_playlist(&decode(args)?),
            "playlist.delete" => {
                let result = self.state.delete_playlist(&decode(args)?)?;
                self.reconcile()?;
                encode(result)
            }
            "playlist.add" => encode(self.state.add_track_to_playlist(&decode(args)?)?),
            "playlist.remove" => encode(self.state.remove_track_from_playlist(&decode(args)?)?),
            "track.favorite" => self.state.set_track_favorite(&decode(args)?),
            "track.get" => encode(self.state.get_track(text(&args, "trackId")?, true)?),
            "track.delete" => {
                let request: TrackBatchDeleteRequest = decode(args)?;
                let result = self.state.delete_tracks_from_library(&request)?;
                self.reconcile()?;
                encode(result)
            }
            "session.snapshot" => self.snapshot(),
            "session.select" => {
                let id = text(&args, "trackId")?.to_owned();
                let queue = args.get("queueTrackIds").cloned().map(decode).transpose()?;
                self.select(&id, queue, args["autoplay"].as_bool().unwrap_or(true), 0.0)
            }
            "session.queue" => {
                let ids: Vec<String> = decode(args["trackIds"].clone())?;
                let saved = self.state.set_session_queue(&ids)?;
                if self
                    .player
                    .snapshot()
                    .active_track_id
                    .is_some_and(|id| !saved.queue_track_ids.contains(&id))
                {
                    self.player.reset();
                } else {
                    self.persist()?;
                }
                self.snapshot()
            }
            "playback.play" => self.play_current(),
            "playback.pause" => {
                let state = self.player.pause();
                self.ended = state.ended_counter;
                self.persist()?;
                if let Some(id) = state.active_track_id {
                    self.history(&id, "paused", state.current_time)?;
                }
                self.snapshot()
            }
            "playback.next" => self.adjacent(1),
            "playback.previous" => self.adjacent(-1),
            "playback.seek" => {
                self.player.seek(decode(args)?)?;
                self.persist()?;
                self.snapshot()
            }
            "playback.volume" => {
                let request: VolumeRequest = decode(args)?;
                let volume = request.volume;
                self.player.set_volume(request);
                let mut p = self.state.load_preferences()?.unwrap_or(json!({}));
                p["volume"] = json!(volume);
                self.state.save_preferences(&p)?;
                self.snapshot()
            }
            "playback.suspend" => {
                self.music_suspended = true;
                let state = self.player.suspend_output();
                self.ended = state.ended_counter;
                self.persist()?;
                self.snapshot()
            }
            "output.devices" => encode(self.player.output_devices()?),
            "output.select" => {
                let request: OutputDevicePreferenceRequest = decode(args)?;
                let id = request.device_id.clone();
                let result = self.player.set_output_device_preference(request)?;
                let mut p = self.state.load_preferences()?.unwrap_or(json!({}));
                p["playbackOutputDeviceId"] = json!(id);
                self.state.save_preferences(&p)?;
                encode(result)
            }
            "preferences.load" => Ok(self.state.load_preferences()?.unwrap_or(json!({}))),
            "preferences.patch" => {
                let mut p = self.state.load_preferences()?.unwrap_or(json!({}));
                let object = args
                    .as_object()
                    .ok_or("Preferences patch must be an object")?;
                for (k, v) in object {
                    p[k] = v.clone();
                }
                self.state.save_preferences(&p)?;
                Ok(p)
            }
            "lyrics.resolve" => {
                let request: lyrics::ResolveTrackLyricsRequest = decode(args)?;
                local_file(&request.audio_path)?;
                encode(lyrics::resolve_track_lyrics(request)?)
            }
            "history.load" => encode(
                self.state
                    .load_recent_history(args["limit"].as_u64().unwrap_or(100).min(1000) as usize)?,
            ),
            "stats.load" => encode(self.state.load_listening_stats(&decode(args)?)?),
            _ => self.frontend_command(command, args),
        }
    }
    fn reconcile(&mut self) -> Result<(), String> {
        let live = self.player.snapshot();
        self.state.sync_session_with_catalog()?;
        if let Some(ref id) = live.active_track_id {
            if self.state.get_track(id, false)?.is_some() {
                self.state.update_session_playback_state(&live)?;
            } else {
                self.player.reset();
            }
        }
        Ok(())
    }
}
