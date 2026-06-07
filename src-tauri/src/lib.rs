mod app_paths;
mod artwork_store;
mod audio_formats;
mod capsule_artwork_cache;
mod capsule_meter;
mod capsule_state;
mod capsule_window_region;
mod catalog_db;
mod db_helpers;
mod desktop_state;
mod desktop_types;
mod diagnostics;
mod dsd_playback;
mod external_sources;
mod library_import_jobs;
mod lyrics;
mod metadata;
mod mobile_handoff;
mod navigation;
mod playback;
mod schema;
mod session_ops;
mod sorting;
mod storage;
mod storage_maintenance;
mod system_media;
mod watcher;

use capsule_artwork_cache::CapsuleArtworkCache;
use capsule_meter::spawn_capsule_meter_emitter;
use capsule_state::{
    track_artwork, CapsuleBootState, CapsuleStateStore, CAPSULE_LABEL,
    CAPSULE_PROGRESS_ANCHOR_EVENT, CAPSULE_STATE_EVENT,
};
use capsule_window_region::{
    apply_capsule_hit_region, clear_capsule_hit_region, CapsuleHitRegionRequest,
};
use desktop_state::DesktopStateStore;
use desktop_types::{
    CatalogLoadSnapshotRequest, CatalogSnapshot, CollectionTrackQueryRequest, DeleteRecordsRequest,
    DesktopBootstrapRequest, DesktopBootstrapSnapshot, DesktopStateResetResult, HistoryLoadRequest,
    LibraryCreateRequest, LibraryCreateResult, LibraryDeleteRequest, LibraryDeleteResult,
    LibraryImportCandidatesRequest, LibraryImportFileInput, LibraryPreparedTrackImportRequest,
    LibraryRenameRequest, LibraryReorderRequest, LocalIndexInvalidationRequest,
    LocalIndexInvalidationResult, NavigationQueryRequest, NavigationQueryResult,
    PlaylistCreateRequest, PlaylistDeleteRequest, PlaylistDeleteResult, PlaylistRenameRequest,
    PlaylistReorderRequest, PlaylistTrackMutationRequest, PlaylistTrackMutationResult,
    PlaylistTrackRemoveRequest, PlaylistTrackRemoveResult, PlaylistTrackReorderRequest,
    SessionPreviousRequest, SessionQueueRequest, SessionSelectTrackRequest, SessionStateSnapshot,
    StorageGarbageCollectionResult, StorageUsageSnapshot, TrackBatchDeleteRequest,
    TrackBatchDeleteResult, TrackDeleteRequest, TrackDeleteResult, TrackFavoriteRequest,
    TrackLookupRequest, TrackUpdateRequest, UpsertRecordsRequest,
};
use diagnostics::{
    build_diagnostic_step_profile, build_process_resource_diagnostics,
    capture_process_resource_snapshot, DiagnosticStepProfile, DiagnosticsLogEventRequest,
    DiagnosticsLogStatus, DiagnosticsLogStore, ProcessResourceSnapshot,
};
use external_sources::{
    ExternalLibraryConnectionRequest, ExternalLibraryListResult, ExternalLibraryTestResult,
    ExternalPlaybackSourceRequest, ExternalPlaybackSourceResult, ExternalProviderCapabilities,
    ExternalProviderCapabilitiesRequest, ExternalTrackListResult,
};
use library_import_jobs::{
    build_library_import_diagnostics_from_job, create_library_import_job_snapshot,
    emit_library_import_job_progress, finalize_library_import_job, mark_library_import_job_failed,
    persist_library_import_job_snapshot, progress_percent, progress_percent_from_ratio,
    update_library_import_job_stage, LibraryImportDiagnostics, LibraryImportJobSnapshot,
    LibraryImportJobStore, IMPORT_JOB_STATUS_COMPLETED, IMPORT_JOB_STATUS_EMPTY,
    IMPORT_JOB_STATUS_FAILED, IMPORT_STAGE_DISCOVER, IMPORT_STAGE_FILTER, IMPORT_STAGE_PERSIST,
    IMPORT_STAGE_PLAYBACK_SYNC, IMPORT_STAGE_PREPARE, IMPORT_STAGE_STATUS_COMPLETED,
    IMPORT_STAGE_STATUS_RUNNING, IMPORT_STAGE_STATUS_SKIPPED,
};
use lyrics::{ResolveTrackLyricsRequest, ResolvedTrackLyrics};
use metadata::{MetadataParseResult, ParseAudioMetadataRequest, ParsedAudioMetadata};
use mobile_handoff::{
    commands::{
        mobile_handoff_capabilities, mobile_handoff_record_event, mobile_handoff_state_snapshot,
    },
    MobileHandoffState,
};
use playback::{
    LoadTrackMediaMetadata, LoadTrackRequest, OutputDevicePreferenceRequest, PlaybackManager,
    PlaybackOutputDeviceChangeResult, PlaybackOutputDevicesSnapshot, PlaybackSnapshot, SeekRequest,
    VolumeRequest,
};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use sorting::QueryTracksResult;
use std::{
    path::Path,
    sync::Mutex,
    thread,
    time::{Duration, Instant},
};
use storage::{
    PrepareTrackImportInput, PrepareTrackImportsDiagnostics, PrepareTrackImportsProgress,
    PrepareTrackImportsRequest, ScanAudioFilesProgress, ScanDirectoriesRequest,
};
use tauri::{AppHandle, Emitter, Manager, State};
use uuid::Uuid;
use watcher::{ConfigureStorageWatchRequest, StorageWatchManager, StorageWatchSnapshot};

const PLAYBACK_HISTORY_TYPE_PLAYED: &str = "played";
const PLAYBACK_HISTORY_TYPE_PAUSED: &str = "paused";
const PLAYBACK_HISTORY_TYPE_ENDED: &str = "ended";
const PLAYBACK_SNAPSHOT_EVENT: &str = "playback://snapshot";
const PLAYBACK_SNAPSHOT_INTERVAL_MS: u64 = 250;
const MAIN_WINDOW_LABEL: &str = "main";
const LYRIC_CAPSULE_ROUTE: &str = "lyric-capsule.html";
const LYRIC_CAPSULE_WINDOW_WIDTH: f64 = 560.0;
const LYRIC_CAPSULE_WINDOW_HEIGHT: f64 = 112.0;
const MAX_SYSTEM_MEDIA_EMBEDDED_COVER_BYTES: usize = 768 * 1024;

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct PlaybackCommandResult {
    session: SessionStateSnapshot,
    playback: PlaybackSnapshot,
    history_entries: Vec<Value>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct LibraryDeleteCommandResult {
    #[serde(flatten)]
    mutation: LibraryDeleteResult,
    session: SessionStateSnapshot,
    playback: PlaybackSnapshot,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct TrackDeleteCommandResult {
    #[serde(flatten)]
    mutation: TrackDeleteResult,
    session: SessionStateSnapshot,
    playback: PlaybackSnapshot,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct TrackBatchDeleteCommandResult {
    #[serde(flatten)]
    mutation: TrackBatchDeleteResult,
    session: SessionStateSnapshot,
    playback: PlaybackSnapshot,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct DesktopStateResetCommandResult {
    #[serde(flatten)]
    reset: DesktopStateResetResult,
    playback: PlaybackSnapshot,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct WindowSurfacePlatformProfile {
    platform: String,
    major_version: Option<u32>,
    minor_version: Option<u32>,
    build_number: Option<u32>,
    is_windows: bool,
    is_windows_10: bool,
    is_windows_11_or_newer: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ImmersiveWindowModeRequest {
    hide_taskbar: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct ImmersiveWindowModeSnapshot {
    fullscreen: bool,
    maximized: bool,
    always_on_top: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct LibraryManagedImportRequest {
    library_id: String,
    files: Vec<LibraryImportFileInput>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct LibraryScanImportRequest {
    library_id: String,
    directories: Vec<String>,
    respect_deleted_import_paths: Option<bool>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct LibraryImportCommandResult {
    job: LibraryImportJobSnapshot,
    imported_tracks: Vec<Value>,
    invalidated_track_ids: Vec<String>,
    invalidated_relation_ids: Vec<String>,
    reordered_tracks: Vec<Value>,
    discovered_total: usize,
    candidate_total: usize,
    diagnostics: LibraryImportDiagnostics,
    session: SessionStateSnapshot,
    playback: PlaybackSnapshot,
    history_entries: Vec<Value>,
}

fn optional_text(value: &Value, field: &str) -> Option<String> {
    value
        .get(field)
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|text| !text.is_empty())
        .map(String::from)
}

fn optional_number(value: &Value, field: &str) -> Option<f64> {
    value
        .get(field)
        .and_then(Value::as_f64)
        .filter(|number| number.is_finite() && *number >= 0.0)
}

fn optional_u32(value: &Value, field: &str) -> Option<u32> {
    value
        .get(field)
        .and_then(Value::as_u64)
        .and_then(|number| u32::try_from(number).ok())
        .filter(|number| *number > 0)
}

fn optional_track_source_path(value: &Value) -> Option<String> {
    value
        .get("source")
        .and_then(|source| source.get("path"))
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|path| !path.is_empty())
        .map(String::from)
}

fn optional_track_source_bool(value: &Value, field: &str) -> Option<bool> {
    value
        .get("source")
        .and_then(|source| source.get(field))
        .and_then(Value::as_bool)
}

fn optional_track_source_kind(value: &Value) -> Option<String> {
    value
        .get("source")
        .and_then(|source| source.get("kind"))
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|kind| !kind.is_empty())
        .map(String::from)
}

fn track_needs_external_playback_resolution(value: &Value) -> bool {
    let has_connection_id = value
        .get("source")
        .and_then(|source| source.get("connectionId"))
        .and_then(Value::as_str)
        .map(str::trim)
        .is_some_and(|connection_id| !connection_id.is_empty());
    let source_kind = optional_track_source_kind(value).unwrap_or_default();
    let source_path = optional_track_source_path(value).unwrap_or_default();

    has_connection_id
        && (matches!(
            source_kind.as_str(),
            "webdav" | "subsonic" | "external-url" | "external-index"
        ) || source_path.starts_with("http://")
            || source_path.starts_with("https://"))
}

fn track_with_playback_source_override(mut track: Value, playback_source: Option<&Value>) -> Value {
    let Some(source_override) = playback_source.and_then(Value::as_object) else {
        return track;
    };
    let Some(track_object) = track.as_object_mut() else {
        return track;
    };

    let mut source = track_object
        .get("source")
        .and_then(Value::as_object)
        .cloned()
        .unwrap_or_default();

    for (key, value) in source_override {
        source.insert(key.clone(), value.clone());
    }

    track_object.insert(String::from("source"), Value::Object(source));
    track
}

fn optional_system_media_artwork(value: &Value) -> Option<String> {
    let artwork = optional_text(value, "artwork")?;

    if artwork.starts_with("data:") && artwork.len() > MAX_SYSTEM_MEDIA_EMBEDDED_COVER_BYTES {
        return None;
    }

    Some(artwork)
}

fn build_load_track_request(
    track: &Value,
    autoplay: bool,
    start_time: Option<f64>,
) -> Result<LoadTrackRequest, String> {
    let track_id = optional_text(track, "id").ok_or_else(|| {
        String::from("Desktop playback session could not resolve the selected track id.")
    })?;
    let path = optional_track_source_path(track).ok_or_else(|| {
        String::from("This track does not have an indexed local path. Re-scan its source folder before playing it.")
    })?;
    let title = optional_text(track, "title")
        .or_else(|| optional_text(track, "displayTitle"))
        .or_else(|| optional_text(track, "fileName"))
        .unwrap_or_else(|| String::from("Untitled"));
    let artwork = optional_system_media_artwork(track);
    let requested_delete_on_release = optional_track_source_bool(track, "deleteOnRelease")
        .unwrap_or(false)
        || optional_track_source_bool(track, "transient").unwrap_or(false)
        || optional_track_source_kind(track).as_deref() == Some("external-temp");

    Ok(LoadTrackRequest {
        track_id,
        delete_on_release: Some(
            requested_delete_on_release && can_delete_playback_path_on_release(&path),
        ),
        path,
        autoplay,
        start_time,
        duration_hint: optional_number(track, "duration"),
        sample_rate: optional_u32(track, "sampleRate"),
        bit_depth: optional_u32(track, "bitDepth"),
        volume: None,
        media: Some(LoadTrackMediaMetadata {
            title: Some(title),
            artist: optional_text(track, "artist").or_else(|| optional_text(track, "albumArtist")),
            album: optional_text(track, "album"),
            cover_url: artwork,
        }),
    })
}

fn sanitize_load_track_request(mut request: LoadTrackRequest) -> LoadTrackRequest {
    if request.delete_on_release.unwrap_or(false)
        && !can_delete_playback_path_on_release(&request.path)
    {
        request.delete_on_release = Some(false);
    }

    request
}

fn can_delete_playback_path_on_release(path: &str) -> bool {
    let Ok(cache_dir) = app_paths::cache_dir() else {
        return false;
    };
    path_is_inside_existing_directory(path, &cache_dir.join("external-sources"))
}

fn path_is_inside_existing_directory(path: &str, root: &Path) -> bool {
    let path = Path::new(path);
    let Ok(path) = path.canonicalize() else {
        return false;
    };
    let Ok(root) = root.canonicalize() else {
        return false;
    };

    path.is_file() && path.starts_with(root)
}

fn is_recoverable_session_playback_error(error: &str) -> bool {
    matches!(
        error,
        "Selected track path is not available on disk."
            | "This track does not have an indexed local path. Re-scan its source folder before playing it."
    )
}

fn build_history_entry(
    entry_type: &str,
    playback: &PlaybackSnapshot,
    explicit_track_id: Option<&str>,
) -> Option<Value> {
    let track_id = explicit_track_id
        .map(str::trim)
        .filter(|track_id| !track_id.is_empty())
        .map(String::from)
        .or_else(|| playback.active_track_id.clone());

    track_id.map(|track_id| {
        json!({
            "id": format!("history-{}", Uuid::new_v4()),
            "trackId": track_id,
            "type": entry_type,
            "position": playback.current_time.max(0.0),
            "duration": playback.duration.max(0.0),
            "recordedAt": chrono::Utc::now().to_rfc3339(),
        })
    })
}

fn command_result(
    session: SessionStateSnapshot,
    playback: PlaybackSnapshot,
    history_entries: Vec<Value>,
) -> PlaybackCommandResult {
    PlaybackCommandResult {
        session,
        playback,
        history_entries,
    }
}

fn resumable_session_start_time(session: &SessionStateSnapshot) -> f64 {
    if session.current_time <= 0.0 || !session.current_time.is_finite() {
        return 0.0;
    }

    if session.duration > 0.0 && session.current_time >= session.duration {
        return 0.0;
    }

    session.current_time
}

fn reconcile_playback_with_session(
    desktop_state: &DesktopStateStore,
    playback: &mut PlaybackManager,
    session: &SessionStateSnapshot,
) -> Result<PlaybackSnapshot, String> {
    let snapshot = playback.snapshot();

    if snapshot.active_track_id == session.current_track_id {
        return Ok(snapshot);
    }

    match session.current_track_id.as_deref() {
        Some(_) => load_session_track_into_playback(
            desktop_state,
            playback,
            session,
            false,
            resumable_session_start_time(session),
        ),
        None => Ok(playback.reset()),
    }
}

fn sync_catalog_playback_state(
    desktop_state: &DesktopStateStore,
    playback: &mut PlaybackManager,
) -> Result<PlaybackCommandResult, String> {
    let mut session = desktop_state.sync_session_with_catalog()?;
    let mut remaining_recovery_attempts = session.queue_track_ids.len().max(1);

    loop {
        match reconcile_playback_with_session(desktop_state, playback, &session) {
            Ok(playback_snapshot) => {
                let session = desktop_state.update_session_playback_state(&playback_snapshot)?;
                return Ok(command_result(session, playback_snapshot, Vec::new()));
            }
            Err(error)
                if is_recoverable_session_playback_error(&error)
                    && session.current_track_id.is_some()
                    && remaining_recovery_attempts > 0 =>
            {
                remaining_recovery_attempts -= 1;
                let failed_track_id = session.current_track_id.clone().unwrap_or_default();
                session = desktop_state.remove_track_from_session_queue(&failed_track_id)?;

                if session.current_track_id.is_none() {
                    return Ok(command_result(session, playback.reset(), Vec::new()));
                }
            }
            Err(error) => return Err(error),
        }
    }
}

fn emit_playback_snapshot(app: &AppHandle, snapshot: &PlaybackSnapshot) {
    let _ = app.emit(PLAYBACK_SNAPSHOT_EVENT, snapshot.clone());
}

fn capsule_payload_bytes<T: Serialize>(payload: &T) -> usize {
    serde_json::to_vec(payload)
        .map(|bytes| bytes.len())
        .unwrap_or_default()
}

fn emit_capsule_payload<T: Clone + Serialize>(
    app: &AppHandle,
    event: &str,
    payload: &T,
    send_kind: &str,
) -> bool {
    let payload_bytes = capsule_payload_bytes(payload);
    let started_at = Instant::now();
    let result = app.emit_to(CAPSULE_LABEL, event, payload.clone());
    let elapsed = elapsed_ms(started_at);
    let ok = result.is_ok();

    if elapsed > 50 || payload_bytes > 16 * 1024 || !ok {
        let capsule_state = app.state::<Mutex<CapsuleStateStore>>();

        if let Ok(mut state) = capsule_state.lock() {
            state.record_send_result(send_kind, elapsed, payload_bytes, ok);
        };
    }

    ok
}

fn load_capsule_track(app: &AppHandle, playback: &PlaybackSnapshot) -> Option<Value> {
    let track_id = playback.active_track_id.as_deref()?;
    let desktop_state = app.state::<Mutex<DesktopStateStore>>();
    let desktop_state = desktop_state.lock().ok()?;

    desktop_state.get_track(track_id, true).ok().flatten()
}

fn build_capsule_boot_state(
    app: &AppHandle,
    playback: &PlaybackSnapshot,
    track: Option<Value>,
    capsule_state: &mut CapsuleStateStore,
    artwork_cache: &mut CapsuleArtworkCache,
) -> CapsuleBootState {
    let artwork = track_artwork(track.as_ref());
    let artwork_ref =
        artwork_cache.resolve(app, playback.active_track_id.as_deref(), artwork.as_deref());
    capsule_state.record_artwork_cache_result(artwork_ref.cache_ms, artwork_ref.cache_miss);

    capsule_state.build_boot_state(playback, track.as_ref(), artwork_ref)
}

fn emit_capsule_progress_anchor_async(
    app: &AppHandle,
    snapshot: &PlaybackSnapshot,
    reason: &'static str,
) {
    let app_handle = app.clone();
    let snapshot = snapshot.clone();

    thread::spawn(move || {
        let anchor = {
            let capsule_state = app_handle.state::<Mutex<CapsuleStateStore>>();
            let mut state = match capsule_state.lock() {
                Ok(state) => state,
                Err(_) => return,
            };

            if !state.is_ready() || !state.allows_progress_anchor(reason) {
                return;
            }

            state.build_progress_anchor(&snapshot)
        };

        emit_capsule_payload(&app_handle, CAPSULE_PROGRESS_ANCHOR_EVENT, &anchor, reason);
    });
}

fn emit_capsule_state_async(app: &AppHandle, snapshot: &PlaybackSnapshot, reason: &'static str) {
    let app_handle = app.clone();
    let snapshot = snapshot.clone();

    thread::spawn(move || {
        let should_send = {
            let capsule_state = app_handle.state::<Mutex<CapsuleStateStore>>();
            let ready = match capsule_state.lock() {
                Ok(state) => state.is_ready() && state.allows_priority_state(),
                Err(_) => return,
            };
            ready
        };

        if !should_send {
            return;
        }

        let track = load_capsule_track(&app_handle, &snapshot);
        let state_payload = {
            let artwork_cache = app_handle.state::<Mutex<CapsuleArtworkCache>>();
            let mut artwork_cache = match artwork_cache.lock() {
                Ok(artwork_cache) => artwork_cache,
                Err(_) => return,
            };
            let capsule_state = app_handle.state::<Mutex<CapsuleStateStore>>();
            let mut capsule_state = match capsule_state.lock() {
                Ok(capsule_state) => capsule_state,
                Err(_) => return,
            };

            if !capsule_state.is_ready() || !capsule_state.allows_priority_state() {
                return;
            }

            build_capsule_boot_state(
                &app_handle,
                &snapshot,
                track,
                &mut capsule_state,
                &mut artwork_cache,
            )
        };

        emit_capsule_payload(&app_handle, CAPSULE_STATE_EVENT, &state_payload, reason);
    });
}

fn spawn_capsule_state_emitter(app_handle: AppHandle) {
    thread::spawn(move || loop {
        thread::sleep(Duration::from_millis(1_500));

        let should_send = {
            let capsule_state = app_handle.state::<Mutex<CapsuleStateStore>>();
            let should_send = match capsule_state.lock() {
                Ok(state) => state.is_ready() && state.allows_regular_state(),
                Err(_) => break,
            };

            should_send
        };

        if !should_send {
            continue;
        }

        let playback_snapshot = {
            let playback_state = app_handle.state::<Mutex<PlaybackManager>>();
            let mut playback = match playback_state.lock() {
                Ok(playback) => playback,
                Err(_) => break,
            };

            playback.snapshot()
        };

        let track = load_capsule_track(&app_handle, &playback_snapshot);
        let state_payload = {
            let artwork_cache = app_handle.state::<Mutex<CapsuleArtworkCache>>();
            let mut artwork_cache = match artwork_cache.lock() {
                Ok(artwork_cache) => artwork_cache,
                Err(_) => break,
            };
            let capsule_state = app_handle.state::<Mutex<CapsuleStateStore>>();
            let mut capsule_state = match capsule_state.lock() {
                Ok(capsule_state) => capsule_state,
                Err(_) => break,
            };

            if !capsule_state.is_ready() || !capsule_state.allows_regular_state() {
                continue;
            }

            build_capsule_boot_state(
                &app_handle,
                &playback_snapshot,
                track,
                &mut capsule_state,
                &mut artwork_cache,
            )
        };

        emit_capsule_payload(
            &app_handle,
            CAPSULE_STATE_EVENT,
            &state_payload,
            "state-calibration",
        );
    });
}

fn elapsed_ms(start: Instant) -> u64 {
    let elapsed = start.elapsed().as_millis();
    elapsed.min(u128::from(u64::MAX)) as u64
}

fn push_diagnostic_step_profile(
    step_profiles: &mut Vec<DiagnosticStepProfile>,
    key: &str,
    started_at: Instant,
    resource_start: Option<ProcessResourceSnapshot>,
) {
    let resource_end = capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        key,
        elapsed_ms(started_at),
        resource_start.as_ref(),
        resource_end.as_ref(),
    ));
}

fn append_diagnostics_event(app: &AppHandle, request: DiagnosticsLogEventRequest) {
    let diagnostics = app.state::<Mutex<DiagnosticsLogStore>>();
    let Ok(mut diagnostics) = diagnostics.lock() else {
        return;
    };

    if diagnostics.initialize(app).is_err() {
        return;
    }

    let _ = diagnostics.append_event(&request);
}

fn spawn_playback_snapshot_emitter(app_handle: AppHandle) {
    thread::spawn(move || {
        let mut last_snapshot: Option<PlaybackSnapshot> = None;

        loop {
            let snapshot = {
                let playback_state = app_handle.state::<Mutex<PlaybackManager>>();
                let mut playback = match playback_state.lock() {
                    Ok(playback) => playback,
                    Err(_) => break,
                };

                playback.snapshot()
            };

            if last_snapshot.as_ref() != Some(&snapshot) {
                emit_playback_snapshot(&app_handle, &snapshot);
                last_snapshot = Some(snapshot);
            }

            thread::sleep(Duration::from_millis(PLAYBACK_SNAPSHOT_INTERVAL_MS));
        }
    });
}

fn load_session_track_into_playback(
    desktop_state: &DesktopStateStore,
    playback: &mut PlaybackManager,
    session: &SessionStateSnapshot,
    autoplay: bool,
    start_time: f64,
) -> Result<PlaybackSnapshot, String> {
    load_session_track_into_playback_with_source(
        desktop_state,
        playback,
        session,
        autoplay,
        start_time,
        None,
    )
}

fn load_session_track_into_playback_with_source(
    desktop_state: &DesktopStateStore,
    playback: &mut PlaybackManager,
    session: &SessionStateSnapshot,
    autoplay: bool,
    start_time: f64,
    playback_source: Option<&Value>,
) -> Result<PlaybackSnapshot, String> {
    let track_id = session
        .current_track_id
        .as_deref()
        .ok_or_else(|| String::from("No track is available in the current playback queue."))?;
    let mut track = desktop_state
        .get_track(track_id, false)?
        .ok_or_else(|| String::from("Selected track was not found in the desktop catalog."))?;

    if playback_source.is_none() && track_needs_external_playback_resolution(&track) {
        return Ok(playback.reset());
    }

    track = track_with_playback_source_override(track, playback_source);

    playback.load_track(build_load_track_request(
        &track,
        autoplay,
        Some(start_time.max(0.0)),
    )?)
}

#[tauri::command]
fn window_surface_platform_profile() -> WindowSurfacePlatformProfile {
    resolve_window_surface_platform_profile()
}

fn resolve_window_surface_platform_profile() -> WindowSurfacePlatformProfile {
    #[cfg(target_os = "windows")]
    {
        if let Some((major_version, minor_version, build_number)) = read_windows_os_version() {
            WindowSurfacePlatformProfile {
                platform: String::from("windows"),
                major_version: Some(major_version),
                minor_version: Some(minor_version),
                build_number: Some(build_number),
                is_windows: true,
                is_windows_10: major_version == 10 && build_number < 22_000,
                is_windows_11_or_newer: major_version > 10
                    || (major_version == 10 && build_number >= 22_000),
            }
        } else {
            WindowSurfacePlatformProfile {
                platform: String::from("windows"),
                major_version: None,
                minor_version: None,
                build_number: None,
                is_windows: true,
                is_windows_10: false,
                is_windows_11_or_newer: false,
            }
        }
    }

    #[cfg(not(target_os = "windows"))]
    {
        WindowSurfacePlatformProfile {
            platform: String::from(std::env::consts::OS),
            major_version: None,
            minor_version: None,
            build_number: None,
            is_windows: false,
            is_windows_10: false,
            is_windows_11_or_newer: false,
        }
    }
}

#[cfg(target_os = "windows")]
fn read_windows_os_version() -> Option<(u32, u32, u32)> {
    #[repr(C)]
    struct RtlOsVersionInfoW {
        size: u32,
        major_version: u32,
        minor_version: u32,
        build_number: u32,
        platform_id: u32,
        service_pack: [u16; 128],
    }

    #[link(name = "ntdll")]
    extern "system" {
        fn RtlGetVersion(version_information: *mut RtlOsVersionInfoW) -> i32;
    }

    let mut version_info = RtlOsVersionInfoW {
        size: std::mem::size_of::<RtlOsVersionInfoW>() as u32,
        major_version: 0,
        minor_version: 0,
        build_number: 0,
        platform_id: 0,
        service_pack: [0; 128],
    };

    let status = unsafe { RtlGetVersion(&mut version_info) };

    if status < 0 || version_info.major_version == 0 {
        return None;
    }

    Some((
        version_info.major_version,
        version_info.minor_version,
        version_info.build_number,
    ))
}

fn main_window(app: &AppHandle) -> Result<tauri::WebviewWindow, String> {
    app.get_webview_window(MAIN_WINDOW_LABEL)
        .ok_or_else(|| String::from("Failed to locate the OFPlayer main window."))
}

fn capture_immersive_window_mode_snapshot(
    window: &tauri::WebviewWindow,
) -> Result<ImmersiveWindowModeSnapshot, String> {
    Ok(ImmersiveWindowModeSnapshot {
        fullscreen: window
            .is_fullscreen()
            .map_err(|error| format!("Failed to read fullscreen state: {error}"))?,
        maximized: window
            .is_maximized()
            .map_err(|error| format!("Failed to read maximized state: {error}"))?,
        always_on_top: window
            .is_always_on_top()
            .map_err(|error| format!("Failed to read always-on-top state: {error}"))?,
    })
}

fn fit_window_to_current_monitor(window: &tauri::WebviewWindow) -> Result<(), String> {
    let Some(monitor) = window
        .current_monitor()
        .map_err(|error| format!("Failed to resolve current monitor: {error}"))?
    else {
        return Ok(());
    };

    if window.is_maximized().unwrap_or(false) {
        window.unmaximize().map_err(|error| {
            format!("Failed to unmaximize before immersive fullscreen: {error}")
        })?;
    }

    window
        .set_position(tauri::Position::Physical(*monitor.position()))
        .map_err(|error| format!("Failed to move window to monitor bounds: {error}"))?;
    window
        .set_size(tauri::Size::Physical(*monitor.size()))
        .map_err(|error| format!("Failed to size window to monitor bounds: {error}"))?;

    Ok(())
}

#[tauri::command]
fn immersive_window_apply_mode(
    app: AppHandle,
    request: ImmersiveWindowModeRequest,
) -> Result<ImmersiveWindowModeSnapshot, String> {
    let window = main_window(&app)?;

    if request.hide_taskbar {
        window
            .set_always_on_top(true)
            .map_err(|error| format!("Failed to raise immersive window: {error}"))?;
        fit_window_to_current_monitor(&window)?;
        window
            .set_fullscreen(true)
            .map_err(|error| format!("Failed to enter immersive fullscreen: {error}"))?;
        window
            .set_focus()
            .map_err(|error| format!("Failed to focus immersive window: {error}"))?;

        if !window.is_fullscreen().unwrap_or(false) {
            window
                .set_fullscreen(true)
                .map_err(|error| format!("Failed to confirm immersive fullscreen: {error}"))?;
            let _ = window.set_focus();
        }
    } else {
        window
            .set_fullscreen(false)
            .map_err(|error| format!("Failed to leave immersive fullscreen: {error}"))?;
        window
            .set_always_on_top(false)
            .map_err(|error| format!("Failed to lower immersive window: {error}"))?;
    }

    capture_immersive_window_mode_snapshot(&window)
}

fn prepare_import_track_values_with_progress<F>(
    library_id: &str,
    files: Vec<LibraryImportFileInput>,
    on_progress: F,
) -> Result<(Vec<Value>, PrepareTrackImportsDiagnostics), String>
where
    F: FnMut(PrepareTrackImportsProgress),
{
    if files.is_empty() {
        return Ok((Vec::new(), PrepareTrackImportsDiagnostics::default()));
    }

    let prepared_report = storage::prepare_track_imports_with_progress(
        PrepareTrackImportsRequest {
            library_id: String::from(library_id),
            files: files
                .into_iter()
                .map(|file| PrepareTrackImportInput {
                    source_path: file.source_path,
                    file_name: file.file_name,
                    original_path: file.original_path,
                })
                .collect(),
        },
        on_progress,
    )?;

    let prepared_tracks = prepared_report
        .tracks
        .into_iter()
        .map(|track| {
            serde_json::to_value(track).map_err(|error| {
                format!("Failed to serialize prepared desktop track import: {error}")
            })
        })
        .collect::<Result<Vec<_>, _>>()?;

    Ok((prepared_tracks, prepared_report.diagnostics))
}

fn import_prepared_tracks(
    desktop_state: &DesktopStateStore,
    library_id: &str,
    prepared_tracks: Vec<Value>,
) -> Result<Vec<Value>, String> {
    if prepared_tracks.is_empty() {
        return Ok(Vec::new());
    }

    let import_request = LibraryPreparedTrackImportRequest {
        library_id: String::from(library_id),
        tracks: prepared_tracks,
    };

    desktop_state.import_library_prepared_tracks(&import_request)
}

fn build_library_import_result(
    job: LibraryImportJobSnapshot,
    imported_tracks: Vec<Value>,
    invalidation: LocalIndexInvalidationResult,
    discovered_total: usize,
    candidate_total: usize,
    diagnostics: LibraryImportDiagnostics,
    playback_result: PlaybackCommandResult,
) -> LibraryImportCommandResult {
    LibraryImportCommandResult {
        job,
        imported_tracks,
        invalidated_track_ids: invalidation.invalidated_track_ids,
        invalidated_relation_ids: invalidation.invalidated_relation_ids,
        reordered_tracks: invalidation.reordered_tracks,
        discovered_total,
        candidate_total,
        diagnostics,
        session: playback_result.session,
        playback: playback_result.playback,
        history_entries: playback_result.history_entries,
    }
}

fn play_or_load_current_session_track(
    desktop_state: &DesktopStateStore,
    playback: &mut PlaybackManager,
    session: &SessionStateSnapshot,
) -> Result<PlaybackSnapshot, String> {
    let selected_track_id = session
        .current_track_id
        .as_deref()
        .ok_or_else(|| String::from("No track is available in the current playback queue."))?;
    let current_snapshot = playback.snapshot();

    if current_snapshot.active_track_id.as_deref() == Some(selected_track_id) {
        playback.play()
    } else {
        load_session_track_into_playback(
            desktop_state,
            playback,
            session,
            true,
            resumable_session_start_time(session),
        )
    }
}

#[tauri::command]
fn playback_snapshot(
    playback: State<'_, Mutex<PlaybackManager>>,
) -> Result<PlaybackSnapshot, String> {
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;

    Ok(playback.snapshot())
}

#[tauri::command]
fn playback_load_track(
    app: AppHandle,
    playback: State<'_, Mutex<PlaybackManager>>,
    request: LoadTrackRequest,
) -> Result<PlaybackSnapshot, String> {
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;

    let request = sanitize_load_track_request(request);
    let snapshot = playback.load_track(request)?;
    emit_capsule_state_async(&app, &snapshot, "playback-load-track");
    Ok(snapshot)
}

#[tauri::command]
fn playback_play(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
) -> Result<PlaybackSnapshot, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;

    let snapshot = playback.play()?;
    desktop_state.update_session_playback_state(&snapshot)?;
    emit_capsule_progress_anchor_async(&app, &snapshot, "playback-play");
    Ok(snapshot)
}

#[tauri::command]
fn playback_pause(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
) -> Result<PlaybackSnapshot, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;

    let snapshot = playback.pause();
    desktop_state.update_session_playback_state(&snapshot)?;
    emit_capsule_progress_anchor_async(&app, &snapshot, "playback-pause");
    Ok(snapshot)
}

#[tauri::command]
fn playback_seek(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
    request: SeekRequest,
) -> Result<PlaybackSnapshot, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;

    let snapshot = playback.seek(request)?;
    desktop_state.update_session_playback_state(&snapshot)?;
    emit_capsule_progress_anchor_async(&app, &snapshot, "playback-seek");
    emit_capsule_state_async(&app, &snapshot, "playback-seek-state");
    Ok(snapshot)
}

#[tauri::command]
fn playback_set_volume(
    playback: State<'_, Mutex<PlaybackManager>>,
    request: VolumeRequest,
) -> Result<PlaybackSnapshot, String> {
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;

    Ok(playback.set_volume(request))
}

#[tauri::command]
fn playback_reset(
    app: AppHandle,
    playback: State<'_, Mutex<PlaybackManager>>,
) -> Result<PlaybackSnapshot, String> {
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;

    let snapshot = playback.reset();
    emit_capsule_state_async(&app, &snapshot, "playback-reset");
    Ok(snapshot)
}

#[tauri::command]
fn playback_recover_output(
    app: AppHandle,
    playback: State<'_, Mutex<PlaybackManager>>,
) -> Result<PlaybackSnapshot, String> {
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;

    let snapshot = playback.recover_output()?;
    emit_capsule_progress_anchor_async(&app, &snapshot, "playback-recover-output");
    Ok(snapshot)
}

#[tauri::command]
fn capsule_get_boot_state(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
    capsule_state: State<'_, Mutex<CapsuleStateStore>>,
    artwork_cache: State<'_, Mutex<CapsuleArtworkCache>>,
) -> Result<CapsuleBootState, String> {
    let playback_snapshot = {
        let mut playback = playback
            .lock()
            .map_err(|_| String::from("Rust playback state lock was poisoned."))?;

        playback.snapshot()
    };
    let track = match playback_snapshot.active_track_id.as_deref() {
        Some(track_id) => {
            let desktop_state = desktop_state
                .lock()
                .map_err(|_| String::from("Desktop state lock was poisoned."))?;
            desktop_state.get_track(track_id, true).ok().flatten()
        }
        None => None,
    };
    let mut artwork_cache = artwork_cache
        .lock()
        .map_err(|_| String::from("Capsule artwork cache lock was poisoned."))?;
    let mut capsule_state = capsule_state
        .lock()
        .map_err(|_| String::from("Capsule state lock was poisoned."))?;

    capsule_state.mark_ready();
    Ok(build_capsule_boot_state(
        &app,
        &playback_snapshot,
        track,
        &mut capsule_state,
        &mut artwork_cache,
    ))
}

fn ensure_main_window(app: &mut tauri::App) -> Result<(), String> {
    if app.get_webview_window(MAIN_WINDOW_LABEL).is_some() {
        return Ok(());
    }

    let window_config = app
        .config()
        .app
        .windows
        .iter()
        .find(|window| window.label == MAIN_WINDOW_LABEL)
        .ok_or_else(|| String::from("Failed to locate the main window configuration."))?;
    let data_dir = app_paths::webview_data_dir(MAIN_WINDOW_LABEL)?;

    tauri::WebviewWindowBuilder::from_config(app.handle(), window_config)
        .map_err(|error| format!("Failed to prepare the OFPlayer main window: {error}"))?
        .data_directory(data_dir)
        .build()
        .map_err(|error| format!("Failed to create the OFPlayer main window: {error}"))?;

    Ok(())
}

fn create_lyric_capsule_window(app: &AppHandle) -> Result<(), String> {
    if app.get_webview_window(CAPSULE_LABEL).is_some() {
        return Ok(());
    }

    let data_dir = app_paths::webview_data_dir(CAPSULE_LABEL)?;

    tauri::WebviewWindowBuilder::new(
        app,
        CAPSULE_LABEL,
        tauri::WebviewUrl::App(LYRIC_CAPSULE_ROUTE.into()),
    )
    .title("OFPlayer Lyric Capsule")
    .inner_size(LYRIC_CAPSULE_WINDOW_WIDTH, LYRIC_CAPSULE_WINDOW_HEIGHT)
    .min_inner_size(LYRIC_CAPSULE_WINDOW_WIDTH, LYRIC_CAPSULE_WINDOW_HEIGHT)
    .max_inner_size(LYRIC_CAPSULE_WINDOW_WIDTH, LYRIC_CAPSULE_WINDOW_HEIGHT)
    .decorations(false)
    .transparent(true)
    .always_on_top(true)
    .skip_taskbar(true)
    .resizable(false)
    .focused(false)
    .focusable(false)
    .visible(false)
    .shadow(false)
    .data_directory(data_dir)
    .build()
    .map_err(|error| format!("Failed to create the lyric capsule window: {error}"))?;

    Ok(())
}

#[tauri::command]
fn capsule_apply_hit_region(
    app: AppHandle,
    request: CapsuleHitRegionRequest,
) -> Result<(), String> {
    apply_capsule_hit_region(&app, request)
}

#[tauri::command]
async fn capsule_create_window(app: AppHandle) -> Result<bool, String> {
    create_lyric_capsule_window(&app)?;
    Ok(true)
}

#[tauri::command]
fn capsule_release(
    app: AppHandle,
    capsule_state: State<'_, Mutex<CapsuleStateStore>>,
) -> Result<(), String> {
    let _ = clear_capsule_hit_region(&app);

    let summary = {
        let mut capsule_state = capsule_state
            .lock()
            .map_err(|_| String::from("Capsule state lock was poisoned."))?;

        capsule_state.mark_closed();
        capsule_state.drain_diagnostics_summary()
    };

    if let Some(payload) = summary {
        append_diagnostics_event(
            &app,
            DiagnosticsLogEventRequest {
                level: Some(String::from("info")),
                category: String::from("lyric-capsule"),
                event: String::from("capsule_diagnostics_summary"),
                label: Some(String::from("[OFPlayer lyric capsule]")),
                payload: Some(payload),
            },
        );
    }

    Ok(())
}

#[tauri::command]
fn playback_list_output_devices(
    playback: State<'_, Mutex<PlaybackManager>>,
) -> Result<PlaybackOutputDevicesSnapshot, String> {
    let playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;

    playback.output_devices()
}

#[tauri::command]
fn playback_set_output_device(
    playback: State<'_, Mutex<PlaybackManager>>,
    request: OutputDevicePreferenceRequest,
) -> Result<PlaybackOutputDeviceChangeResult, String> {
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;

    playback.set_output_device_preference(request)
}

#[tauri::command]
fn playback_session_sync_catalog(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
) -> Result<PlaybackCommandResult, String> {
    let total_started_at = Instant::now();
    let process_start = capture_process_resource_snapshot();
    let mut step_profiles = Vec::new();

    let desktop_lock_started_at = Instant::now();
    let desktop_lock_resource_start = capture_process_resource_snapshot();
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;
    push_diagnostic_step_profile(
        &mut step_profiles,
        "desktopStateLock",
        desktop_lock_started_at,
        desktop_lock_resource_start,
    );

    let playback_lock_started_at = Instant::now();
    let playback_lock_resource_start = capture_process_resource_snapshot();
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;
    push_diagnostic_step_profile(
        &mut step_profiles,
        "playbackLock",
        playback_lock_started_at,
        playback_lock_resource_start,
    );

    let sync_started_at = Instant::now();
    let sync_resource_start = capture_process_resource_snapshot();
    let result = sync_catalog_playback_state(&desktop_state, &mut playback)?;
    push_diagnostic_step_profile(
        &mut step_profiles,
        "syncCatalogPlayback",
        sync_started_at,
        sync_resource_start,
    );

    let capsule_emit_started_at = Instant::now();
    emit_capsule_state_async(&app, &result.playback, "playback-session-sync-catalog");
    push_diagnostic_step_profile(
        &mut step_profiles,
        "capsuleEmitSchedule",
        capsule_emit_started_at,
        None,
    );
    let process_end = capture_process_resource_snapshot();

    append_diagnostics_event(
        &app,
        DiagnosticsLogEventRequest {
            level: Some(String::from("info")),
            category: String::from("playback"),
            event: String::from("playback_session_sync_catalog_profile"),
            label: Some(String::from("[OFPlayer playback sync catalog]")),
            payload: Some(json!({
                "activeTrackId": result.playback.active_track_id.clone(),
                "playbackStatus": result.playback.status,
                "sessionQueueTrackCount": result.session.queue_track_ids.len(),
                "totalMs": elapsed_ms(total_started_at),
                "process": build_process_resource_diagnostics(process_start.as_ref(), process_end.as_ref()),
                "stepProfiles": step_profiles,
            })),
        },
    );
    Ok(result)
}

#[tauri::command]
fn playback_session_set_queue(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: SessionQueueRequest,
) -> Result<SessionStateSnapshot, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.set_session_queue(&request.track_ids)
}

#[tauri::command]
fn playback_session_select_track(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
    request: SessionSelectTrackRequest,
) -> Result<PlaybackCommandResult, String> {
    let total_started_at = Instant::now();
    let process_start = capture_process_resource_snapshot();
    let mut step_profiles = Vec::new();
    let requested_track_id = request.track_id.clone();
    let requested_queue_track_count = request.queue_track_ids.as_ref().map(Vec::len).unwrap_or(0);
    let autoplay = request.autoplay.unwrap_or(true);

    let desktop_lock_started_at = Instant::now();
    let desktop_lock_resource_start = capture_process_resource_snapshot();
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;
    push_diagnostic_step_profile(
        &mut step_profiles,
        "desktopStateLock",
        desktop_lock_started_at,
        desktop_lock_resource_start,
    );

    let playback_lock_started_at = Instant::now();
    let playback_lock_resource_start = capture_process_resource_snapshot();
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;
    push_diagnostic_step_profile(
        &mut step_profiles,
        "playbackLock",
        playback_lock_started_at,
        playback_lock_resource_start,
    );

    let session_select_started_at = Instant::now();
    let session_select_resource_start = capture_process_resource_snapshot();
    let session = desktop_state
        .select_session_track(&request.track_id, request.queue_track_ids.as_deref())?;
    push_diagnostic_step_profile(
        &mut step_profiles,
        "sessionSelect",
        session_select_started_at,
        session_select_resource_start,
    );

    let load_track_started_at = Instant::now();
    let load_track_resource_start = capture_process_resource_snapshot();
    let playback_snapshot = load_session_track_into_playback_with_source(
        &desktop_state,
        &mut playback,
        &session,
        autoplay,
        0.0,
        request.playback_source.as_ref(),
    )?;
    push_diagnostic_step_profile(
        &mut step_profiles,
        "loadTrackIntoPlayback",
        load_track_started_at,
        load_track_resource_start,
    );

    let session_update_started_at = Instant::now();
    let session_update_resource_start = capture_process_resource_snapshot();
    let session = desktop_state.update_session_playback_state(&playback_snapshot)?;
    push_diagnostic_step_profile(
        &mut step_profiles,
        "sessionPlaybackStateUpdate",
        session_update_started_at,
        session_update_resource_start,
    );

    let history_build_started_at = Instant::now();
    let history_entries = if autoplay {
        build_history_entry(
            PLAYBACK_HISTORY_TYPE_PLAYED,
            &playback_snapshot,
            session.current_track_id.as_deref(),
        )
        .into_iter()
        .collect::<Vec<_>>()
    } else {
        Vec::new()
    };
    push_diagnostic_step_profile(
        &mut step_profiles,
        "historyBuild",
        history_build_started_at,
        None,
    );

    let history_append_started_at = Instant::now();
    let history_append_resource_start = capture_process_resource_snapshot();
    desktop_state.append_history_entries(&history_entries)?;
    push_diagnostic_step_profile(
        &mut step_profiles,
        "historyAppend",
        history_append_started_at,
        history_append_resource_start,
    );

    let capsule_emit_started_at = Instant::now();
    emit_capsule_state_async(&app, &playback_snapshot, "playback-session-select-track");
    push_diagnostic_step_profile(
        &mut step_profiles,
        "capsuleEmitSchedule",
        capsule_emit_started_at,
        None,
    );
    let process_end = capture_process_resource_snapshot();

    append_diagnostics_event(
        &app,
        DiagnosticsLogEventRequest {
            level: Some(String::from("info")),
            category: String::from("playback"),
            event: String::from("playback_session_select_track_profile"),
            label: Some(String::from("[OFPlayer playback select track]")),
            payload: Some(json!({
                "trackId": requested_track_id,
                "activeTrackId": playback_snapshot.active_track_id,
                "autoplay": autoplay,
                "requestedQueueTrackCount": requested_queue_track_count,
                "sessionQueueTrackCount": session.queue_track_ids.len(),
                "historyEntryCount": history_entries.len(),
                "playbackStatus": playback_snapshot.status,
                "duration": playback_snapshot.duration,
                "totalMs": elapsed_ms(total_started_at),
                "process": build_process_resource_diagnostics(process_start.as_ref(), process_end.as_ref()),
                "stepProfiles": step_profiles,
            })),
        },
    );
    Ok(command_result(session, playback_snapshot, history_entries))
}

#[tauri::command]
fn playback_session_play_current(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
) -> Result<PlaybackCommandResult, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;
    let mut session = desktop_state.load_session_snapshot()?;

    if session.current_track_id.is_none() {
        session = desktop_state.sync_session_with_catalog()?;
    }

    let playback_snapshot =
        play_or_load_current_session_track(&desktop_state, &mut playback, &session)?;
    let session = desktop_state.update_session_playback_state(&playback_snapshot)?;
    let history_entries = build_history_entry(
        PLAYBACK_HISTORY_TYPE_PLAYED,
        &playback_snapshot,
        session.current_track_id.as_deref(),
    )
    .into_iter()
    .collect::<Vec<_>>();

    desktop_state.append_history_entries(&history_entries)?;
    emit_capsule_state_async(&app, &playback_snapshot, "playback-session-play-current");
    Ok(command_result(session, playback_snapshot, history_entries))
}

#[tauri::command]
fn playback_session_pause(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
) -> Result<PlaybackCommandResult, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;
    let playback_snapshot = playback.pause();
    let session = desktop_state.update_session_playback_state(&playback_snapshot)?;
    let history_entries =
        build_history_entry(PLAYBACK_HISTORY_TYPE_PAUSED, &playback_snapshot, None)
            .into_iter()
            .collect::<Vec<_>>();

    desktop_state.append_history_entries(&history_entries)?;
    emit_capsule_progress_anchor_async(&app, &playback_snapshot, "playback-session-pause");
    Ok(command_result(session, playback_snapshot, history_entries))
}

#[tauri::command]
fn playback_session_next(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
) -> Result<PlaybackCommandResult, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;
    let session = desktop_state
        .advance_session_to_next_track()?
        .ok_or_else(|| String::from("No track is available in the current playback queue."))?;
    let playback_snapshot =
        load_session_track_into_playback(&desktop_state, &mut playback, &session, true, 0.0)?;
    let session = desktop_state.update_session_playback_state(&playback_snapshot)?;
    let history_entries = build_history_entry(
        PLAYBACK_HISTORY_TYPE_PLAYED,
        &playback_snapshot,
        session.current_track_id.as_deref(),
    )
    .into_iter()
    .collect::<Vec<_>>();

    desktop_state.append_history_entries(&history_entries)?;
    emit_capsule_state_async(&app, &playback_snapshot, "playback-session-next");
    Ok(command_result(session, playback_snapshot, history_entries))
}

#[tauri::command]
fn playback_session_previous(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
    request: SessionPreviousRequest,
) -> Result<PlaybackCommandResult, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;
    let restart_threshold_seconds = request.restart_threshold_seconds.unwrap_or(3.0).max(0.0);
    let current_snapshot = playback.snapshot();

    if current_snapshot.current_time > restart_threshold_seconds
        && current_snapshot.active_track_id.is_some()
    {
        let playback_snapshot = playback.seek(SeekRequest { seconds: 0.0 })?;
        let session = desktop_state.update_session_playback_state(&playback_snapshot)?;
        emit_capsule_progress_anchor_async(
            &app,
            &playback_snapshot,
            "playback-session-previous-seek",
        );
        emit_capsule_state_async(
            &app,
            &playback_snapshot,
            "playback-session-previous-seek-state",
        );
        return Ok(command_result(session, playback_snapshot, Vec::new()));
    }

    let session = desktop_state
        .advance_session_to_previous_track()?
        .ok_or_else(|| String::from("No track is available in the current playback queue."))?;
    let playback_snapshot =
        load_session_track_into_playback(&desktop_state, &mut playback, &session, true, 0.0)?;
    let session = desktop_state.update_session_playback_state(&playback_snapshot)?;
    let history_entries = build_history_entry(
        PLAYBACK_HISTORY_TYPE_PLAYED,
        &playback_snapshot,
        session.current_track_id.as_deref(),
    )
    .into_iter()
    .collect::<Vec<_>>();

    desktop_state.append_history_entries(&history_entries)?;
    emit_capsule_state_async(&app, &playback_snapshot, "playback-session-previous");
    Ok(command_result(session, playback_snapshot, history_entries))
}

#[tauri::command]
fn playback_session_handle_ended(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
) -> Result<PlaybackCommandResult, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;
    let mut playback = playback
        .lock()
        .map_err(|_| String::from("Rust playback state lock was poisoned."))?;
    let mut session = desktop_state.load_session_snapshot()?;
    let ended_snapshot = playback.snapshot();
    let mut history_entries = build_history_entry(
        PLAYBACK_HISTORY_TYPE_ENDED,
        &ended_snapshot,
        ended_snapshot
            .ended_track_id
            .as_deref()
            .or(session.current_track_id.as_deref()),
    )
    .into_iter()
    .collect::<Vec<_>>();
    let playback_snapshot = if session.queue_track_ids.len() > 1 {
        session = desktop_state
            .advance_session_to_next_track()?
            .ok_or_else(|| String::from("No track is available in the current playback queue."))?;
        let next_snapshot =
            load_session_track_into_playback(&desktop_state, &mut playback, &session, true, 0.0)?;

        if let Some(entry) = build_history_entry(
            PLAYBACK_HISTORY_TYPE_PLAYED,
            &next_snapshot,
            session.current_track_id.as_deref(),
        ) {
            history_entries.push(entry);
        }

        next_snapshot
    } else {
        ended_snapshot
    };
    let session = desktop_state.update_session_playback_state(&playback_snapshot)?;

    desktop_state.append_history_entries(&history_entries)?;
    emit_capsule_state_async(&app, &playback_snapshot, "playback-session-handle-ended");
    Ok(command_result(session, playback_snapshot, history_entries))
}

#[tauri::command]
fn desktop_state_load_preferences(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
) -> Result<Option<serde_json::Value>, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.load_preferences()
}

#[tauri::command]
fn desktop_state_save_preferences(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    value: serde_json::Value,
) -> Result<bool, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.save_preferences(&value)?;
    Ok(true)
}

#[tauri::command]
fn desktop_state_load_session(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
) -> Result<Option<serde_json::Value>, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.load_session()
}

#[tauri::command]
fn desktop_state_save_session(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    value: serde_json::Value,
) -> Result<bool, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.save_session(&value)?;
    Ok(true)
}

#[tauri::command]
fn desktop_external_library_load_connections(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
) -> Result<Vec<serde_json::Value>, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.load_external_library_connections()
}

#[tauri::command]
fn desktop_external_library_put_connection(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    value: serde_json::Value,
) -> Result<serde_json::Value, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.put_external_library_connection(&value)
}

#[tauri::command]
fn desktop_external_library_delete_connection(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    connection_id: String,
) -> Result<bool, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.delete_external_library_connection(&connection_id)
}

#[tauri::command]
fn desktop_state_load_bootstrap(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: DesktopBootstrapRequest,
) -> Result<DesktopBootstrapSnapshot, String> {
    let mut desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.load_bootstrap_snapshot(&request)
}

#[tauri::command]
fn desktop_state_reset_all_data(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    import_jobs: State<'_, Mutex<LibraryImportJobStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
) -> Result<DesktopStateResetCommandResult, String> {
    let reset = {
        let mut desktop_state = desktop_state
            .lock()
            .map_err(|_| String::from("Desktop state lock was poisoned."))?;

        desktop_state.reset_all_data()?
    };

    {
        let mut import_jobs = import_jobs
            .lock()
            .map_err(|_| String::from("Library import job lock was poisoned."))?;

        import_jobs.clear();
    }

    let playback = {
        let mut playback = playback
            .lock()
            .map_err(|_| String::from("Rust playback state lock was poisoned."))?;

        playback.reset()
    };

    Ok(DesktopStateResetCommandResult { reset, playback })
}

#[tauri::command]
fn desktop_storage_analyze(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
) -> Result<StorageUsageSnapshot, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.analyze_storage_usage()
}

#[tauri::command]
fn desktop_storage_collect_garbage(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
) -> Result<StorageGarbageCollectionResult, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.collect_storage_garbage()
}

#[tauri::command]
fn desktop_catalog_load_snapshot(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: Option<CatalogLoadSnapshotRequest>,
) -> Result<CatalogSnapshot, String> {
    let mut desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.load_catalog_snapshot(
        request
            .as_ref()
            .and_then(|request| request.track_artwork_mode.as_deref()),
    )
}

#[tauri::command]
fn desktop_catalog_put_libraries(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: UpsertRecordsRequest,
) -> Result<bool, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.put_libraries(&request.records)?;
    Ok(true)
}

#[tauri::command]
fn desktop_catalog_delete_libraries(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: DeleteRecordsRequest,
) -> Result<bool, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.delete_libraries(&request.ids)?;
    Ok(true)
}

#[tauri::command]
fn desktop_catalog_put_playlists(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: UpsertRecordsRequest,
) -> Result<bool, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.put_playlists(&request.records)?;
    Ok(true)
}

#[tauri::command]
fn desktop_catalog_delete_playlists(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: DeleteRecordsRequest,
) -> Result<bool, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.delete_playlists(&request.ids)?;
    Ok(true)
}

#[tauri::command]
fn desktop_catalog_put_playlist_track_relations(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: UpsertRecordsRequest,
) -> Result<bool, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.put_playlist_track_relations(&request.records)?;
    Ok(true)
}

#[tauri::command]
fn desktop_catalog_delete_playlist_track_relations(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: DeleteRecordsRequest,
) -> Result<bool, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.delete_playlist_track_relations(&request.ids)?;
    Ok(true)
}

#[tauri::command]
fn desktop_catalog_get_track(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: TrackLookupRequest,
    app: AppHandle,
) -> Result<Option<serde_json::Value>, String> {
    let total_started_at = Instant::now();
    let process_start = capture_process_resource_snapshot();
    let mut step_profiles = Vec::new();
    let include_artwork = request.include_artwork.unwrap_or(true);

    let lock_started_at = Instant::now();
    let lock_resource_start = capture_process_resource_snapshot();
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;
    push_diagnostic_step_profile(
        &mut step_profiles,
        "desktopStateLock",
        lock_started_at,
        lock_resource_start,
    );

    let get_track_started_at = Instant::now();
    let get_track_resource_start = capture_process_resource_snapshot();
    let track = desktop_state.get_track(&request.track_id, include_artwork)?;
    push_diagnostic_step_profile(
        &mut step_profiles,
        "getTrack",
        get_track_started_at,
        get_track_resource_start,
    );

    let result = track;
    let process_end = capture_process_resource_snapshot();
    let payload_bytes = if include_artwork {
        0
    } else {
        result
            .as_ref()
            .map(|value| value.to_string().len())
            .unwrap_or_default()
    };
    let artwork_bytes = result
        .as_ref()
        .and_then(|value| value.get("artwork"))
        .and_then(Value::as_str)
        .map(str::len)
        .unwrap_or_default();

    append_diagnostics_event(
        &app,
        DiagnosticsLogEventRequest {
            level: Some(String::from("info")),
            category: String::from("catalog"),
            event: String::from("desktop_catalog_get_track_profile"),
            label: Some(String::from("[OFPlayer catalog get track]")),
            payload: Some(json!({
                "trackId": request.track_id,
                "includeArtwork": include_artwork,
                "payloadBytes": payload_bytes,
                "artworkBytes": artwork_bytes,
                "totalMs": elapsed_ms(total_started_at),
                "process": build_process_resource_diagnostics(process_start.as_ref(), process_end.as_ref()),
                "stepProfiles": step_profiles,
            })),
        },
    );

    Ok(result)
}

#[tauri::command]
fn desktop_catalog_put_tracks(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: UpsertRecordsRequest,
) -> Result<bool, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.put_tracks(&request.records)?;
    Ok(true)
}

#[tauri::command]
fn desktop_catalog_update_track(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: TrackUpdateRequest,
) -> Result<Option<serde_json::Value>, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.update_track(&request.track_id, &request.patch)
}

#[tauri::command]
fn desktop_catalog_delete_tracks(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: DeleteRecordsRequest,
) -> Result<bool, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.delete_tracks(&request.ids)?;
    Ok(true)
}

#[tauri::command]
fn desktop_history_load_recent(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: HistoryLoadRequest,
) -> Result<Vec<serde_json::Value>, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.load_recent_history(request.limit)
}

#[tauri::command]
fn desktop_history_append(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    value: serde_json::Value,
) -> Result<bool, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.append_history_entry(&value)?;
    Ok(true)
}

#[tauri::command]
fn desktop_catalog_resolve_navigation(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: NavigationQueryRequest,
) -> Result<NavigationQueryResult, String> {
    let command_lock_started_at = Instant::now();
    let mut desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;
    let command_lock_wait_ms = elapsed_ms(command_lock_started_at);

    let mut result = desktop_state.resolve_navigation_summary(&request)?;
    result.diagnostics.command_lock_wait_ms = command_lock_wait_ms;
    Ok(result)
}

#[tauri::command]
fn desktop_catalog_query_collection_tracks(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: CollectionTrackQueryRequest,
) -> Result<QueryTracksResult, String> {
    let command_lock_started_at = Instant::now();
    let mut desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;
    let command_lock_wait_ms = elapsed_ms(command_lock_started_at);

    let mut result = desktop_state.query_collection_tracks(&request)?;
    result.diagnostics.command_lock_wait_ms = command_lock_wait_ms;
    Ok(result)
}

#[tauri::command]
fn desktop_library_create(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: LibraryCreateRequest,
) -> Result<LibraryCreateResult, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.create_library(&request)
}

#[tauri::command]
fn desktop_library_rename(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: LibraryRenameRequest,
) -> Result<serde_json::Value, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.rename_library(&request)
}

#[tauri::command]
fn desktop_library_delete(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
    request: LibraryDeleteRequest,
) -> Result<LibraryDeleteCommandResult, String> {
    let started_at = Instant::now();
    let requested_library_id = request.library_id.clone();
    let result = (|| -> Result<LibraryDeleteCommandResult, String> {
        let desktop_state = desktop_state
            .lock()
            .map_err(|_| String::from("Desktop state lock was poisoned."))?;
        let mut playback = playback
            .lock()
            .map_err(|_| String::from("Rust playback state lock was poisoned."))?;
        let mutation = desktop_state.delete_library(&request)?;
        let playback_result = sync_catalog_playback_state(&desktop_state, &mut playback)?;

        Ok(LibraryDeleteCommandResult {
            mutation,
            session: playback_result.session,
            playback: playback_result.playback,
        })
    })();

    match &result {
        Ok(result) => append_diagnostics_event(
            &app,
            DiagnosticsLogEventRequest {
                level: Some(String::from("info")),
                category: String::from("catalog"),
                event: String::from("desktop_library_delete"),
                label: Some(String::from("[OFPlayer library delete]")),
                payload: Some(json!({
                    "requestedLibraryId": requested_library_id.clone(),
                    "deletedLibraryId": result.mutation.deleted_library_id.clone(),
                    "deletedTrackCount": result.mutation.deleted_track_ids.len(),
                    "deletedPlaylistCount": result.mutation.deleted_playlist_ids.len(),
                    "sessionQueueTrackCount": result.session.queue_track_ids.len(),
                    "activeTrackId": result.playback.active_track_id.clone(),
                    "totalMs": elapsed_ms(started_at),
                })),
            },
        ),
        Err(error) => append_diagnostics_event(
            &app,
            DiagnosticsLogEventRequest {
                level: Some(String::from("error")),
                category: String::from("catalog"),
                event: String::from("desktop_library_delete_failed"),
                label: Some(String::from("[OFPlayer library delete]")),
                payload: Some(json!({
                    "requestedLibraryId": requested_library_id.clone(),
                    "error": error,
                    "totalMs": elapsed_ms(started_at),
                })),
            },
        ),
    }

    result
}

#[tauri::command]
fn desktop_library_reorder(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: LibraryReorderRequest,
) -> Result<Vec<serde_json::Value>, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.reorder_libraries(&request)
}

#[tauri::command]
fn desktop_playlist_create(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: PlaylistCreateRequest,
) -> Result<serde_json::Value, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.create_playlist(&request)
}

#[tauri::command]
fn desktop_playlist_rename(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: PlaylistRenameRequest,
) -> Result<serde_json::Value, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.rename_playlist(&request)
}

#[tauri::command]
fn desktop_playlist_delete(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: PlaylistDeleteRequest,
) -> Result<PlaylistDeleteResult, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.delete_playlist(&request)
}

#[tauri::command]
fn desktop_playlist_reorder(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: PlaylistReorderRequest,
) -> Result<Vec<serde_json::Value>, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.reorder_playlists(&request)
}

#[tauri::command]
fn desktop_playlist_add_track(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: PlaylistTrackMutationRequest,
) -> Result<PlaylistTrackMutationResult, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.add_track_to_playlist(&request)
}

#[tauri::command]
fn desktop_playlist_remove_track(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: PlaylistTrackRemoveRequest,
) -> Result<PlaylistTrackRemoveResult, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.remove_track_from_playlist(&request)
}

#[tauri::command]
fn desktop_playlist_reorder_tracks(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: PlaylistTrackReorderRequest,
) -> Result<Vec<serde_json::Value>, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.reorder_playlist_tracks(&request)
}

#[tauri::command]
fn desktop_track_delete_from_library(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
    request: TrackDeleteRequest,
) -> Result<TrackDeleteCommandResult, String> {
    let started_at = Instant::now();
    let requested_track_id = request.track_id.clone();
    let result = (|| -> Result<TrackDeleteCommandResult, String> {
        let desktop_state = desktop_state
            .lock()
            .map_err(|_| String::from("Desktop state lock was poisoned."))?;
        let mut playback = playback
            .lock()
            .map_err(|_| String::from("Rust playback state lock was poisoned."))?;
        let mutation = desktop_state.delete_track_from_library(&request)?;
        let playback_result = sync_catalog_playback_state(&desktop_state, &mut playback)?;

        Ok(TrackDeleteCommandResult {
            mutation,
            session: playback_result.session,
            playback: playback_result.playback,
        })
    })();

    match &result {
        Ok(result) => append_diagnostics_event(
            &app,
            DiagnosticsLogEventRequest {
                level: Some(String::from("info")),
                category: String::from("catalog"),
                event: String::from("desktop_track_delete_from_library"),
                label: Some(String::from("[OFPlayer track delete]")),
                payload: Some(json!({
                    "requestedTrackId": requested_track_id.clone(),
                    "deletedTrackId": result.mutation.deleted_track_id.clone(),
                    "deletedRelationCount": result.mutation.deleted_relation_ids.len(),
                    "reorderedTrackCount": result.mutation.reordered_tracks.len(),
                    "sessionQueueTrackCount": result.session.queue_track_ids.len(),
                    "activeTrackId": result.playback.active_track_id.clone(),
                    "totalMs": elapsed_ms(started_at),
                })),
            },
        ),
        Err(error) => append_diagnostics_event(
            &app,
            DiagnosticsLogEventRequest {
                level: Some(String::from("error")),
                category: String::from("catalog"),
                event: String::from("desktop_track_delete_from_library_failed"),
                label: Some(String::from("[OFPlayer track delete]")),
                payload: Some(json!({
                    "requestedTrackId": requested_track_id.clone(),
                    "error": error,
                    "totalMs": elapsed_ms(started_at),
                })),
            },
        ),
    }

    result
}

#[tauri::command]
fn desktop_tracks_delete_from_library(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
    request: TrackBatchDeleteRequest,
) -> Result<TrackBatchDeleteCommandResult, String> {
    let started_at = Instant::now();
    let requested_track_count = request.track_ids.len();
    let result = (|| -> Result<TrackBatchDeleteCommandResult, String> {
        let desktop_state = desktop_state
            .lock()
            .map_err(|_| String::from("Desktop state lock was poisoned."))?;
        let mut playback = playback
            .lock()
            .map_err(|_| String::from("Rust playback state lock was poisoned."))?;
        let mutation = desktop_state.delete_tracks_from_library(&request)?;
        let playback_result = sync_catalog_playback_state(&desktop_state, &mut playback)?;

        Ok(TrackBatchDeleteCommandResult {
            mutation,
            session: playback_result.session,
            playback: playback_result.playback,
        })
    })();

    match &result {
        Ok(result) => append_diagnostics_event(
            &app,
            DiagnosticsLogEventRequest {
                level: Some(String::from("info")),
                category: String::from("catalog"),
                event: String::from("desktop_tracks_delete_from_library"),
                label: Some(String::from("[OFPlayer track delete]")),
                payload: Some(json!({
                    "requestedTrackCount": requested_track_count,
                    "deletedTrackCount": result.mutation.deleted_track_ids.len(),
                    "deletedRelationCount": result.mutation.deleted_relation_ids.len(),
                    "libraryCount": result.mutation.library_ids.len(),
                    "reorderedTrackCount": result.mutation.reordered_tracks.len(),
                    "sessionQueueTrackCount": result.session.queue_track_ids.len(),
                    "activeTrackId": result.playback.active_track_id.clone(),
                    "totalMs": elapsed_ms(started_at),
                })),
            },
        ),
        Err(error) => append_diagnostics_event(
            &app,
            DiagnosticsLogEventRequest {
                level: Some(String::from("error")),
                category: String::from("catalog"),
                event: String::from("desktop_tracks_delete_from_library_failed"),
                label: Some(String::from("[OFPlayer track delete]")),
                payload: Some(json!({
                    "requestedTrackCount": requested_track_count,
                    "error": error,
                    "totalMs": elapsed_ms(started_at),
                })),
            },
        ),
    }

    result
}

#[tauri::command]
fn desktop_track_set_favorite(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: TrackFavoriteRequest,
) -> Result<serde_json::Value, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.set_track_favorite(&request)
}

#[tauri::command]
fn desktop_track_toggle_favorite(
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    request: TrackLookupRequest,
) -> Result<serde_json::Value, String> {
    let desktop_state = desktop_state
        .lock()
        .map_err(|_| String::from("Desktop state lock was poisoned."))?;

    desktop_state.toggle_track_favorite(&request.track_id)
}

#[tauri::command]
fn desktop_library_import_files(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    import_jobs: State<'_, Mutex<LibraryImportJobStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
    request: LibraryManagedImportRequest,
) -> Result<LibraryImportCommandResult, String> {
    let started_at = Instant::now();
    let process_start = capture_process_resource_snapshot();
    let mut step_profiles = vec![build_diagnostic_step_profile(
        "discover",
        0,
        process_start.as_ref(),
        process_start.as_ref(),
    )];
    let mut job = create_library_import_job_snapshot("indexed-import", &request.library_id);
    persist_library_import_job_snapshot(&import_jobs, &job)?;
    update_library_import_job_stage(
        &mut job,
        IMPORT_STAGE_DISCOVER,
        IMPORT_STAGE_STATUS_SKIPPED,
        request.files.len(),
        request.files.len(),
        Some(0),
    );
    persist_library_import_job_snapshot(&import_jobs, &job)?;
    let candidate_request = LibraryImportCandidatesRequest {
        library_id: request.library_id.clone(),
        files: request.files,
        respect_deleted_import_paths: Some(false),
    };
    update_library_import_job_stage(
        &mut job,
        IMPORT_STAGE_FILTER,
        IMPORT_STAGE_STATUS_RUNNING,
        0,
        candidate_request.files.len().max(1),
        None,
    );
    persist_library_import_job_snapshot(&import_jobs, &job)?;
    emit_library_import_job_progress(
        &app,
        &job,
        String::from("preparing"),
        12,
        0,
        candidate_request.files.len().max(1),
        0,
        candidate_request.files.len(),
        0,
        0,
        0,
        String::new(),
        elapsed_ms(started_at),
    );
    let filter_started_at = Instant::now();
    let filter_resource_start = capture_process_resource_snapshot();
    let candidates = {
        let desktop_state = desktop_state
            .lock()
            .map_err(|_| String::from("Desktop state lock was poisoned."))?;

        desktop_state.filter_library_import_candidates(&candidate_request)?
    };
    let filter_ms = elapsed_ms(filter_started_at);
    let filter_resource_end = capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        IMPORT_STAGE_FILTER,
        filter_ms,
        filter_resource_start.as_ref(),
        filter_resource_end.as_ref(),
    ));
    let candidate_total = candidates.len();
    job.discovered_total = candidate_request.files.len();
    job.candidate_total = candidate_total;
    update_library_import_job_stage(
        &mut job,
        IMPORT_STAGE_FILTER,
        IMPORT_STAGE_STATUS_COMPLETED,
        candidate_request.files.len(),
        candidate_request.files.len().max(1),
        Some(filter_ms),
    );
    persist_library_import_job_snapshot(&import_jobs, &job)?;
    update_library_import_job_stage(
        &mut job,
        IMPORT_STAGE_PREPARE,
        IMPORT_STAGE_STATUS_RUNNING,
        0,
        candidate_total.max(1),
        None,
    );
    persist_library_import_job_snapshot(&import_jobs, &job)?;
    emit_library_import_job_progress(
        &app,
        &job,
        String::from("processing"),
        22,
        0,
        candidate_total.max(1),
        0,
        candidate_request.files.len(),
        candidate_total,
        0,
        0,
        String::new(),
        elapsed_ms(started_at),
    );
    let prepare_started_at = Instant::now();
    let prepare_resource_start = capture_process_resource_snapshot();
    let (prepared_tracks, prepare_diagnostics) = prepare_import_track_values_with_progress(
        &request.library_id,
        candidates,
        |progress: PrepareTrackImportsProgress| {
            let current_file = progress.current_file;
            update_library_import_job_stage(
                &mut job,
                IMPORT_STAGE_PREPARE,
                IMPORT_STAGE_STATUS_RUNNING,
                progress.processed,
                progress.total.max(1),
                None,
            );
            job.current_file = current_file.clone();
            emit_library_import_job_progress(
                &app,
                &job,
                String::from("processing"),
                progress_percent(22, 88, progress.processed, progress.total.max(1)),
                progress.processed,
                progress.total.max(1),
                0,
                candidate_request.files.len(),
                candidate_total,
                0,
                0,
                current_file,
                elapsed_ms(started_at),
            );
        },
    )?;
    let prepared_total = prepared_tracks.len();
    let has_prepared_tracks = prepared_total > 0;
    let prepare_ms = elapsed_ms(prepare_started_at);
    let prepare_resource_end = capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        IMPORT_STAGE_PREPARE,
        prepare_ms,
        prepare_resource_start.as_ref(),
        prepare_resource_end.as_ref(),
    ));
    job.current_file.clear();
    update_library_import_job_stage(
        &mut job,
        IMPORT_STAGE_PREPARE,
        IMPORT_STAGE_STATUS_COMPLETED,
        prepared_total,
        candidate_total.max(1),
        Some(prepare_ms),
    );
    persist_library_import_job_snapshot(&import_jobs, &job)?;
    update_library_import_job_stage(
        &mut job,
        IMPORT_STAGE_PERSIST,
        IMPORT_STAGE_STATUS_RUNNING,
        0,
        prepared_total.max(1),
        None,
    );
    persist_library_import_job_snapshot(&import_jobs, &job)?;
    emit_library_import_job_progress(
        &app,
        &job,
        String::from("importing"),
        92,
        0,
        prepared_total.max(1),
        0,
        candidate_request.files.len(),
        candidate_total,
        0,
        0,
        String::new(),
        elapsed_ms(started_at),
    );
    let persist_started_at = Instant::now();
    let persist_resource_start = capture_process_resource_snapshot();
    let imported_tracks = if !has_prepared_tracks {
        Vec::new()
    } else {
        let desktop_state = desktop_state
            .lock()
            .map_err(|_| String::from("Desktop state lock was poisoned."))?;

        import_prepared_tracks(&desktop_state, &request.library_id, prepared_tracks)?
    };
    let persist_ms = elapsed_ms(persist_started_at);
    let persist_resource_end = capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        IMPORT_STAGE_PERSIST,
        persist_ms,
        persist_resource_start.as_ref(),
        persist_resource_end.as_ref(),
    ));
    job.imported_total = imported_tracks.len();
    update_library_import_job_stage(
        &mut job,
        IMPORT_STAGE_PERSIST,
        if has_prepared_tracks {
            IMPORT_STAGE_STATUS_COMPLETED
        } else {
            IMPORT_STAGE_STATUS_SKIPPED
        },
        imported_tracks.len(),
        prepared_total.max(1),
        Some(persist_ms),
    );
    persist_library_import_job_snapshot(&import_jobs, &job)?;
    update_library_import_job_stage(
        &mut job,
        IMPORT_STAGE_PLAYBACK_SYNC,
        IMPORT_STAGE_STATUS_RUNNING,
        0,
        imported_tracks.len().max(1),
        None,
    );
    persist_library_import_job_snapshot(&import_jobs, &job)?;
    emit_library_import_job_progress(
        &app,
        &job,
        String::from("importing"),
        if imported_tracks.is_empty() { 96 } else { 98 },
        imported_tracks.len(),
        imported_tracks.len().max(1),
        imported_tracks.len(),
        candidate_request.files.len(),
        candidate_total,
        0,
        0,
        String::new(),
        elapsed_ms(started_at),
    );
    let playback_sync_started_at = Instant::now();
    let playback_sync_resource_start = capture_process_resource_snapshot();
    let playback_result = {
        let desktop_state = desktop_state
            .lock()
            .map_err(|_| String::from("Desktop state lock was poisoned."))?;
        let mut playback = playback
            .lock()
            .map_err(|_| String::from("Rust playback state lock was poisoned."))?;

        sync_catalog_playback_state(&desktop_state, &mut playback)?
    };
    let playback_sync_ms = elapsed_ms(playback_sync_started_at);
    let playback_sync_resource_end = capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        IMPORT_STAGE_PLAYBACK_SYNC,
        playback_sync_ms,
        playback_sync_resource_start.as_ref(),
        playback_sync_resource_end.as_ref(),
    ));
    let process_end = capture_process_resource_snapshot();
    update_library_import_job_stage(
        &mut job,
        IMPORT_STAGE_PLAYBACK_SYNC,
        IMPORT_STAGE_STATUS_COMPLETED,
        imported_tracks.len(),
        imported_tracks.len().max(1),
        Some(playback_sync_ms),
    );
    let diagnostics = LibraryImportDiagnostics {
        total_ms: elapsed_ms(started_at),
        discover_ms: 0,
        filter_ms,
        prepare_ms,
        persist_ms,
        playback_sync_ms,
        copy_ms: prepare_diagnostics.copy_ms,
        metadata_ms: prepare_diagnostics.metadata_ms,
        metadata_fallback_count: prepare_diagnostics.metadata_fallback_count,
        directories_scanned: 0,
        entries_scanned: 0,
        discovered_total: candidate_request.files.len(),
        candidate_total,
        imported_total: imported_tracks.len(),
        process: build_process_resource_diagnostics(process_start.as_ref(), process_end.as_ref()),
        step_profiles,
    };
    finalize_library_import_job(
        &mut job,
        if candidate_total == 0 {
            IMPORT_JOB_STATUS_EMPTY
        } else {
            IMPORT_JOB_STATUS_COMPLETED
        },
        diagnostics.clone(),
        None,
    );
    persist_library_import_job_snapshot(&import_jobs, &job)?;

    let result = build_library_import_result(
        job,
        imported_tracks,
        LocalIndexInvalidationResult::default(),
        candidate_request.files.len(),
        candidate_total,
        diagnostics,
        playback_result,
    );
    emit_library_import_job_progress(
        &app,
        &result.job,
        if result.candidate_total == 0 {
            String::from("empty")
        } else {
            String::from("complete")
        },
        100,
        result.imported_tracks.len(),
        result.candidate_total.max(result.discovered_total).max(1),
        result.imported_tracks.len(),
        result.discovered_total,
        result.candidate_total,
        0,
        0,
        String::new(),
        result.diagnostics.total_ms,
    );

    Ok(result)
}

#[tauri::command]
fn desktop_library_scan_import(
    app: AppHandle,
    desktop_state: State<'_, Mutex<DesktopStateStore>>,
    import_jobs: State<'_, Mutex<LibraryImportJobStore>>,
    playback: State<'_, Mutex<PlaybackManager>>,
    request: LibraryScanImportRequest,
) -> Result<LibraryImportCommandResult, String> {
    let started_at = Instant::now();
    let process_start = capture_process_resource_snapshot();
    let mut step_profiles = Vec::new();
    let mut job = create_library_import_job_snapshot("scan-import", &request.library_id);
    persist_library_import_job_snapshot(&import_jobs, &job)?;

    let mut run = || -> Result<LibraryImportCommandResult, String> {
        update_library_import_job_stage(
            &mut job,
            IMPORT_STAGE_DISCOVER,
            IMPORT_STAGE_STATUS_RUNNING,
            0,
            request.directories.len().max(1),
            None,
        );
        persist_library_import_job_snapshot(&import_jobs, &job)?;
        emit_library_import_job_progress(
            &app,
            &job,
            String::from("discovering"),
            4,
            0,
            request.directories.len().max(1),
            0,
            0,
            0,
            0,
            0,
            String::new(),
            0,
        );

        let discover_started_at = Instant::now();
        let discover_resource_start = capture_process_resource_snapshot();
        let scan_report = storage::scan_audio_files_with_progress(
            ScanDirectoriesRequest {
                directories: request.directories.clone(),
            },
            |progress: ScanAudioFilesProgress| {
                let discover_total = progress
                    .directories_discovered
                    .max(request.directories.len())
                    .max(1);
                let discover_processed = progress.directories_processed.min(discover_total);
                let current_file = progress.current_path;
                update_library_import_job_stage(
                    &mut job,
                    IMPORT_STAGE_DISCOVER,
                    IMPORT_STAGE_STATUS_RUNNING,
                    discover_processed,
                    discover_total,
                    None,
                );
                job.discovered_total = progress.discovered_total;
                job.directories_scanned = progress.directories_processed;
                job.entries_scanned = progress.entries_scanned;
                job.current_file = current_file.clone();
                emit_library_import_job_progress(
                    &app,
                    &job,
                    String::from("discovering"),
                    progress_percent_from_ratio(
                        4,
                        32,
                        (progress.directories_processed as f64
                            + if progress.current_directory_entries_total > 0 {
                                progress.current_directory_entries_scanned as f64
                                    / progress.current_directory_entries_total as f64
                            } else {
                                0.0
                            })
                            / discover_total as f64,
                    ),
                    discover_processed,
                    discover_total,
                    0,
                    progress.discovered_total,
                    0,
                    progress.directories_processed,
                    progress.entries_scanned,
                    current_file,
                    elapsed_ms(started_at),
                );
            },
        )?;
        let discover_ms = elapsed_ms(discover_started_at);
        let discover_resource_end = capture_process_resource_snapshot();
        step_profiles.push(build_diagnostic_step_profile(
            IMPORT_STAGE_DISCOVER,
            discover_ms,
            discover_resource_start.as_ref(),
            discover_resource_end.as_ref(),
        ));
        let discovered_total = scan_report.files.len();
        let discovered_paths = scan_report
            .files
            .iter()
            .map(|file| file.path.clone())
            .collect::<Vec<_>>();
        job.discovered_total = discovered_total;
        job.directories_scanned = scan_report.diagnostics.directories_scanned;
        job.entries_scanned = scan_report.diagnostics.entries_scanned;
        job.current_file.clear();
        update_library_import_job_stage(
            &mut job,
            IMPORT_STAGE_DISCOVER,
            IMPORT_STAGE_STATUS_COMPLETED,
            scan_report
                .diagnostics
                .directories_scanned
                .max(request.directories.len()),
            scan_report
                .diagnostics
                .directories_scanned
                .max(request.directories.len())
                .max(1),
            Some(discover_ms),
        );
        persist_library_import_job_snapshot(&import_jobs, &job)?;

        update_library_import_job_stage(
            &mut job,
            IMPORT_STAGE_FILTER,
            IMPORT_STAGE_STATUS_RUNNING,
            0,
            discovered_total.max(1),
            None,
        );
        persist_library_import_job_snapshot(&import_jobs, &job)?;
        emit_library_import_job_progress(
            &app,
            &job,
            String::from("preparing"),
            36,
            discovered_total,
            discovered_total.max(1),
            0,
            discovered_total,
            0,
            scan_report.diagnostics.directories_scanned,
            scan_report.diagnostics.entries_scanned,
            String::new(),
            elapsed_ms(started_at),
        );

        let invalidation = {
            let desktop_state = desktop_state
                .lock()
                .map_err(|_| String::from("Desktop state lock was poisoned."))?;

            desktop_state.invalidate_missing_local_indexed_tracks(
                &LocalIndexInvalidationRequest {
                    library_id: request.library_id.clone(),
                    directories: request.directories.clone(),
                    discovered_paths,
                },
            )?
        };

        let candidate_request = LibraryImportCandidatesRequest {
            library_id: request.library_id.clone(),
            files: scan_report
                .files
                .into_iter()
                .map(|file| LibraryImportFileInput {
                    source_path: file.path,
                    file_name: Some(file.file_name),
                    original_path: None,
                })
                .collect(),
            respect_deleted_import_paths: Some(
                request.respect_deleted_import_paths.unwrap_or(true),
            ),
        };

        let filter_started_at = Instant::now();
        let filter_resource_start = capture_process_resource_snapshot();
        let candidates = {
            let desktop_state = desktop_state
                .lock()
                .map_err(|_| String::from("Desktop state lock was poisoned."))?;

            desktop_state.filter_library_import_candidates(&candidate_request)?
        };
        let filter_ms = elapsed_ms(filter_started_at);
        let filter_resource_end = capture_process_resource_snapshot();
        step_profiles.push(build_diagnostic_step_profile(
            IMPORT_STAGE_FILTER,
            filter_ms,
            filter_resource_start.as_ref(),
            filter_resource_end.as_ref(),
        ));
        let candidate_total = candidates.len();
        job.candidate_total = candidate_total;
        update_library_import_job_stage(
            &mut job,
            IMPORT_STAGE_FILTER,
            IMPORT_STAGE_STATUS_COMPLETED,
            discovered_total,
            discovered_total.max(1),
            Some(filter_ms),
        );
        persist_library_import_job_snapshot(&import_jobs, &job)?;

        let (prepared_tracks, prepare_diagnostics, prepare_ms) = if candidate_total == 0 {
            let prepare_resource_sample = capture_process_resource_snapshot();
            step_profiles.push(build_diagnostic_step_profile(
                IMPORT_STAGE_PREPARE,
                0,
                prepare_resource_sample.as_ref(),
                prepare_resource_sample.as_ref(),
            ));
            update_library_import_job_stage(
                &mut job,
                IMPORT_STAGE_PREPARE,
                IMPORT_STAGE_STATUS_SKIPPED,
                0,
                0,
                Some(0),
            );
            persist_library_import_job_snapshot(&import_jobs, &job)?;
            let persist_resource_sample = capture_process_resource_snapshot();
            step_profiles.push(build_diagnostic_step_profile(
                IMPORT_STAGE_PERSIST,
                0,
                persist_resource_sample.as_ref(),
                persist_resource_sample.as_ref(),
            ));
            update_library_import_job_stage(
                &mut job,
                IMPORT_STAGE_PERSIST,
                IMPORT_STAGE_STATUS_SKIPPED,
                0,
                0,
                Some(0),
            );
            persist_library_import_job_snapshot(&import_jobs, &job)?;
            (Vec::new(), PrepareTrackImportsDiagnostics::default(), 0)
        } else {
            update_library_import_job_stage(
                &mut job,
                IMPORT_STAGE_PREPARE,
                IMPORT_STAGE_STATUS_RUNNING,
                0,
                candidate_total,
                None,
            );
            persist_library_import_job_snapshot(&import_jobs, &job)?;
            emit_library_import_job_progress(
                &app,
                &job,
                String::from("processing"),
                42,
                0,
                candidate_total,
                0,
                discovered_total,
                candidate_total,
                scan_report.diagnostics.directories_scanned,
                scan_report.diagnostics.entries_scanned,
                String::new(),
                elapsed_ms(started_at),
            );

            let prepare_started_at = Instant::now();
            let prepare_resource_start = capture_process_resource_snapshot();
            let (prepared_tracks, prepare_diagnostics) = prepare_import_track_values_with_progress(
                &request.library_id,
                candidates,
                |progress: PrepareTrackImportsProgress| {
                    let current_file = progress.current_file;
                    update_library_import_job_stage(
                        &mut job,
                        IMPORT_STAGE_PREPARE,
                        IMPORT_STAGE_STATUS_RUNNING,
                        progress.processed,
                        progress.total.max(1),
                        None,
                    );
                    job.current_file = current_file.clone();
                    emit_library_import_job_progress(
                        &app,
                        &job,
                        String::from("processing"),
                        progress_percent(42, 88, progress.processed, progress.total.max(1)),
                        progress.processed,
                        progress.total.max(1),
                        0,
                        discovered_total,
                        candidate_total,
                        scan_report.diagnostics.directories_scanned,
                        scan_report.diagnostics.entries_scanned,
                        current_file,
                        elapsed_ms(started_at),
                    );
                },
            )?;
            let prepare_ms = elapsed_ms(prepare_started_at);
            let prepare_resource_end = capture_process_resource_snapshot();
            step_profiles.push(build_diagnostic_step_profile(
                IMPORT_STAGE_PREPARE,
                prepare_ms,
                prepare_resource_start.as_ref(),
                prepare_resource_end.as_ref(),
            ));
            job.current_file.clear();
            update_library_import_job_stage(
                &mut job,
                IMPORT_STAGE_PREPARE,
                IMPORT_STAGE_STATUS_COMPLETED,
                prepared_tracks.len(),
                candidate_total,
                Some(prepare_ms),
            );
            persist_library_import_job_snapshot(&import_jobs, &job)?;
            (prepared_tracks, prepare_diagnostics, prepare_ms)
        };

        let (imported_tracks, persist_ms) = if prepared_tracks.is_empty() {
            if candidate_total > 0 {
                let persist_resource_sample = capture_process_resource_snapshot();
                step_profiles.push(build_diagnostic_step_profile(
                    IMPORT_STAGE_PERSIST,
                    0,
                    persist_resource_sample.as_ref(),
                    persist_resource_sample.as_ref(),
                ));
            }
            (Vec::new(), 0)
        } else {
            let prepared_total = prepared_tracks.len();
            update_library_import_job_stage(
                &mut job,
                IMPORT_STAGE_PERSIST,
                IMPORT_STAGE_STATUS_RUNNING,
                0,
                prepared_total,
                None,
            );
            persist_library_import_job_snapshot(&import_jobs, &job)?;
            emit_library_import_job_progress(
                &app,
                &job,
                String::from("importing"),
                92,
                prepared_total,
                prepared_total.max(1),
                0,
                discovered_total,
                candidate_total,
                scan_report.diagnostics.directories_scanned,
                scan_report.diagnostics.entries_scanned,
                String::new(),
                elapsed_ms(started_at),
            );

            let persist_started_at = Instant::now();
            let persist_resource_start = capture_process_resource_snapshot();
            let imported_tracks = {
                let desktop_state = desktop_state
                    .lock()
                    .map_err(|_| String::from("Desktop state lock was poisoned."))?;

                import_prepared_tracks(&desktop_state, &request.library_id, prepared_tracks)?
            };
            let persist_ms = elapsed_ms(persist_started_at);
            let persist_resource_end = capture_process_resource_snapshot();
            step_profiles.push(build_diagnostic_step_profile(
                IMPORT_STAGE_PERSIST,
                persist_ms,
                persist_resource_start.as_ref(),
                persist_resource_end.as_ref(),
            ));
            job.imported_total = imported_tracks.len();
            update_library_import_job_stage(
                &mut job,
                IMPORT_STAGE_PERSIST,
                IMPORT_STAGE_STATUS_COMPLETED,
                imported_tracks.len(),
                prepared_total,
                Some(persist_ms),
            );
            persist_library_import_job_snapshot(&import_jobs, &job)?;
            (imported_tracks, persist_ms)
        };

        update_library_import_job_stage(
            &mut job,
            IMPORT_STAGE_PLAYBACK_SYNC,
            IMPORT_STAGE_STATUS_RUNNING,
            0,
            imported_tracks.len().max(1),
            None,
        );
        persist_library_import_job_snapshot(&import_jobs, &job)?;
        emit_library_import_job_progress(
            &app,
            &job,
            String::from("importing"),
            if imported_tracks.is_empty() { 96 } else { 98 },
            imported_tracks.len(),
            imported_tracks.len().max(1),
            imported_tracks.len(),
            discovered_total,
            candidate_total,
            scan_report.diagnostics.directories_scanned,
            scan_report.diagnostics.entries_scanned,
            String::new(),
            elapsed_ms(started_at),
        );

        let playback_sync_started_at = Instant::now();
        let playback_sync_resource_start = capture_process_resource_snapshot();
        let playback_result = {
            let desktop_state = desktop_state
                .lock()
                .map_err(|_| String::from("Desktop state lock was poisoned."))?;
            let mut playback = playback
                .lock()
                .map_err(|_| String::from("Rust playback state lock was poisoned."))?;

            sync_catalog_playback_state(&desktop_state, &mut playback)?
        };
        let playback_sync_ms = elapsed_ms(playback_sync_started_at);
        let playback_sync_resource_end = capture_process_resource_snapshot();
        step_profiles.push(build_diagnostic_step_profile(
            IMPORT_STAGE_PLAYBACK_SYNC,
            playback_sync_ms,
            playback_sync_resource_start.as_ref(),
            playback_sync_resource_end.as_ref(),
        ));
        update_library_import_job_stage(
            &mut job,
            IMPORT_STAGE_PLAYBACK_SYNC,
            IMPORT_STAGE_STATUS_COMPLETED,
            imported_tracks.len(),
            imported_tracks.len().max(1),
            Some(playback_sync_ms),
        );
        let process_end = capture_process_resource_snapshot();

        let diagnostics = LibraryImportDiagnostics {
            total_ms: elapsed_ms(started_at),
            discover_ms,
            filter_ms,
            prepare_ms,
            persist_ms,
            playback_sync_ms,
            copy_ms: prepare_diagnostics.copy_ms,
            metadata_ms: prepare_diagnostics.metadata_ms,
            metadata_fallback_count: prepare_diagnostics.metadata_fallback_count,
            directories_scanned: scan_report.diagnostics.directories_scanned,
            entries_scanned: scan_report.diagnostics.entries_scanned,
            discovered_total,
            candidate_total,
            imported_total: imported_tracks.len(),
            process: build_process_resource_diagnostics(
                process_start.as_ref(),
                process_end.as_ref(),
            ),
            step_profiles: step_profiles.clone(),
        };
        finalize_library_import_job(
            &mut job,
            if candidate_total == 0 {
                IMPORT_JOB_STATUS_EMPTY
            } else {
                IMPORT_JOB_STATUS_COMPLETED
            },
            diagnostics.clone(),
            None,
        );
        persist_library_import_job_snapshot(&import_jobs, &job)?;

        Ok(build_library_import_result(
            job.clone(),
            imported_tracks,
            invalidation,
            discovered_total,
            candidate_total,
            diagnostics,
            playback_result,
        ))
    };

    match run() {
        Ok(result) => {
            emit_library_import_job_progress(
                &app,
                &result.job,
                if result.candidate_total == 0 {
                    String::from("empty")
                } else {
                    String::from("complete")
                },
                100,
                result.imported_tracks.len(),
                result.candidate_total.max(result.discovered_total).max(1),
                result.imported_tracks.len(),
                result.discovered_total,
                result.candidate_total,
                result.diagnostics.directories_scanned,
                result.diagnostics.entries_scanned,
                String::new(),
                result.diagnostics.total_ms,
            );
            Ok(result)
        }
        Err(error) => {
            mark_library_import_job_failed(&mut job);
            let process_end = capture_process_resource_snapshot();
            let diagnostics = build_library_import_diagnostics_from_job(
                &job,
                elapsed_ms(started_at),
                build_process_resource_diagnostics(process_start.as_ref(), process_end.as_ref()),
                step_profiles.clone(),
            );
            finalize_library_import_job(
                &mut job,
                IMPORT_JOB_STATUS_FAILED,
                diagnostics.clone(),
                Some(error.clone()),
            );
            persist_library_import_job_snapshot(&import_jobs, &job)?;
            emit_library_import_job_progress(
                &app,
                &job,
                String::from("error"),
                100,
                0,
                request.directories.len().max(1),
                0,
                job.discovered_total,
                job.candidate_total,
                diagnostics.directories_scanned,
                diagnostics.entries_scanned,
                String::new(),
                diagnostics.total_ms,
            );
            Err(error)
        }
    }
}

#[tauri::command]
fn metadata_parse_audio_file(
    request: ParseAudioMetadataRequest,
) -> MetadataParseResult<ParsedAudioMetadata> {
    metadata::parse_audio_metadata(request)
}

#[tauri::command]
fn lyrics_resolve_track(request: ResolveTrackLyricsRequest) -> Result<ResolvedTrackLyrics, String> {
    lyrics::resolve_track_lyrics(request)
}

#[tauri::command]
fn storage_configure_watch(
    app: AppHandle,
    watcher: State<'_, Mutex<StorageWatchManager>>,
    request: ConfigureStorageWatchRequest,
) -> Result<StorageWatchSnapshot, String> {
    let mut watcher = watcher
        .lock()
        .map_err(|_| String::from("Storage watch state lock was poisoned."))?;

    watcher.configure(&app, request)
}

#[tauri::command]
fn diagnostics_log_event(
    app: AppHandle,
    diagnostics: State<'_, Mutex<DiagnosticsLogStore>>,
    request: DiagnosticsLogEventRequest,
) -> Result<bool, String> {
    let mut diagnostics = diagnostics
        .lock()
        .map_err(|_| String::from("Diagnostics log state lock was poisoned."))?;
    diagnostics.initialize(&app)?;
    diagnostics.append_event(&request)?;
    Ok(true)
}

#[tauri::command]
fn diagnostics_log_status(
    app: AppHandle,
    diagnostics: State<'_, Mutex<DiagnosticsLogStore>>,
) -> Result<DiagnosticsLogStatus, String> {
    let mut diagnostics = diagnostics
        .lock()
        .map_err(|_| String::from("Diagnostics log state lock was poisoned."))?;
    diagnostics.initialize(&app)?;
    diagnostics.log_status()
}

#[tauri::command]
fn external_library_provider_capabilities(
    request: ExternalProviderCapabilitiesRequest,
) -> Result<ExternalProviderCapabilities, String> {
    Ok(external_sources::provider_capabilities(&request.provider))
}

#[tauri::command]
async fn external_library_test_connection(
    request: ExternalLibraryConnectionRequest,
) -> Result<ExternalLibraryTestResult, String> {
    external_sources::test_connection(request).await
}

#[tauri::command]
async fn external_library_list_libraries(
    request: ExternalLibraryConnectionRequest,
) -> Result<ExternalLibraryListResult, String> {
    external_sources::list_libraries(request).await
}

#[tauri::command]
async fn external_library_list_tracks(
    request: ExternalLibraryConnectionRequest,
) -> Result<ExternalTrackListResult, String> {
    external_sources::list_tracks(request).await
}

#[tauri::command]
async fn external_library_resolve_playback_source(
    app: tauri::AppHandle,
    request: ExternalPlaybackSourceRequest,
) -> Result<ExternalPlaybackSourceResult, String> {
    let total_started_at = Instant::now();
    let process_start = capture_process_resource_snapshot();
    let track_id = optional_text(&request.track, "id");
    let include_metadata = request.include_metadata.unwrap_or(true);

    let cache_dir_started_at = Instant::now();
    let cache_dir_resource_start = capture_process_resource_snapshot();
    let cache_dir = app_paths::cache_subdir("external-sources")
        .map_err(|error| format!("Failed to resolve external playback cache directory: {error}"))?;
    let mut step_profiles = Vec::new();
    push_diagnostic_step_profile(
        &mut step_profiles,
        "resolveCacheDir",
        cache_dir_started_at,
        cache_dir_resource_start,
    );

    let resolve_started_at = Instant::now();
    let resolve_resource_start = capture_process_resource_snapshot();
    let result = external_sources::resolve_playback_source(request, cache_dir).await;
    push_diagnostic_step_profile(
        &mut step_profiles,
        "resolvePlaybackSource",
        resolve_started_at,
        resolve_resource_start,
    );

    if let Ok(resolved) = result.as_ref() {
        let process_end = capture_process_resource_snapshot();
        append_diagnostics_event(
            &app,
            DiagnosticsLogEventRequest {
                level: Some(String::from("info")),
                category: String::from("playback"),
                event: String::from("external_playback_source_profile"),
                label: Some(String::from("[OFPlayer external playback source]")),
                payload: Some(json!({
                    "trackId": track_id,
                    "includeMetadata": include_metadata,
                    "provider": resolved.provider,
                    "sourceKind": resolved.source.get("kind").and_then(Value::as_str).unwrap_or_default(),
                    "metadataPayloadBytes": resolved.metadata.as_ref().map(|value| value.to_string().len()).unwrap_or(0),
                    "totalMs": elapsed_ms(total_started_at),
                    "process": build_process_resource_diagnostics(process_start.as_ref(), process_end.as_ref()),
                    "stepProfiles": step_profiles,
                })),
            },
        );
    }

    result
}

#[tauri::command]
fn desktop_app_exit(app: AppHandle) -> Result<bool, String> {
    app.exit(0);
    Ok(true)
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .manage(Mutex::new(DiagnosticsLogStore::default()))
        .manage(Mutex::new(CapsuleArtworkCache::default()))
        .manage(Mutex::new(CapsuleStateStore::default()))
        .manage(Mutex::new(DesktopStateStore::default()))
        .manage(Mutex::new(LibraryImportJobStore::default()))
        .manage(Mutex::new(MobileHandoffState::default()))
        .manage(Mutex::new(PlaybackManager::new()))
        .manage(Mutex::new(StorageWatchManager::default()))
        .setup(|app| {
            let setup_started_at = Instant::now();
            app_paths::prepare_runtime_data_dirs()?;
            ensure_main_window(app)?;

            let desktop_state = app.state::<Mutex<DesktopStateStore>>();
            let desktop_state_started_at = Instant::now();
            let mut desktop_state = desktop_state
                .lock()
                .map_err(|_| String::from("Desktop state lock was poisoned during setup."))?;
            desktop_state.initialize(app.handle())?;
            let desktop_state_initialize_ms = elapsed_ms(desktop_state_started_at);
            drop(desktop_state);

            let playback = app.state::<Mutex<PlaybackManager>>();
            let playback_setup_started_at = Instant::now();
            let mut playback = playback
                .lock()
                .map_err(|_| String::from("Rust playback state lock was poisoned during setup."))?;

            let _ = playback.initialize_system_media(app.handle());
            let system_media_initialize_ms = elapsed_ms(playback_setup_started_at);
            let emit_snapshot_started_at = Instant::now();
            emit_playback_snapshot(app.handle(), &playback.snapshot());
            let emit_snapshot_ms = elapsed_ms(emit_snapshot_started_at);
            let audio_meter = playback.audio_meter_handle();
            drop(playback);
            let playback_emitter_started_at = Instant::now();
            spawn_playback_snapshot_emitter(app.handle().clone());
            spawn_capsule_meter_emitter(app.handle().clone(), audio_meter);
            spawn_capsule_state_emitter(app.handle().clone());
            let playback_emitter_spawn_ms = elapsed_ms(playback_emitter_started_at);

            append_diagnostics_event(
                app.handle(),
                DiagnosticsLogEventRequest {
                    level: Some(String::from("info")),
                    category: String::from("startup"),
                    event: String::from("tauri_setup"),
                    label: Some(String::from("[OFPlayer tauri setup]")),
                    payload: Some(json!({
                        "desktopStateInitializeMs": desktop_state_initialize_ms,
                        "systemMediaInitializeMs": system_media_initialize_ms,
                        "emitInitialPlaybackSnapshotMs": emit_snapshot_ms,
                        "playbackEmitterSpawnMs": playback_emitter_spawn_ms,
                        "totalMs": elapsed_ms(setup_started_at),
                    })),
                },
            );
            Ok(())
        })
        .plugin(tauri_plugin_dialog::init())
        .invoke_handler(tauri::generate_handler![
            window_surface_platform_profile,
            desktop_app_exit,
            immersive_window_apply_mode,
            playback_snapshot,
            playback_load_track,
            playback_play,
            playback_pause,
            playback_seek,
            playback_set_volume,
            playback_reset,
            playback_recover_output,
            playback_list_output_devices,
            playback_set_output_device,
            playback_session_sync_catalog,
            playback_session_set_queue,
            playback_session_select_track,
            playback_session_play_current,
            playback_session_pause,
            playback_session_next,
            playback_session_previous,
            playback_session_handle_ended,
            capsule_get_boot_state,
            capsule_create_window,
            capsule_apply_hit_region,
            capsule_release,
            desktop_state_load_preferences,
            desktop_state_save_preferences,
            desktop_state_load_session,
            desktop_state_save_session,
            desktop_external_library_load_connections,
            desktop_external_library_put_connection,
            desktop_external_library_delete_connection,
            desktop_state_load_bootstrap,
            desktop_state_reset_all_data,
            desktop_storage_analyze,
            desktop_storage_collect_garbage,
            desktop_catalog_load_snapshot,
            desktop_catalog_put_libraries,
            desktop_catalog_delete_libraries,
            desktop_catalog_put_playlists,
            desktop_catalog_delete_playlists,
            desktop_catalog_put_playlist_track_relations,
            desktop_catalog_delete_playlist_track_relations,
            desktop_catalog_get_track,
            desktop_catalog_put_tracks,
            desktop_catalog_update_track,
            desktop_catalog_delete_tracks,
            desktop_history_load_recent,
            desktop_history_append,
            desktop_catalog_resolve_navigation,
            desktop_catalog_query_collection_tracks,
            desktop_library_create,
            desktop_library_rename,
            desktop_library_delete,
            desktop_library_reorder,
            desktop_playlist_create,
            desktop_playlist_rename,
            desktop_playlist_delete,
            desktop_playlist_reorder,
            desktop_playlist_add_track,
            desktop_playlist_remove_track,
            desktop_playlist_reorder_tracks,
            desktop_track_delete_from_library,
            desktop_tracks_delete_from_library,
            desktop_track_set_favorite,
            desktop_track_toggle_favorite,
            desktop_library_import_files,
            desktop_library_scan_import,
            metadata_parse_audio_file,
            lyrics_resolve_track,
            mobile_handoff_capabilities,
            mobile_handoff_state_snapshot,
            mobile_handoff_record_event,
            external_library_provider_capabilities,
            external_library_test_connection,
            external_library_list_libraries,
            external_library_list_tracks,
            external_library_resolve_playback_source,
            storage_configure_watch,
            diagnostics_log_event,
            diagnostics_log_status
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{fs, io, path::PathBuf};

    #[cfg(unix)]
    fn try_symlink_file(original: &Path, link: &Path) -> io::Result<()> {
        std::os::unix::fs::symlink(original, link)
    }

    #[cfg(windows)]
    fn try_symlink_file(original: &Path, link: &Path) -> io::Result<()> {
        std::os::windows::fs::symlink_file(original, link)
    }

    fn unique_test_dir(name: &str) -> PathBuf {
        std::env::temp_dir().join(format!("ofplayer-tauri-{name}-{}", Uuid::new_v4()))
    }

    #[test]
    fn path_is_inside_existing_directory_accepts_owned_file() {
        let root = unique_test_dir("owned-root");
        let child = root.join("webdav");
        fs::create_dir_all(&child).unwrap();
        let file = child.join("external-cache-test.mp3");
        fs::write(&file, b"audio").unwrap();

        assert!(path_is_inside_existing_directory(
            file.to_string_lossy().as_ref(),
            &root
        ));

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn path_is_inside_existing_directory_rejects_unmanaged_file() {
        let root = unique_test_dir("owned-root");
        let outside = unique_test_dir("outside-root");
        fs::create_dir_all(&root).unwrap();
        fs::create_dir_all(&outside).unwrap();
        let file = outside.join("external-cache-test.mp3");
        fs::write(&file, b"audio").unwrap();

        assert!(!path_is_inside_existing_directory(
            file.to_string_lossy().as_ref(),
            &root
        ));

        let _ = fs::remove_dir_all(root);
        let _ = fs::remove_dir_all(outside);
    }

    #[test]
    fn path_is_inside_existing_directory_rejects_symlink_escape_when_supported() {
        let root = unique_test_dir("owned-root");
        let outside = unique_test_dir("outside-root");
        fs::create_dir_all(&root).unwrap();
        fs::create_dir_all(&outside).unwrap();
        let outside_file = outside.join("external-cache-test.mp3");
        let link = root.join("external-cache-link.mp3");
        fs::write(&outside_file, b"audio").unwrap();

        if try_symlink_file(&outside_file, &link).is_err() {
            let _ = fs::remove_dir_all(root);
            let _ = fs::remove_dir_all(outside);
            return;
        }

        assert!(!path_is_inside_existing_directory(
            link.to_string_lossy().as_ref(),
            &root
        ));

        let _ = fs::remove_dir_all(root);
        let _ = fs::remove_dir_all(outside);
    }

    #[test]
    fn path_is_inside_existing_directory_rejects_missing_file() {
        let root = unique_test_dir("owned-root");
        fs::create_dir_all(&root).unwrap();
        let missing = root.join("missing.mp3");

        assert!(!path_is_inside_existing_directory(
            missing.to_string_lossy().as_ref(),
            &root
        ));

        let _ = fs::remove_dir_all(root);
    }
}
