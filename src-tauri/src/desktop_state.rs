use crate::{
    app_paths,
    catalog_db::*,
    db_helpers::*,
    diagnostics::{
        build_diagnostic_step_profile, build_process_resource_diagnostics,
        capture_process_resource_snapshot,
    },
    navigation::{
        build_sortable_track_cache_from_records, create_navigation_request_from_preferences,
        load_sortable_track_cache_from_connection, query_collection_tracks_from_connection,
        resolve_navigation_summary_from_connection,
    },
    playback::{PlaybackSnapshot, PlaybackStatus},
    schema::initialize_schema,
    session_ops::{
        apply_session_playback_state, build_queue_from_catalog, load_all_catalog_track_ids,
        load_session_snapshot_from_connection, normalize_session_queue_track_ids,
        reset_session_playback_state, resolve_adjacent_session_track_id,
        save_session_snapshot_to_connection, touch_session_snapshot, SESSION_PLAYBACK_STATUS_IDLE,
        SESSION_PLAYBACK_STATUS_PAUSED, SESSION_PLAYBACK_STATUS_PLAYING, SESSION_STATE_KEY,
    },
    sorting::{QueryTracksResult, SortableTrack},
    storage, storage_maintenance,
};
use rusqlite::{params, Connection, OptionalExtension, Transaction};
use serde_json::{json, Value};
use std::{
    cell::{Cell, RefCell},
    collections::{HashMap, HashSet},
    path::{Path, PathBuf},
    time::Instant,
};
use tauri::AppHandle;

const DATABASE_FILE_NAME: &str = "ofplayer-desktop.sqlite3";
const PREFERENCES_STATE_KEY: &str = "preferences";
const REVISION_CATALOG_STATE_KEY: &str = "revision.catalog";
const REVISION_NAVIGATION_STATE_KEY: &str = "revision.navigation";
const REVISION_HISTORY_STATE_KEY: &str = "revision.history";
const REVISION_PREFERENCES_STATE_KEY: &str = "revision.preferences";
const REVISION_SESSION_STATE_KEY: &str = "revision.session";
const LEGACY_SNAPSHOT_CATALOG_STATE_KEY: &str = "snapshot.catalog";
const EXTERNAL_LIBRARY_CONNECTIONS_STATE_KEY: &str = "externalLibraries.connections";
const DELETED_IMPORT_PATHS_STATE_KEY: &str = "deletedImportPaths";
const DESKTOP_BOOTSTRAP_MANIFEST_VERSION: &str = "desktop-bootstrap-v1";
use crate::desktop_types::*;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum TrackArtworkSnapshotMode {
    All,
    AlbumCovers,
    None,
}

#[derive(Default)]
pub struct DesktopStateStore {
    database_path: Option<PathBuf>,
    catalog_consistency_checked: Cell<bool>,
    track_query_cache: RefCell<Option<HashMap<String, SortableTrack>>>,
}

fn playback_status_key(status: PlaybackStatus) -> &'static str {
    match status {
        PlaybackStatus::Idle => SESSION_PLAYBACK_STATUS_IDLE,
        PlaybackStatus::Paused => SESSION_PLAYBACK_STATUS_PAUSED,
        PlaybackStatus::Playing => SESSION_PLAYBACK_STATUS_PLAYING,
    }
}

fn parse_track_artwork_snapshot_mode(value: Option<&str>) -> TrackArtworkSnapshotMode {
    match value.map(str::trim).map(str::to_ascii_lowercase).as_deref() {
        Some("none") => TrackArtworkSnapshotMode::None,
        Some("album-covers") => TrackArtworkSnapshotMode::AlbumCovers,
        _ => TrackArtworkSnapshotMode::All,
    }
}

fn apply_track_artwork_snapshot_mode(
    connection: &Connection,
    tracks: Vec<Value>,
    mode: TrackArtworkSnapshotMode,
) -> Result<Vec<Value>, String> {
    match mode {
        TrackArtworkSnapshotMode::All => tracks
            .into_iter()
            .map(|track| attach_track_artwork(connection, track))
            .collect(),
        TrackArtworkSnapshotMode::None => Ok(tracks.into_iter().map(strip_track_artwork).collect()),
        TrackArtworkSnapshotMode::AlbumCovers => {
            let mut retained_album_keys = HashSet::new();

            tracks
                .into_iter()
                .map(|track| {
                    if retained_album_keys.insert(track_album_artwork_key(&track)) {
                        attach_track_artwork(connection, track)
                    } else {
                        Ok(strip_track_artwork(track))
                    }
                })
                .collect()
        }
    }
}

fn track_album_artwork_key(track: &Value) -> String {
    let library = normalized_artwork_key_field(track, "libraryId")
        .unwrap_or_else(|| String::from("<unknown-library>"));
    let album = normalized_artwork_key_field(track, "album")
        .unwrap_or_else(|| String::from("<unknown-album>"));
    let artist = normalized_artwork_key_field(track, "albumArtist")
        .or_else(|| normalized_artwork_key_field(track, "artist"))
        .unwrap_or_else(|| String::from("<unknown-artist>"));

    format!("{library}\u{1f}{album}\u{1f}{artist}")
}

fn normalized_artwork_key_field(track: &Value, field: &str) -> Option<String> {
    optional_text_field(track, field).map(|value| value.to_ascii_lowercase())
}

fn strip_track_artwork(mut track: Value) -> Value {
    if let Some(track_object) = track.as_object_mut() {
        track_object.insert(String::from("artwork"), Value::String(String::new()));
    }

    track
}

impl DesktopStateStore {
    pub fn initialize(&mut self, _app: &AppHandle) -> Result<(), String> {
        if self.database_path.is_some() {
            return Ok(());
        }

        let data_dir = app_paths::state_dir()?;
        let database_path = data_dir.join(DATABASE_FILE_NAME);
        let connection = open_connection(&database_path)?;
        initialize_schema(&connection)?;
        clear_legacy_catalog_snapshot_cache(&connection)?;
        self.database_path = Some(database_path);
        self.catalog_consistency_checked.set(false);
        self.track_query_cache.borrow_mut().take();
        Ok(())
    }

    pub fn load_preferences(&self) -> Result<Option<Value>, String> {
        self.load_json(PREFERENCES_STATE_KEY)
    }

    pub fn save_preferences(&self, value: &Value) -> Result<(), String> {
        self.save_json(PREFERENCES_STATE_KEY, value)?;
        self.mark_preferences_changed()
    }

    pub fn load_session(&self) -> Result<Option<Value>, String> {
        self.load_json(SESSION_STATE_KEY)
    }

    pub fn save_session(&self, value: &Value) -> Result<(), String> {
        self.save_json(SESSION_STATE_KEY, value)?;
        self.mark_session_changed()
    }

    pub fn load_external_library_connections(&self) -> Result<Vec<Value>, String> {
        Ok(self
            .load_json(EXTERNAL_LIBRARY_CONNECTIONS_STATE_KEY)?
            .and_then(|value| value.as_array().cloned())
            .unwrap_or_default())
    }

    pub fn put_external_library_connection(&self, value: &Value) -> Result<Value, String> {
        let connection_id = required_text_field(value, "id", "external library connection")?;
        let mut connections = self.load_external_library_connections()?;
        let mut saved = false;

        for connection in &mut connections {
            if optional_text_field(connection, "id").as_deref() == Some(connection_id.as_str()) {
                *connection = value.clone();
                saved = true;
                break;
            }
        }

        if !saved {
            connections.push(value.clone());
        }

        self.save_json(
            EXTERNAL_LIBRARY_CONNECTIONS_STATE_KEY,
            &Value::Array(connections),
        )?;
        Ok(value.clone())
    }

    pub fn delete_external_library_connection(&self, connection_id: &str) -> Result<bool, String> {
        let mut connections = self.load_external_library_connections()?;
        let before_len = connections.len();

        connections.retain(|connection| {
            optional_text_field(connection, "id").as_deref() != Some(connection_id)
        });

        let deleted = connections.len() != before_len;

        if deleted {
            self.save_json(
                EXTERNAL_LIBRARY_CONNECTIONS_STATE_KEY,
                &Value::Array(connections),
            )?;
        }

        Ok(deleted)
    }

    pub fn load_session_snapshot(&self) -> Result<SessionStateSnapshot, String> {
        let connection = self.connection()?;
        load_session_snapshot_from_connection(&connection)
    }

    pub fn update_session_playback_state(
        &self,
        playback: &PlaybackSnapshot,
    ) -> Result<SessionStateSnapshot, String> {
        let connection = self.connection()?;
        let mut session = load_session_snapshot_from_connection(&connection)?;
        let Some(active_track_id) = playback.active_track_id.as_ref() else {
            return Ok(session);
        };

        if !session
            .queue_track_ids
            .iter()
            .any(|queued_track_id| queued_track_id == active_track_id)
        {
            return Ok(session);
        }

        session.current_track_id = Some(active_track_id.clone());
        apply_session_playback_state(
            &mut session,
            playback_status_key(playback.status),
            playback.current_time,
            playback.duration,
        );
        touch_session_snapshot(&mut session);
        save_session_snapshot_to_connection(&connection, &session)?;
        self.mark_session_changed()?;
        Ok(session)
    }

    pub fn sync_session_with_catalog(&self) -> Result<SessionStateSnapshot, String> {
        let connection = self.connection()?;
        let mut session = load_session_snapshot_from_connection(&connection)?;
        let available_track_ids = load_all_catalog_track_ids(&connection)?;
        let next_queue_track_ids =
            build_queue_from_catalog(&session.queue_track_ids, &available_track_ids);
        let next_current_track_id = if session.current_track_id.as_ref().is_some_and(|track_id| {
            next_queue_track_ids
                .iter()
                .any(|queued_track_id| queued_track_id == track_id)
        }) {
            session.current_track_id.clone()
        } else {
            next_queue_track_ids.first().cloned()
        };

        if session.queue_track_ids != next_queue_track_ids
            || session.current_track_id != next_current_track_id
        {
            let current_track_changed = session.current_track_id != next_current_track_id;
            session.queue_track_ids = next_queue_track_ids;
            session.current_track_id = next_current_track_id;
            if current_track_changed {
                reset_session_playback_state(&mut session);
            }
            touch_session_snapshot(&mut session);
            save_session_snapshot_to_connection(&connection, &session)?;
            self.mark_session_changed()?;
        }

        Ok(session)
    }

    pub fn set_session_queue(&self, track_ids: &[String]) -> Result<SessionStateSnapshot, String> {
        let connection = self.connection()?;
        let mut session = load_session_snapshot_from_connection(&connection)?;
        let available_track_id_set = load_all_catalog_track_ids(&connection)?
            .into_iter()
            .collect::<HashSet<_>>();
        let next_queue_track_ids =
            normalize_session_queue_track_ids(track_ids, &available_track_id_set);
        let next_current_track_id = match session.current_track_id.as_ref() {
            Some(track_id)
                if next_queue_track_ids
                    .iter()
                    .any(|queued_track_id| queued_track_id == track_id) =>
            {
                Some(track_id.clone())
            }
            _ => None,
        };

        if session.queue_track_ids != next_queue_track_ids
            || session.current_track_id != next_current_track_id
        {
            let current_track_changed = session.current_track_id != next_current_track_id;
            session.queue_track_ids = next_queue_track_ids;
            session.current_track_id = next_current_track_id;
            if current_track_changed {
                reset_session_playback_state(&mut session);
            }
            touch_session_snapshot(&mut session);
            save_session_snapshot_to_connection(&connection, &session)?;
            self.mark_session_changed()?;
        }

        Ok(session)
    }

    pub fn select_session_track(
        &self,
        track_id: &str,
        queue_track_ids: Option<&[String]>,
    ) -> Result<SessionStateSnapshot, String> {
        let connection = self.connection()?;
        let mut session = load_session_snapshot_from_connection(&connection)?;
        let available_track_id_set = load_all_catalog_track_ids(&connection)?
            .into_iter()
            .collect::<HashSet<_>>();

        if !available_track_id_set.contains(track_id) {
            return Err(String::from(
                "Selected track was not found in the desktop catalog.",
            ));
        }

        let next_queue_track_ids = if let Some(queue_track_ids) = queue_track_ids {
            let normalized_queue_track_ids =
                normalize_session_queue_track_ids(queue_track_ids, &available_track_id_set);

            if !normalized_queue_track_ids
                .iter()
                .any(|queued_track_id| queued_track_id == track_id)
            {
                return Err(String::from(
                    "Selected track is not available in the supplied playback queue.",
                ));
            }

            normalized_queue_track_ids
        } else {
            if !session
                .queue_track_ids
                .iter()
                .any(|queued_track_id| queued_track_id == track_id)
            {
                return Err(String::from(
                    "Selected track is not available in the current playback queue.",
                ));
            }

            session.queue_track_ids.clone()
        };
        let next_current_track_id = Some(String::from(track_id));

        if session.queue_track_ids != next_queue_track_ids
            || session.current_track_id != next_current_track_id
        {
            let current_track_changed = session.current_track_id != next_current_track_id;
            session.queue_track_ids = next_queue_track_ids;
            session.current_track_id = next_current_track_id;
            if current_track_changed {
                reset_session_playback_state(&mut session);
            }
            touch_session_snapshot(&mut session);
            save_session_snapshot_to_connection(&connection, &session)?;
            self.mark_session_changed()?;
        }

        Ok(session)
    }

    pub fn advance_session_to_next_track(&self) -> Result<Option<SessionStateSnapshot>, String> {
        let connection = self.connection()?;
        let mut session = load_session_snapshot_from_connection(&connection)?;
        let Some(next_track_id) = resolve_adjacent_session_track_id(&session, 1) else {
            return Ok(None);
        };

        if session.current_track_id.as_deref() != Some(next_track_id.as_str()) {
            session.current_track_id = Some(next_track_id);
            reset_session_playback_state(&mut session);
            touch_session_snapshot(&mut session);
            save_session_snapshot_to_connection(&connection, &session)?;
            self.mark_session_changed()?;
        }

        Ok(Some(session))
    }

    pub fn advance_session_to_previous_track(
        &self,
    ) -> Result<Option<SessionStateSnapshot>, String> {
        let connection = self.connection()?;
        let mut session = load_session_snapshot_from_connection(&connection)?;
        let Some(previous_track_id) = resolve_adjacent_session_track_id(&session, -1) else {
            return Ok(None);
        };

        if session.current_track_id.as_deref() != Some(previous_track_id.as_str()) {
            session.current_track_id = Some(previous_track_id);
            reset_session_playback_state(&mut session);
            touch_session_snapshot(&mut session);
            save_session_snapshot_to_connection(&connection, &session)?;
            self.mark_session_changed()?;
        }

        Ok(Some(session))
    }

    pub fn remove_track_from_session_queue(
        &self,
        track_id: &str,
    ) -> Result<SessionStateSnapshot, String> {
        let connection = self.connection()?;
        let mut session = load_session_snapshot_from_connection(&connection)?;
        let next_queue_track_ids = session
            .queue_track_ids
            .iter()
            .filter(|queued_track_id| queued_track_id.as_str() != track_id)
            .cloned()
            .collect::<Vec<_>>();
        let next_current_track_id = match session.current_track_id.as_deref() {
            Some(current_track_id)
                if current_track_id != track_id
                    && next_queue_track_ids
                        .iter()
                        .any(|queued_track_id| queued_track_id == current_track_id) =>
            {
                Some(String::from(current_track_id))
            }
            _ => next_queue_track_ids.first().cloned(),
        };

        if session.queue_track_ids != next_queue_track_ids
            || session.current_track_id != next_current_track_id
        {
            let current_track_changed = session.current_track_id != next_current_track_id;
            session.queue_track_ids = next_queue_track_ids;
            session.current_track_id = next_current_track_id;
            if current_track_changed {
                reset_session_playback_state(&mut session);
            }
            touch_session_snapshot(&mut session);
            save_session_snapshot_to_connection(&connection, &session)?;
            self.mark_session_changed()?;
        }

        Ok(session)
    }

    pub fn load_catalog_snapshot(
        &mut self,
        track_artwork_mode: Option<&str>,
    ) -> Result<CatalogSnapshot, String> {
        let mut connection = self.connection()?;
        let mut snapshot = ensure_catalog_consistency(&mut connection)?;
        let sortable_track_cache = build_sortable_track_cache_from_records(&snapshot.tracks)?;
        *self.track_query_cache.borrow_mut() = Some(sortable_track_cache);
        self.catalog_consistency_checked.set(true);
        snapshot.tracks = apply_track_artwork_snapshot_mode(
            &connection,
            snapshot.tracks,
            parse_track_artwork_snapshot_mode(track_artwork_mode),
        )?;
        Ok(snapshot)
    }

    pub fn put_libraries(&self, records: &[Value]) -> Result<(), String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop libraries transaction: {error}")
        })?;

        upsert_libraries(&transaction, records)?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop libraries: {error}"))?;
        self.mark_catalog_structure_changed()?;
        Ok(())
    }

    pub fn delete_libraries(&self, ids: &[String]) -> Result<(), String> {
        let connection = self.connection()?;
        delete_records(&connection, "libraries", ids, "desktop libraries")?;
        self.mark_catalog_structure_changed()?;
        Ok(())
    }

    pub fn put_playlists(&self, records: &[Value]) -> Result<(), String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop playlists transaction: {error}")
        })?;

        upsert_playlists(&transaction, records)?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop playlists: {error}"))?;
        self.mark_catalog_structure_changed()?;
        Ok(())
    }

    pub fn delete_playlists(&self, ids: &[String]) -> Result<(), String> {
        let connection = self.connection()?;
        delete_records(&connection, "playlists", ids, "desktop playlists")?;
        self.mark_catalog_structure_changed()?;
        Ok(())
    }

    pub fn put_playlist_track_relations(&self, records: &[Value]) -> Result<(), String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop playlist-track relations transaction: {error}")
        })?;

        upsert_playlist_track_relations(&transaction, records)?;
        transaction.commit().map_err(|error| {
            format!("Failed to commit desktop playlist-track relations: {error}")
        })?;
        self.mark_catalog_structure_changed()?;
        Ok(())
    }

    pub fn delete_playlist_track_relations(&self, ids: &[String]) -> Result<(), String> {
        let connection = self.connection()?;
        delete_records(
            &connection,
            "playlist_track_relations",
            ids,
            "desktop playlist-track relations",
        )?;
        self.mark_catalog_structure_changed()?;
        Ok(())
    }

    pub fn get_track(
        &self,
        track_id: &str,
        include_artwork: bool,
    ) -> Result<Option<Value>, String> {
        let connection = self.connection()?;
        let serialized = connection
            .query_row(
                "SELECT payload_json FROM tracks WHERE id = ?1",
                [track_id],
                |row| row.get::<_, String>(0),
            )
            .optional()
            .map_err(|error| format!("Failed to read desktop track '{track_id}': {error}"))?;

        let track = serialized
            .map(|payload| deserialize_value(&payload, "desktop track"))
            .transpose()?;

        if include_artwork {
            track
                .map(|track| attach_track_artwork(&connection, track))
                .transpose()
        } else {
            Ok(track)
        }
    }

    pub fn put_tracks(&self, records: &[Value]) -> Result<(), String> {
        self.persist_tracks(records)?;
        self.mark_catalog_structure_changed()?;
        Ok(())
    }

    fn persist_tracks(&self, records: &[Value]) -> Result<(), String> {
        let mut connection = self.connection()?;
        let transaction = connection
            .transaction()
            .map_err(|error| format!("Failed to open the desktop tracks transaction: {error}"))?;

        upsert_tracks(&transaction, records)?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop tracks: {error}"))?;
        Ok(())
    }

    pub fn filter_library_import_candidates(
        &self,
        request: &LibraryImportCandidatesRequest,
    ) -> Result<Vec<LibraryImportFileInput>, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop import candidate filter transaction: {error}")
        })?;

        ensure_library_exists(&transaction, &request.library_id)?;

        let existing_tracks = load_library_track_values(&transaction, &request.library_id)?;
        let mut known_paths = collect_import_path_keys(&existing_tracks);
        let deleted_import_paths = if request.respect_deleted_import_paths.unwrap_or(false) {
            load_deleted_import_path_keys(&transaction)?
        } else {
            HashSet::new()
        };
        let mut filtered_files = Vec::new();

        for file in &request.files {
            let source_path = normalized_non_empty_text(&file.source_path)
                .ok_or_else(|| String::from("Import candidate is missing a source path."))?;
            let original_path = normalized_non_empty_text(
                file.original_path
                    .as_deref()
                    .unwrap_or(source_path.as_str()),
            )
            .unwrap_or_else(|| source_path.clone());
            let normalized_source_path = normalize_import_path(&source_path);
            let normalized_original_path = normalize_import_path(&original_path);

            if matches_existing_import_path(
                &known_paths,
                &normalized_source_path,
                &normalized_original_path,
            ) {
                continue;
            }

            if matches_existing_import_path(
                &deleted_import_paths,
                &normalized_source_path,
                &normalized_original_path,
            ) {
                continue;
            }

            register_import_path_keys_for_values(
                &mut known_paths,
                &normalized_source_path,
                &normalized_original_path,
            );
            filtered_files.push(LibraryImportFileInput {
                source_path,
                file_name: normalized_optional_text(file.file_name.as_deref()),
                original_path: Some(original_path),
            });
        }

        Ok(filtered_files)
    }

    pub fn import_library_prepared_tracks(
        &self,
        request: &LibraryPreparedTrackImportRequest,
    ) -> Result<Vec<Value>, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop track import transaction: {error}")
        })?;

        ensure_library_exists(&transaction, &request.library_id)?;

        let existing_tracks = load_library_track_values(&transaction, &request.library_id)?;
        let mut known_paths = collect_import_path_keys(&existing_tracks);
        let mut next_library_order = existing_tracks
            .iter()
            .filter_map(|track| track.get("libraryOrder").and_then(Value::as_u64))
            .max()
            .map(|value| usize::try_from(value).unwrap_or(existing_tracks.len()) + 1)
            .unwrap_or(existing_tracks.len());
        let mut imported_tracks = Vec::new();

        for track in &request.tracks {
            let source_path = required_track_source_text(track, "path")?;
            let original_path = optional_track_source_text(track, "originPath")
                .unwrap_or_else(|| source_path.clone());
            let normalized_source_path = normalize_import_path(&source_path);
            let normalized_original_path = normalize_import_path(&original_path);

            if matches_existing_import_path(
                &known_paths,
                &normalized_source_path,
                &normalized_original_path,
            ) {
                continue;
            }

            let normalized_track =
                normalize_prepared_track_import(track, &request.library_id, next_library_order)?;

            register_import_path_keys_for_values(
                &mut known_paths,
                &normalized_source_path,
                &normalized_original_path,
            );
            imported_tracks.push(normalized_track);
            next_library_order += 1;
        }

        clear_deleted_import_paths_for_tracks(&transaction, &imported_tracks)?;
        upsert_tracks(&transaction, &imported_tracks)?;
        self.catalog_consistency_checked.set(false);
        self.invalidate_track_query_cache();
        bump_state_revisions_in_connection(
            &transaction,
            &[REVISION_CATALOG_STATE_KEY, REVISION_NAVIGATION_STATE_KEY],
        )?;
        transaction.commit().map_err(|error| {
            format!("Failed to commit desktop track import transaction: {error}")
        })?;

        Ok(imported_tracks)
    }

    pub fn invalidate_missing_local_indexed_tracks(
        &self,
        request: &LocalIndexInvalidationRequest,
    ) -> Result<LocalIndexInvalidationResult, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop local index invalidation transaction: {error}")
        })?;

        ensure_library_exists(&transaction, &request.library_id)?;

        let existing_tracks = load_library_track_values(&transaction, &request.library_id)?;
        let discovered_paths = request
            .discovered_paths
            .iter()
            .map(|path| normalize_import_path(path))
            .filter(|path| !path.is_empty())
            .collect::<HashSet<_>>();
        let normalized_directories = collect_normalized_scan_directories(&request.directories);
        let mut invalidated_track_ids = Vec::new();
        let mut invalidated_relation_ids = Vec::new();

        for track in &existing_tracks {
            if !is_local_indexed_track(track) {
                continue;
            }

            let Some(source_path) = optional_track_source_text(track, "path") else {
                continue;
            };
            let normalized_source_path = normalize_import_path(&source_path);

            if normalized_source_path.is_empty()
                || discovered_paths.contains(&normalized_source_path)
                || !path_matches_scan_directories(&normalized_source_path, &normalized_directories)
                || PathBuf::from(&source_path).is_file()
            {
                continue;
            }

            let track_id = required_text_field(track, "id", "desktop track")?;
            invalidated_relation_ids
                .extend(load_relation_ids_for_track_delete(&transaction, &track_id)?);
            invalidated_track_ids.push(track_id);
        }

        if invalidated_track_ids.is_empty() {
            transaction.commit().map_err(|error| {
                format!("Failed to commit empty local index invalidation transaction: {error}")
            })?;
            return Ok(LocalIndexInvalidationResult::default());
        }

        delete_records_in_transaction(
            &transaction,
            "playlist_track_relations",
            &invalidated_relation_ids,
            "desktop playlist-track relations",
        )?;
        delete_records_in_transaction(
            &transaction,
            "tracks",
            &invalidated_track_ids,
            "desktop tracks",
        )?;

        let remaining_tracks = load_library_track_values(&transaction, &request.library_id)?;
        let reordered_tracks = normalize_library_track_orders(remaining_tracks)?;
        upsert_tracks(&transaction, &reordered_tracks)?;
        bump_state_revisions_in_connection(
            &transaction,
            &[REVISION_CATALOG_STATE_KEY, REVISION_NAVIGATION_STATE_KEY],
        )?;
        transaction.commit().map_err(|error| {
            format!("Failed to commit local index invalidation transaction: {error}")
        })?;

        self.catalog_consistency_checked.set(false);
        self.invalidate_track_query_cache();

        Ok(LocalIndexInvalidationResult {
            invalidated_track_ids,
            invalidated_relation_ids,
            reordered_tracks,
        })
    }

    pub fn update_track(&self, track_id: &str, patch: &Value) -> Result<Option<Value>, String> {
        let mut current_track = match self.get_track(track_id, false)? {
            Some(track) => track,
            None => return Ok(None),
        };
        let should_clear_artwork = patch
            .get("artwork")
            .and_then(Value::as_str)
            .is_some_and(|value| value.trim().is_empty());
        let should_return_artwork = patch.get("artwork").is_some();

        merge_json_values(&mut current_track, patch);
        let mut connection = self.connection()?;
        let transaction = connection
            .transaction()
            .map_err(|error| format!("Failed to open desktop track update transaction: {error}"))?;
        upsert_tracks(&transaction, &[current_track.clone()])?;
        if should_clear_artwork {
            delete_track_artwork(&transaction, track_id)?;
        }
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop track update: {error}"))?;
        self.mark_catalog_projection_changed()?;
        self.get_track(track_id, should_return_artwork)
    }

    pub fn set_track_favorite(&self, request: &TrackFavoriteRequest) -> Result<Value, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop track favorite transaction: {error}")
        })?;

        let mut track = load_required_track_value(&transaction, &request.track_id)?;
        let updated_at = current_iso_timestamp();

        set_bool_field(
            &mut track,
            "isFavorite",
            request.is_favorite,
            "desktop track",
        )?;
        set_string_field(&mut track, "updatedAt", updated_at, "desktop track")?;

        upsert_tracks(&transaction, &[track.clone()])?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop track favorite update: {error}"))?;
        self.mark_catalog_projection_changed()?;

        Ok(track)
    }

    pub fn toggle_track_favorite(&self, track_id: &str) -> Result<Value, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop track favorite toggle transaction: {error}")
        })?;

        let mut track = load_required_track_value(&transaction, track_id)?;
        let is_favorite = required_boolean_field(&track, "isFavorite", "desktop track")?;
        let updated_at = current_iso_timestamp();

        set_bool_field(&mut track, "isFavorite", !is_favorite, "desktop track")?;
        set_string_field(&mut track, "updatedAt", updated_at, "desktop track")?;

        upsert_tracks(&transaction, &[track.clone()])?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop track favorite toggle: {error}"))?;
        self.mark_catalog_projection_changed()?;

        Ok(track)
    }

    pub fn delete_tracks(&self, ids: &[String]) -> Result<(), String> {
        let connection = self.connection()?;
        delete_records(&connection, "tracks", ids, "desktop tracks")?;
        self.mark_catalog_structure_changed()?;
        Ok(())
    }

    pub fn load_recent_history(&self, limit: usize) -> Result<Vec<Value>, String> {
        let connection = self.connection()?;
        load_recent_history_from_connection(&connection, limit)
    }

    pub fn append_history_entry(&self, entry: &Value) -> Result<(), String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop playback history transaction: {error}")
        })?;

        upsert_history_entries(&transaction, std::slice::from_ref(entry))?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop playback history: {error}"))?;
        self.mark_history_changed()?;
        Ok(())
    }

    pub fn append_history_entries(&self, entries: &[Value]) -> Result<(), String> {
        if entries.is_empty() {
            return Ok(());
        }

        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop playback history batch transaction: {error}")
        })?;

        upsert_history_entries(&transaction, entries)?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop playback history batch: {error}"))?;
        self.mark_history_changed()?;
        Ok(())
    }

    pub fn create_library(
        &self,
        request: &LibraryCreateRequest,
    ) -> Result<LibraryCreateResult, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop library creation transaction: {error}")
        })?;

        let normalized_name = normalize_entity_name(&request.name, "Library")?;
        let order = count_libraries(&transaction)?;
        let library = create_library_value(&normalized_name, order);
        let library_id = required_text_field(&library, "id", "desktop library")?;
        let default_playlist = create_default_playlist_value(&library_id, 0, None);

        upsert_libraries(&transaction, std::slice::from_ref(&library))?;
        upsert_playlists(&transaction, std::slice::from_ref(&default_playlist))?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop library creation: {error}"))?;
        self.mark_catalog_structure_changed()?;

        Ok(LibraryCreateResult {
            library,
            default_playlist,
        })
    }

    pub fn rename_library(&self, request: &LibraryRenameRequest) -> Result<Value, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop library rename transaction: {error}")
        })?;

        let normalized_name = normalize_entity_name(&request.name, "Library")?;
        let mut library = load_required_library_value(&transaction, &request.library_id)?;
        let updated_at = current_iso_timestamp();

        set_string_field(&mut library, "name", normalized_name, "desktop library")?;
        set_string_field(&mut library, "updatedAt", updated_at, "desktop library")?;

        upsert_libraries(&transaction, &[library.clone()])?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop library rename: {error}"))?;
        self.mark_catalog_structure_changed()?;

        Ok(library)
    }

    pub fn delete_library(
        &self,
        request: &LibraryDeleteRequest,
    ) -> Result<LibraryDeleteResult, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop library deletion transaction: {error}")
        })?;

        let library = load_required_library_value(&transaction, &request.library_id)?;
        assert_library_can_be_deleted(&library)?;

        let library_playlists = load_library_playlist_values(&transaction, &request.library_id)?;
        let deleted_playlist_ids = library_playlists
            .iter()
            .map(|playlist| required_text_field(playlist, "id", "desktop playlist"))
            .collect::<Result<Vec<_>, _>>()?;
        let deleted_tracks = load_library_track_values(&transaction, &request.library_id)?;
        let deleted_track_ids = deleted_tracks
            .iter()
            .map(|track| required_text_field(track, "id", "desktop track"))
            .collect::<Result<Vec<_>, _>>()?;
        let deleted_relation_ids = load_relation_ids_for_library_delete(
            &transaction,
            &deleted_playlist_ids,
            &deleted_track_ids,
        )?;

        remember_deleted_import_paths_for_tracks(&transaction, &deleted_tracks)?;
        delete_records_in_transaction(
            &transaction,
            "playlist_track_relations",
            &deleted_relation_ids,
            "desktop playlist-track relations",
        )?;
        delete_records_in_transaction(
            &transaction,
            "tracks",
            &deleted_track_ids,
            "desktop tracks",
        )?;
        delete_records_in_transaction(
            &transaction,
            "playlists",
            &deleted_playlist_ids,
            "desktop playlists",
        )?;
        delete_records_in_transaction(
            &transaction,
            "libraries",
            std::slice::from_ref(&request.library_id),
            "desktop libraries",
        )?;

        let remaining_libraries = load_library_values(&transaction)?;
        let reordered_libraries = reorder_libraries(remaining_libraries, &[])?;
        upsert_libraries(&transaction, &reordered_libraries)?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop library deletion: {error}"))?;
        self.mark_catalog_structure_changed()?;

        Ok(LibraryDeleteResult {
            deleted_library_id: request.library_id.clone(),
            deleted_playlist_ids,
            deleted_track_ids,
            deleted_relation_ids,
            fallback_library_id: reordered_libraries
                .first()
                .and_then(|library| library_id(library).ok()),
            libraries: reordered_libraries,
        })
    }

    pub fn reorder_libraries(&self, request: &LibraryReorderRequest) -> Result<Vec<Value>, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop library reorder transaction: {error}")
        })?;

        let current_libraries = load_library_values(&transaction)?;
        let reordered_libraries =
            reorder_libraries(current_libraries, &request.ordered_library_ids)?;

        upsert_libraries(&transaction, &reordered_libraries)?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop library reorder: {error}"))?;
        self.mark_catalog_structure_changed()?;

        Ok(reordered_libraries)
    }

    pub fn create_playlist(&self, request: &PlaylistCreateRequest) -> Result<Value, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop playlist creation transaction: {error}")
        })?;

        ensure_library_exists(&transaction, &request.library_id)?;

        let normalized_name = normalize_entity_name(&request.name, "Playlist")?;
        let order = count_library_playlists(&transaction, &request.library_id)?;
        let playlist = create_user_playlist_value(&request.library_id, &normalized_name, order);

        upsert_playlists(&transaction, std::slice::from_ref(&playlist))?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop playlist creation: {error}"))?;
        self.mark_catalog_structure_changed()?;

        Ok(playlist)
    }

    pub fn rename_playlist(&self, request: &PlaylistRenameRequest) -> Result<Value, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop playlist rename transaction: {error}")
        })?;

        let normalized_name = normalize_entity_name(&request.name, "Playlist")?;
        let mut playlist = load_required_playlist_value(&transaction, &request.playlist_id)?;
        let updated_at = current_iso_timestamp();

        assert_user_playlist(&playlist)?;
        set_string_field(&mut playlist, "name", normalized_name, "desktop playlist")?;
        set_string_field(&mut playlist, "updatedAt", updated_at, "desktop playlist")?;

        upsert_playlists(&transaction, &[playlist.clone()])?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop playlist rename: {error}"))?;
        self.mark_catalog_structure_changed()?;

        Ok(playlist)
    }

    pub fn delete_playlist(
        &self,
        request: &PlaylistDeleteRequest,
    ) -> Result<PlaylistDeleteResult, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop playlist deletion transaction: {error}")
        })?;

        let playlist = load_required_playlist_value(&transaction, &request.playlist_id)?;
        assert_user_playlist(&playlist)?;
        let library_id = required_text_field(&playlist, "libraryId", "desktop playlist")?;
        let relation_values = load_playlist_relation_values(&transaction, &request.playlist_id)?;
        let deleted_relation_ids = relation_values
            .iter()
            .map(|relation| required_text_field(relation, "id", "desktop playlist-track relation"))
            .collect::<Result<Vec<_>, _>>()?;

        delete_records_in_transaction(
            &transaction,
            "playlist_track_relations",
            &deleted_relation_ids,
            "desktop playlist-track relations",
        )?;
        delete_records_in_transaction(
            &transaction,
            "playlists",
            std::slice::from_ref(&request.playlist_id),
            "desktop playlists",
        )?;

        let remaining_playlists = load_library_playlist_values(&transaction, &library_id)?;
        let reordered_playlists = reorder_library_playlists(remaining_playlists, &[])?;

        upsert_playlists(&transaction, &reordered_playlists)?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop playlist deletion: {error}"))?;
        self.mark_catalog_structure_changed()?;

        Ok(PlaylistDeleteResult {
            deleted_playlist_id: request.playlist_id.clone(),
            deleted_relation_ids,
            library_id,
            playlists: reordered_playlists,
        })
    }

    pub fn reorder_playlists(
        &self,
        request: &PlaylistReorderRequest,
    ) -> Result<Vec<Value>, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop playlist reorder transaction: {error}")
        })?;

        ensure_library_exists(&transaction, &request.library_id)?;

        let current_playlists = load_library_playlist_values(&transaction, &request.library_id)?;
        let reordered_playlists =
            reorder_library_playlists(current_playlists, &request.ordered_playlist_ids)?;

        upsert_playlists(&transaction, &reordered_playlists)?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit desktop playlist reorder: {error}"))?;
        self.mark_catalog_structure_changed()?;

        Ok(reordered_playlists)
    }

    pub fn add_track_to_playlist(
        &self,
        request: &PlaylistTrackMutationRequest,
    ) -> Result<PlaylistTrackMutationResult, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop add-track-to-playlist transaction: {error}")
        })?;

        let playlist = load_required_playlist_value(&transaction, &request.playlist_id)?;
        assert_user_playlist(&playlist)?;
        let track = load_required_track_value(&transaction, &request.track_id)?;
        ensure_track_matches_playlist_library(&track, &playlist)?;

        let existing_relations = load_playlist_relation_values(&transaction, &request.playlist_id)?;

        if let Some(existing_relation) = existing_relations
            .iter()
            .find(|relation| {
                relation_track_id(relation).as_deref() == Some(request.track_id.as_str())
            })
            .cloned()
        {
            return Ok(PlaylistTrackMutationResult {
                relation: Some(existing_relation),
                relations: existing_relations,
            });
        }

        let insert_index = request
            .index
            .map(|index| index.min(existing_relations.len()))
            .unwrap_or(existing_relations.len());
        let mut next_relations = existing_relations;

        next_relations.insert(
            insert_index,
            create_playlist_track_relation_value(
                &request.playlist_id,
                &request.track_id,
                insert_index,
                None,
            ),
        );

        let normalized_relations = normalize_playlist_relation_orders(next_relations)?;
        let relation = normalized_relations
            .iter()
            .find(|relation| {
                relation_track_id(relation).as_deref() == Some(request.track_id.as_str())
            })
            .cloned();

        upsert_playlist_track_relations(&transaction, &normalized_relations)?;
        transaction.commit().map_err(|error| {
            format!("Failed to commit desktop add-track-to-playlist transaction: {error}")
        })?;
        self.mark_catalog_structure_changed()?;

        Ok(PlaylistTrackMutationResult {
            relation,
            relations: normalized_relations,
        })
    }

    pub fn remove_track_from_playlist(
        &self,
        request: &PlaylistTrackRemoveRequest,
    ) -> Result<PlaylistTrackRemoveResult, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop remove-track-from-playlist transaction: {error}")
        })?;

        let playlist = load_required_playlist_value(&transaction, &request.playlist_id)?;
        assert_user_playlist(&playlist)?;

        let current_relations = load_playlist_relation_values(&transaction, &request.playlist_id)?;
        let relation_to_delete = current_relations
            .iter()
            .find(|relation| {
                relation_track_id(relation).as_deref() == Some(request.track_id.as_str())
            })
            .cloned();

        let Some(relation_to_delete) = relation_to_delete else {
            return Ok(PlaylistTrackRemoveResult {
                deleted_relation_id: None,
                relations: current_relations,
            });
        };

        let deleted_relation_id =
            required_text_field(&relation_to_delete, "id", "desktop playlist-track relation")?;
        let normalized_relations = normalize_playlist_relation_orders(
            current_relations
                .into_iter()
                .filter(|relation| {
                    relation_id(relation).as_deref() != Some(deleted_relation_id.as_str())
                })
                .collect(),
        )?;

        delete_records_in_transaction(
            &transaction,
            "playlist_track_relations",
            std::slice::from_ref(&deleted_relation_id),
            "desktop playlist-track relations",
        )?;
        upsert_playlist_track_relations(&transaction, &normalized_relations)?;
        transaction.commit().map_err(|error| {
            format!("Failed to commit desktop remove-track-from-playlist transaction: {error}")
        })?;
        self.mark_catalog_structure_changed()?;

        Ok(PlaylistTrackRemoveResult {
            deleted_relation_id: Some(deleted_relation_id),
            relations: normalized_relations,
        })
    }

    pub fn reorder_playlist_tracks(
        &self,
        request: &PlaylistTrackReorderRequest,
    ) -> Result<Vec<Value>, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop playlist-track reorder transaction: {error}")
        })?;

        let playlist = load_required_playlist_value(&transaction, &request.playlist_id)?;
        assert_user_playlist(&playlist)?;

        let current_relations = load_playlist_relation_values(&transaction, &request.playlist_id)?;
        let reordered_relations =
            reorder_playlist_relation_values(current_relations, &request.ordered_track_ids)?;

        upsert_playlist_track_relations(&transaction, &reordered_relations)?;
        transaction.commit().map_err(|error| {
            format!("Failed to commit desktop playlist-track reorder transaction: {error}")
        })?;
        self.mark_catalog_structure_changed()?;

        Ok(reordered_relations)
    }

    pub fn delete_track_from_library(
        &self,
        request: &TrackDeleteRequest,
    ) -> Result<TrackDeleteResult, String> {
        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop delete-track-from-library transaction: {error}")
        })?;

        let track = load_required_track_value(&transaction, &request.track_id)?;
        let library_id = required_text_field(&track, "libraryId", "desktop track")?;
        let deleted_relation_ids =
            load_relation_ids_for_track_delete(&transaction, &request.track_id)?;

        remember_deleted_import_paths_for_tracks(&transaction, std::slice::from_ref(&track))?;
        delete_records_in_transaction(
            &transaction,
            "playlist_track_relations",
            &deleted_relation_ids,
            "desktop playlist-track relations",
        )?;
        delete_records_in_transaction(
            &transaction,
            "tracks",
            std::slice::from_ref(&request.track_id),
            "desktop tracks",
        )?;

        let remaining_tracks = load_library_track_values(&transaction, &library_id)?;
        let reordered_tracks = normalize_library_track_orders(remaining_tracks)?;
        upsert_tracks(&transaction, &reordered_tracks)?;
        transaction.commit().map_err(|error| {
            format!("Failed to commit desktop delete-track-from-library transaction: {error}")
        })?;
        self.mark_catalog_structure_changed()?;

        Ok(TrackDeleteResult {
            deleted_track_id: request.track_id.clone(),
            deleted_relation_ids,
            library_id,
            reordered_tracks,
        })
    }

    pub fn delete_tracks_from_library(
        &self,
        request: &TrackBatchDeleteRequest,
    ) -> Result<TrackBatchDeleteResult, String> {
        let mut normalized_track_ids = Vec::new();
        let mut seen_track_ids = HashSet::new();

        for track_id in &request.track_ids {
            let normalized = track_id.trim();

            if normalized.is_empty() || !seen_track_ids.insert(normalized.to_string()) {
                continue;
            }

            normalized_track_ids.push(normalized.to_string());
        }

        if normalized_track_ids.is_empty() {
            return Ok(TrackBatchDeleteResult::default());
        }

        let mut connection = self.connection()?;
        let transaction = connection.transaction().map_err(|error| {
            format!("Failed to open the desktop batch delete-tracks transaction: {error}")
        })?;

        let mut deleted_tracks = Vec::with_capacity(normalized_track_ids.len());
        let mut deleted_relation_ids = Vec::new();
        let mut library_ids = Vec::new();
        let mut seen_library_ids = HashSet::new();

        for track_id in &normalized_track_ids {
            let track = load_required_track_value(&transaction, track_id)?;
            let library_id = required_text_field(&track, "libraryId", "desktop track")?;

            if seen_library_ids.insert(library_id.clone()) {
                library_ids.push(library_id);
            }

            deleted_relation_ids
                .extend(load_relation_ids_for_track_delete(&transaction, track_id)?);
            deleted_tracks.push(track);
        }

        deleted_relation_ids.sort();
        deleted_relation_ids.dedup();

        remember_deleted_import_paths_for_tracks(&transaction, &deleted_tracks)?;
        delete_records_in_transaction(
            &transaction,
            "playlist_track_relations",
            &deleted_relation_ids,
            "desktop playlist-track relations",
        )?;
        delete_records_in_transaction(
            &transaction,
            "tracks",
            &normalized_track_ids,
            "desktop tracks",
        )?;

        let mut reordered_tracks = Vec::new();

        for library_id in &library_ids {
            let remaining_tracks = load_library_track_values(&transaction, library_id)?;
            let library_reordered_tracks = normalize_library_track_orders(remaining_tracks)?;

            if !library_reordered_tracks.is_empty() {
                upsert_tracks(&transaction, &library_reordered_tracks)?;
                reordered_tracks.extend(library_reordered_tracks);
            }
        }

        transaction.commit().map_err(|error| {
            format!("Failed to commit desktop batch delete-tracks transaction: {error}")
        })?;
        self.mark_catalog_structure_changed()?;

        Ok(TrackBatchDeleteResult {
            deleted_track_ids: normalized_track_ids,
            deleted_relation_ids,
            library_ids,
            reordered_tracks,
        })
    }

    pub fn resolve_navigation_summary(
        &mut self,
        request: &NavigationQueryRequest,
    ) -> Result<NavigationQueryResult, String> {
        let mut connection = self.connection()?;
        self.ensure_catalog_consistency_ready(&mut connection)?;
        resolve_navigation_summary_from_connection(&connection, request)
    }

    pub fn load_bootstrap_snapshot(
        &mut self,
        request: &DesktopBootstrapRequest,
    ) -> Result<DesktopBootstrapSnapshot, String> {
        let total_start = Instant::now();
        let process_start = capture_process_resource_snapshot();
        let mut step_profiles = Vec::new();

        let connection_start = Instant::now();
        let connection_resource_start = capture_process_resource_snapshot();
        let mut connection = self.connection()?;
        let connection_ms = elapsed_ms(connection_start);
        let connection_resource_end = capture_process_resource_snapshot();
        step_profiles.push(build_diagnostic_step_profile(
            "connection",
            connection_ms,
            connection_resource_start.as_ref(),
            connection_resource_end.as_ref(),
        ));

        let revisions_start = Instant::now();
        let revisions_resource_start = capture_process_resource_snapshot();
        let revisions = load_state_revisions_from_connection(&connection)?;
        let revisions_ms = elapsed_ms(revisions_start);
        let revisions_resource_end = capture_process_resource_snapshot();
        step_profiles.push(build_diagnostic_step_profile(
            "revisions",
            revisions_ms,
            revisions_resource_start.as_ref(),
            revisions_resource_end.as_ref(),
        ));

        let preferences_start = Instant::now();
        let preferences_resource_start = capture_process_resource_snapshot();
        let preferences = load_json_from_connection(&connection, PREFERENCES_STATE_KEY)?;
        let preferences_ms = elapsed_ms(preferences_start);
        let preferences_resource_end = capture_process_resource_snapshot();
        step_profiles.push(build_diagnostic_step_profile(
            "preferences",
            preferences_ms,
            preferences_resource_start.as_ref(),
            preferences_resource_end.as_ref(),
        ));

        let session_start = Instant::now();
        let session_resource_start = capture_process_resource_snapshot();
        let session = load_json_from_connection(&connection, SESSION_STATE_KEY)?;
        let session_ms = elapsed_ms(session_start);
        let session_resource_end = capture_process_resource_snapshot();
        step_profiles.push(build_diagnostic_step_profile(
            "session",
            session_ms,
            session_resource_start.as_ref(),
            session_resource_end.as_ref(),
        ));

        let include_catalog_tracks = request.include_catalog_tracks.unwrap_or(false);
        let include_playlist_track_relations =
            request.include_playlist_track_relations.unwrap_or(true);
        let warm_track_query_cache = request.warm_track_query_cache.unwrap_or(true);
        let catalog_cache_ms = 0;
        let catalog_cache_hit = false;
        let catalog_consistency_start = Instant::now();
        let catalog_consistency_resource_start = capture_process_resource_snapshot();
        let ensured_full_catalog = if include_catalog_tracks {
            let snapshot = ensure_catalog_consistency(&mut connection)?;
            let sortable_track_cache = build_sortable_track_cache_from_records(&snapshot.tracks)?;
            *self.track_query_cache.borrow_mut() = Some(sortable_track_cache);
            self.catalog_consistency_checked.set(true);
            Some(snapshot)
        } else {
            ensure_catalog_shell_consistency(&mut connection)?;

            if warm_track_query_cache && self.track_query_cache.borrow().is_none() {
                let sortable_track_cache = load_sortable_track_cache_from_connection(&connection)?;
                *self.track_query_cache.borrow_mut() = Some(sortable_track_cache);
            }

            self.catalog_consistency_checked.set(true);
            None
        };
        let catalog_consistency_ms = elapsed_ms(catalog_consistency_start);
        let catalog_consistency_resource_end = capture_process_resource_snapshot();
        step_profiles.push(build_diagnostic_step_profile(
            "catalogConsistency",
            catalog_consistency_ms,
            catalog_consistency_resource_start.as_ref(),
            catalog_consistency_resource_end.as_ref(),
        ));
        let track_cache_entries = self
            .track_query_cache
            .borrow()
            .as_ref()
            .map(|cache| cache.len())
            .unwrap_or(0);
        let track_cache_warm_ms = if warm_track_query_cache {
            catalog_consistency_ms
        } else {
            0
        };

        let catalog_load_start = Instant::now();
        let catalog_load_resource_start = capture_process_resource_snapshot();
        let catalog = match ensured_full_catalog {
            Some(snapshot) => snapshot,
            None => load_catalog_shell_snapshot_from_connection(
                &connection,
                include_playlist_track_relations,
            )?,
        };
        let catalog_load_ms = elapsed_ms(catalog_load_start);
        let catalog_load_resource_end = capture_process_resource_snapshot();
        step_profiles.push(build_diagnostic_step_profile(
            "catalogLoad",
            catalog_load_ms,
            catalog_load_resource_start.as_ref(),
            catalog_load_resource_end.as_ref(),
        ));
        let catalog_ms = catalog_consistency_ms + catalog_load_ms;
        let catalog_track_count = catalog.tracks.len();
        let catalog_relation_count = catalog.playlist_track_relations.len();

        let history_start = Instant::now();
        let history_resource_start = capture_process_resource_snapshot();
        let history =
            load_recent_history_from_connection(&connection, request.history_limit.unwrap_or(100))?;
        let history_ms = elapsed_ms(history_start);
        let history_resource_end = capture_process_resource_snapshot();
        step_profiles.push(build_diagnostic_step_profile(
            "history",
            history_ms,
            history_resource_start.as_ref(),
            history_resource_end.as_ref(),
        ));

        let navigation_start = Instant::now();
        let navigation_resource_start = capture_process_resource_snapshot();
        let navigation_request = create_navigation_request_from_preferences(preferences.as_ref());
        let navigation =
            resolve_navigation_summary_from_connection(&connection, &navigation_request)?;
        let navigation_ms = elapsed_ms(navigation_start);
        let navigation_resource_end = capture_process_resource_snapshot();
        step_profiles.push(build_diagnostic_step_profile(
            "navigation",
            navigation_ms,
            navigation_resource_start.as_ref(),
            navigation_resource_end.as_ref(),
        ));
        let manifest = self.load_bootstrap_manifest(&revisions);
        let process_end = capture_process_resource_snapshot();

        Ok(DesktopBootstrapSnapshot {
            manifest,
            preferences,
            session,
            catalog,
            history,
            navigation,
            diagnostics: DesktopBootstrapDiagnostics {
                connection_ms,
                revisions_ms,
                preferences_ms,
                session_ms,
                catalog_cache_hit,
                catalog_cache_ms,
                catalog_consistency_ms,
                catalog_load_ms,
                catalog_ms,
                catalog_tracks_included: include_catalog_tracks,
                catalog_track_count,
                catalog_relation_count,
                track_cache_warm_ms,
                track_cache_entries,
                history_ms,
                navigation_ms,
                total_ms: elapsed_ms(total_start),
                process: build_process_resource_diagnostics(
                    process_start.as_ref(),
                    process_end.as_ref(),
                ),
                step_profiles,
            },
        })
    }

    pub fn reset_all_data(&mut self) -> Result<DesktopStateResetResult, String> {
        let mut connection = self.connection()?;
        let preferences = load_json_from_connection(&connection, PREFERENCES_STATE_KEY)?;
        let storage_root = preferences
            .as_ref()
            .and_then(|value| optional_text_field(value, "storageRoot"))
            .filter(|value| !value.trim().is_empty());
        let cleared_managed_storage = storage_root
            .as_deref()
            .map(storage::clear_managed_storage_root)
            .transpose()?
            .flatten();
        let transaction = connection
            .transaction()
            .map_err(|error| format!("Failed to open the desktop reset transaction: {error}"))?;

        transaction
            .execute("DELETE FROM playlist_track_relations", [])
            .map_err(|error| {
                format!("Failed to clear desktop playlist-track relations during reset: {error}")
            })?;
        transaction
            .execute("DELETE FROM tracks", [])
            .map_err(|error| format!("Failed to clear desktop tracks during reset: {error}"))?;
        transaction
            .execute("DELETE FROM playlists", [])
            .map_err(|error| format!("Failed to clear desktop playlists during reset: {error}"))?;
        transaction
            .execute("DELETE FROM libraries", [])
            .map_err(|error| format!("Failed to clear desktop libraries during reset: {error}"))?;
        transaction
            .execute("DELETE FROM playback_history", [])
            .map_err(|error| {
                format!("Failed to clear desktop playback history during reset: {error}")
            })?;
        transaction
            .execute("DELETE FROM playback_history_latest", [])
            .map_err(|error| {
                format!(
                    "Failed to clear desktop playback history latest projection during reset: {error}"
                )
            })?;
        transaction
            .execute("DELETE FROM app_state", [])
            .map_err(|error| format!("Failed to clear desktop app state during reset: {error}"))?;
        transaction
            .commit()
            .map_err(|error| format!("Failed to commit the desktop reset transaction: {error}"))?;

        self.catalog_consistency_checked.set(false);
        self.invalidate_track_query_cache();

        Ok(DesktopStateResetResult {
            managed_storage_deleted: cleared_managed_storage.is_some(),
            managed_storage_path: cleared_managed_storage.map(|path| path.display().to_string()),
        })
    }

    pub fn analyze_storage_usage(&self) -> Result<StorageUsageSnapshot, String> {
        let connection = self.connection()?;
        let preferences = load_json_from_connection(&connection, PREFERENCES_STATE_KEY)?;
        let database_path = self.database_path.as_ref().ok_or_else(|| {
            String::from("Desktop state database was accessed before initialization completed.")
        })?;

        storage_maintenance::analyze_storage_usage(&connection, database_path, preferences.as_ref())
    }

    pub fn collect_storage_garbage(&self) -> Result<StorageGarbageCollectionResult, String> {
        let connection = self.connection()?;
        let preferences = load_json_from_connection(&connection, PREFERENCES_STATE_KEY)?;
        let database_path = self.database_path.as_ref().ok_or_else(|| {
            String::from("Desktop state database was accessed before initialization completed.")
        })?;

        storage_maintenance::collect_storage_garbage(
            &connection,
            database_path,
            preferences.as_ref(),
        )
    }

    pub fn query_collection_tracks(
        &mut self,
        request: &CollectionTrackQueryRequest,
    ) -> Result<QueryTracksResult, String> {
        let total_start = Instant::now();
        let process_start = capture_process_resource_snapshot();
        let mut step_profiles = Vec::new();

        let connection_start = Instant::now();
        let connection_resource_start = capture_process_resource_snapshot();
        let mut connection = self.connection()?;
        let connection_ms = elapsed_ms(connection_start);
        let connection_resource_end = capture_process_resource_snapshot();
        step_profiles.push(build_diagnostic_step_profile(
            "connection",
            connection_ms,
            connection_resource_start.as_ref(),
            connection_resource_end.as_ref(),
        ));

        let catalog_consistency_start = Instant::now();
        let catalog_consistency_resource_start = capture_process_resource_snapshot();
        self.ensure_catalog_consistency_ready(&mut connection)?;
        let catalog_consistency_ms = elapsed_ms(catalog_consistency_start);
        let catalog_consistency_resource_end = capture_process_resource_snapshot();
        step_profiles.push(build_diagnostic_step_profile(
            "catalogConsistency",
            catalog_consistency_ms,
            catalog_consistency_resource_start.as_ref(),
            catalog_consistency_resource_end.as_ref(),
        ));

        query_collection_tracks_from_connection(
            &connection,
            request,
            &self.track_query_cache,
            total_start,
            process_start,
            connection_ms,
            catalog_consistency_ms,
            step_profiles,
        )
    }

    fn load_json(&self, key: &str) -> Result<Option<Value>, String> {
        let connection = self.connection()?;
        load_json_from_connection(&connection, key)
    }

    fn save_json(&self, key: &str, value: &Value) -> Result<(), String> {
        let connection = self.connection()?;
        save_json_to_connection(&connection, key, value)
    }

    fn load_bootstrap_manifest(
        &self,
        revisions: &DesktopStateRevisions,
    ) -> DesktopBootstrapManifest {
        DesktopBootstrapManifest {
            version: String::from(DESKTOP_BOOTSTRAP_MANIFEST_VERSION),
            generated_at: current_iso_timestamp(),
            revisions: revisions.clone(),
            catalog_consistency_checked: self.catalog_consistency_checked.get(),
            track_query_cache_ready: self.track_query_cache.borrow().is_some(),
        }
    }

    fn connection(&self) -> Result<Connection, String> {
        let path = self.database_path.as_ref().ok_or_else(|| {
            String::from("Desktop state database was accessed before initialization completed.")
        })?;

        open_connection(path)
    }

    fn ensure_catalog_consistency_ready(
        &mut self,
        connection: &mut Connection,
    ) -> Result<(), String> {
        if self.catalog_consistency_checked.get() {
            return Ok(());
        }

        ensure_catalog_shell_consistency(connection)?;
        let sortable_track_cache = load_sortable_track_cache_from_connection(connection)?;
        *self.track_query_cache.borrow_mut() = Some(sortable_track_cache);
        self.catalog_consistency_checked.set(true);
        Ok(())
    }

    fn invalidate_track_query_cache(&self) {
        self.track_query_cache.borrow_mut().take();
    }

    fn mark_catalog_structure_changed(&self) -> Result<(), String> {
        self.catalog_consistency_checked.set(false);
        self.invalidate_track_query_cache();
        self.bump_state_revisions(&[REVISION_CATALOG_STATE_KEY, REVISION_NAVIGATION_STATE_KEY])?;
        Ok(())
    }

    fn mark_catalog_projection_changed(&self) -> Result<(), String> {
        self.invalidate_track_query_cache();
        self.bump_state_revisions(&[REVISION_CATALOG_STATE_KEY, REVISION_NAVIGATION_STATE_KEY])?;
        Ok(())
    }

    fn mark_history_changed(&self) -> Result<(), String> {
        self.bump_state_revisions(&[REVISION_HISTORY_STATE_KEY, REVISION_NAVIGATION_STATE_KEY])?;
        Ok(())
    }

    fn mark_preferences_changed(&self) -> Result<(), String> {
        self.bump_state_revisions(&[
            REVISION_PREFERENCES_STATE_KEY,
            REVISION_NAVIGATION_STATE_KEY,
        ])?;
        Ok(())
    }

    fn mark_session_changed(&self) -> Result<(), String> {
        self.bump_state_revisions(&[REVISION_SESSION_STATE_KEY, REVISION_NAVIGATION_STATE_KEY])?;
        Ok(())
    }

    fn bump_state_revisions(&self, keys: &[&str]) -> Result<(), String> {
        if keys.is_empty() {
            return Ok(());
        }

        let connection = self.connection()?;
        bump_state_revisions_in_connection(&connection, keys)
    }
}

fn load_state_revisions_from_connection(
    connection: &Connection,
) -> Result<DesktopStateRevisions, String> {
    Ok(DesktopStateRevisions {
        catalog: load_state_revision_from_connection(connection, REVISION_CATALOG_STATE_KEY)?,
        navigation: load_state_revision_from_connection(connection, REVISION_NAVIGATION_STATE_KEY)?,
        history: load_state_revision_from_connection(connection, REVISION_HISTORY_STATE_KEY)?,
        preferences: load_state_revision_from_connection(
            connection,
            REVISION_PREFERENCES_STATE_KEY,
        )?,
        session: load_state_revision_from_connection(connection, REVISION_SESSION_STATE_KEY)?,
    })
}

fn load_state_revision_from_connection(connection: &Connection, key: &str) -> Result<u64, String> {
    Ok(load_json_from_connection(connection, key)?
        .and_then(|value| value.as_u64())
        .unwrap_or(0))
}

fn clear_legacy_catalog_snapshot_cache(connection: &Connection) -> Result<(), String> {
    connection
        .execute(
            "DELETE FROM app_state WHERE key = ?1",
            [LEGACY_SNAPSHOT_CATALOG_STATE_KEY],
        )
        .map_err(|error| format!("Failed to clear legacy catalog snapshot cache: {error}"))?;
    Ok(())
}

fn bump_state_revisions_in_connection(
    connection: &Connection,
    keys: &[&str],
) -> Result<(), String> {
    let mut seen_keys = HashSet::new();

    for key in keys {
        if !seen_keys.insert(*key) {
            continue;
        }

        let next_revision = load_state_revision_from_connection(connection, key)? + 1;
        save_json_to_connection(connection, key, &Value::from(next_revision))?;
    }

    Ok(())
}

fn open_connection(path: &Path) -> Result<Connection, String> {
    let connection = Connection::open(path).map_err(|error| {
        format!(
            "Failed to open desktop state database '{}': {error}",
            path.display()
        )
    })?;

    connection
        .pragma_update(None, "journal_mode", "WAL")
        .map_err(|error| {
            format!("Failed to enable WAL mode for desktop state database: {error}")
        })?;
    connection
        .pragma_update(None, "foreign_keys", "ON")
        .map_err(|error| {
            format!("Failed to enable foreign keys for desktop state database: {error}")
        })?;

    Ok(connection)
}

fn load_recent_history_from_connection(
    connection: &Connection,
    limit: usize,
) -> Result<Vec<Value>, String> {
    let query_limit = if limit == 0 {
        100_i64
    } else {
        i64::try_from(limit).unwrap_or(i64::MAX)
    };

    load_payloads(
        connection,
        "SELECT payload_json FROM playback_history ORDER BY recorded_at_text DESC, id DESC LIMIT ?1",
        [query_limit],
        "desktop playback history",
    )
}

fn upsert_history_entries(transaction: &Transaction<'_>, entries: &[Value]) -> Result<(), String> {
    if entries.is_empty() {
        return Ok(());
    }

    let mut statement = transaction
        .prepare(
            "INSERT INTO playback_history (id, track_id, recorded_at_text, payload_json, updated_at)
             VALUES (?1, ?2, ?3, ?4, ?5)
             ON CONFLICT(id) DO UPDATE SET
               track_id = excluded.track_id,
               recorded_at_text = excluded.recorded_at_text,
               payload_json = excluded.payload_json,
               updated_at = excluded.updated_at",
        )
        .map_err(|error| format!("Failed to prepare desktop playback history upsert: {error}"))?;
    let mut latest_statement = transaction
        .prepare(
            "INSERT INTO playback_history_latest
               (track_id, latest_history_id, recorded_at_text, updated_at)
             VALUES (?1, ?2, ?3, ?4)
             ON CONFLICT(track_id) DO UPDATE SET
               latest_history_id = excluded.latest_history_id,
               recorded_at_text = excluded.recorded_at_text,
               updated_at = excluded.updated_at
             WHERE excluded.recorded_at_text > playback_history_latest.recorded_at_text
                OR (
                  excluded.recorded_at_text = playback_history_latest.recorded_at_text
                  AND excluded.latest_history_id > playback_history_latest.latest_history_id
                )",
        )
        .map_err(|error| {
            format!("Failed to prepare desktop playback history latest projection upsert: {error}")
        })?;

    for entry in entries {
        let id = required_text_field(entry, "id", "desktop playback history entry")?;
        let track_id = optional_text_field(entry, "trackId");
        let recorded_at =
            required_text_field(entry, "recordedAt", "desktop playback history entry")?;
        let payload_json = serialize_value(entry, "desktop playback history entry")?;
        let updated_at = current_unix_timestamp();

        statement
            .execute(params![id, track_id, recorded_at, payload_json, updated_at])
            .map_err(|error| {
                format!("Failed to upsert desktop playback history entry '{id}': {error}")
            })?;

        if entry
            .get("type")
            .and_then(Value::as_str)
            .unwrap_or("played")
            == "played"
        {
            if let Some(track_id) = track_id
                .as_deref()
                .map(str::trim)
                .filter(|value| !value.is_empty())
            {
                latest_statement
                    .execute(params![track_id, id, recorded_at, updated_at])
                    .map_err(|error| {
                        format!(
                            "Failed to upsert desktop playback history latest projection for '{id}': {error}"
                        )
                    })?;
            }
        }
    }

    Ok(())
}

fn optional_track_source_text(track: &Value, field: &str) -> Option<String> {
    track
        .get("source")
        .and_then(Value::as_object)
        .and_then(|source| source.get(field))
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|text| !text.is_empty())
        .map(String::from)
}

fn optional_track_source_bool(track: &Value, field: &str) -> Option<bool> {
    track
        .get("source")
        .and_then(Value::as_object)
        .and_then(|source| source.get(field))
        .and_then(Value::as_bool)
}

fn required_track_source_text(track: &Value, field: &str) -> Result<String, String> {
    optional_track_source_text(track, field)
        .ok_or_else(|| format!("Prepared desktop track import is missing source.{field}."))
}

fn normalize_import_path(value: &str) -> String {
    value.trim().replace('/', "\\").to_ascii_lowercase()
}

fn normalize_import_directory(value: &str) -> String {
    normalize_import_path(value)
        .trim_end_matches('\\')
        .to_string()
}

fn collect_normalized_scan_directories(directories: &[String]) -> Vec<String> {
    let mut normalized = Vec::new();

    for directory in directories {
        let directory = normalize_import_directory(directory);

        if directory.is_empty()
            || normalized
                .iter()
                .any(|current_directory| current_directory == &directory)
        {
            continue;
        }

        normalized.push(directory);
    }

    normalized
}

fn path_matches_scan_directories(normalized_path: &str, normalized_directories: &[String]) -> bool {
    normalized_directories.is_empty()
        || normalized_directories.iter().any(|directory| {
            normalized_path == directory
                || normalized_path
                    .strip_prefix(directory)
                    .is_some_and(|suffix| suffix.starts_with('\\'))
        })
}

fn is_local_indexed_track(track: &Value) -> bool {
    optional_track_source_text(track, "kind")
        .as_deref()
        .is_some_and(|kind| kind == "native-file")
        && optional_track_source_bool(track, "indexed").unwrap_or(false)
}

fn register_import_path_keys(keys: &mut HashSet<String>, track: &Value) {
    if let Some(source_path) = optional_track_source_text(track, "path") {
        let normalized = normalize_import_path(&source_path);

        if !normalized.is_empty() {
            keys.insert(normalized);
        }
    }

    if let Some(original_path) = optional_track_source_text(track, "originPath") {
        let normalized = normalize_import_path(&original_path);

        if !normalized.is_empty() {
            keys.insert(normalized);
        }
    }
}

fn unregister_import_path_keys(keys: &mut HashSet<String>, track: &Value) {
    if let Some(source_path) = optional_track_source_text(track, "path") {
        let normalized = normalize_import_path(&source_path);

        if !normalized.is_empty() {
            keys.remove(&normalized);
        }
    }

    if let Some(original_path) = optional_track_source_text(track, "originPath") {
        let normalized = normalize_import_path(&original_path);

        if !normalized.is_empty() {
            keys.remove(&normalized);
        }
    }
}

fn register_import_path_keys_for_values(
    keys: &mut HashSet<String>,
    normalized_source_path: &str,
    normalized_original_path: &str,
) {
    if !normalized_source_path.is_empty() {
        keys.insert(String::from(normalized_source_path));
    }

    if !normalized_original_path.is_empty() {
        keys.insert(String::from(normalized_original_path));
    }
}

fn collect_import_path_keys(records: &[Value]) -> HashSet<String> {
    let mut keys = HashSet::new();

    for record in records {
        register_import_path_keys(&mut keys, record);
    }

    keys
}

fn matches_existing_import_path(
    known_paths: &HashSet<String>,
    normalized_source_path: &str,
    normalized_original_path: &str,
) -> bool {
    (!normalized_source_path.is_empty() && known_paths.contains(normalized_source_path))
        || (!normalized_original_path.is_empty() && known_paths.contains(normalized_original_path))
}

fn load_deleted_import_path_keys(transaction: &Transaction<'_>) -> Result<HashSet<String>, String> {
    let serialized = transaction
        .query_row(
            "SELECT value_json FROM app_state WHERE key = ?1",
            [DELETED_IMPORT_PATHS_STATE_KEY],
            |row| row.get::<_, String>(0),
        )
        .optional()
        .map_err(|error| {
            format!("Failed to read desktop deleted import path tombstones: {error}")
        })?;
    let Some(serialized) = serialized else {
        return Ok(HashSet::new());
    };
    let value = deserialize_value(&serialized, "desktop deleted import path tombstones")?;
    let keys = value
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(Value::as_str)
        .map(normalize_import_path)
        .filter(|path| !path.is_empty())
        .collect::<HashSet<_>>();

    Ok(keys)
}

fn save_deleted_import_path_keys(
    transaction: &Transaction<'_>,
    keys: &HashSet<String>,
) -> Result<(), String> {
    let mut ordered_keys = keys.iter().cloned().collect::<Vec<_>>();
    ordered_keys.sort();
    let value = Value::Array(ordered_keys.into_iter().map(Value::String).collect());
    let serialized = serialize_value(&value, "desktop deleted import path tombstones")?;
    let updated_at = current_unix_timestamp();

    transaction
        .execute(
            "INSERT INTO app_state (key, value_json, updated_at)
             VALUES (?1, ?2, ?3)
             ON CONFLICT(key) DO UPDATE SET
               value_json = excluded.value_json,
               updated_at = excluded.updated_at",
            params![DELETED_IMPORT_PATHS_STATE_KEY, serialized, updated_at],
        )
        .map_err(|error| {
            format!("Failed to save desktop deleted import path tombstones: {error}")
        })?;

    Ok(())
}

fn remember_deleted_import_paths_for_tracks(
    transaction: &Transaction<'_>,
    tracks: &[Value],
) -> Result<(), String> {
    if tracks.is_empty() {
        return Ok(());
    }

    let mut keys = load_deleted_import_path_keys(transaction)?;
    let previous_len = keys.len();

    for track in tracks {
        register_import_path_keys(&mut keys, track);
    }

    if keys.len() != previous_len {
        save_deleted_import_path_keys(transaction, &keys)?;
    }

    Ok(())
}

fn clear_deleted_import_paths_for_tracks(
    transaction: &Transaction<'_>,
    tracks: &[Value],
) -> Result<(), String> {
    if tracks.is_empty() {
        return Ok(());
    }

    let mut keys = load_deleted_import_path_keys(transaction)?;
    let previous_len = keys.len();

    for track in tracks {
        unregister_import_path_keys(&mut keys, track);
    }

    if keys.len() != previous_len {
        save_deleted_import_path_keys(transaction, &keys)?;
    }

    Ok(())
}

fn normalize_prepared_track_import(
    track: &Value,
    library_id: &str,
    library_order: usize,
) -> Result<Value, String> {
    let mut next_track = track
        .as_object()
        .cloned()
        .ok_or_else(|| String::from("Prepared desktop track import payload must be an object."))?;
    let source_path = required_track_source_text(track, "path")?;
    let original_path =
        optional_track_source_text(track, "originPath").unwrap_or_else(|| source_path.clone());

    next_track.insert(
        String::from("libraryId"),
        Value::String(String::from(library_id)),
    );
    next_track.insert(String::from("libraryOrder"), json!(library_order));

    if normalized_optional_text(next_track.get("importedAt").and_then(Value::as_str)).is_none() {
        next_track.insert(
            String::from("importedAt"),
            Value::String(current_iso_timestamp()),
        );
    }

    let source = next_track
        .entry(String::from("source"))
        .or_insert_with(|| json!({}));

    if let Some(source_object) = source.as_object_mut() {
        source_object.insert(
            String::from("kind"),
            Value::String(String::from("native-file")),
        );
        source_object.insert(String::from("path"), Value::String(source_path));
        source_object.insert(String::from("originPath"), Value::String(original_path));
        source_object.insert(String::from("indexed"), Value::Bool(true));
    }

    Ok(Value::Object(next_track))
}

fn normalize_entity_name(value: &str, fallback_label: &str) -> Result<String, String> {
    let trimmed = value.trim();

    if trimmed.is_empty() {
        Err(format!("{fallback_label} name is required."))
    } else {
        Ok(String::from(trimmed))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{env, fs};
    use uuid::Uuid;

    fn temp_store(label: &str) -> (DesktopStateStore, PathBuf) {
        let root = env::temp_dir().join(format!("ofplayer-state-{label}-{}", Uuid::new_v4()));
        fs::create_dir_all(&root).expect("test state root should be created");
        let database_path = root.join("desktop-state.sqlite3");
        let connection = open_connection(&database_path).expect("test database should open");
        initialize_schema(&connection).expect("test schema should initialize");
        drop(connection);

        (
            DesktopStateStore {
                database_path: Some(database_path),
                catalog_consistency_checked: Cell::new(false),
                track_query_cache: RefCell::new(None),
            },
            root,
        )
    }

    fn cleanup(root: &Path) {
        let _ = fs::remove_dir_all(root);
    }

    fn prepared_indexed_track(id: &str, library_id: &str, source_path: &Path) -> Value {
        let file_name = source_path
            .file_name()
            .map(|name| name.to_string_lossy().to_string())
            .unwrap_or_else(|| String::from("track.mp3"));
        let title = source_path
            .file_stem()
            .map(|name| name.to_string_lossy().to_string())
            .unwrap_or_else(|| String::from("Track"));
        let source_path = source_path.to_string_lossy().to_string();

        json!({
            "id": id,
            "libraryId": library_id,
            "libraryOrder": 0,
            "isFavorite": false,
            "title": title,
            "artist": "",
            "albumArtist": "",
            "album": "",
            "genre": "",
            "year": 0,
            "trackNumber": 0,
            "trackTotal": 0,
            "discNumber": 0,
            "discTotal": 0,
            "composer": "",
            "lyricist": "",
            "comment": "",
            "displayTitle": title,
            "fileName": file_name,
            "fileSize": 1,
            "size": 1,
            "duration": 1.0,
            "format": "MP3",
            "bitrate": 0,
            "sampleRate": 0,
            "bitDepth": 0,
            "artwork": "",
            "mimeType": "audio/mpeg",
            "importedAt": "2026-01-01T00:00:00Z",
            "metadataVersion": 1,
            "source": {
                "kind": "native-file",
                "path": source_path,
                "originPath": source_path,
                "indexed": true
            }
        })
    }

    fn deleted_import_path_keys(store: &DesktopStateStore) -> HashSet<String> {
        let mut connection = store.connection().expect("test database should connect");
        let transaction = connection
            .transaction()
            .expect("test deleted import path transaction should open");
        let keys = load_deleted_import_path_keys(&transaction)
            .expect("deleted import path tombstones should load");

        transaction
            .commit()
            .expect("test deleted import path transaction should commit");
        keys
    }

    #[test]
    fn manual_import_filter_can_recover_deleted_source_tombstones() {
        let (store, root) = temp_store("manual-import-tombstone-recovery");
        let music_root = root.join("music");
        fs::create_dir_all(&music_root).expect("music root should be created");
        let source_path = music_root.join("silksong.flac");
        fs::write(&source_path, b"source").expect("source file should be writable");
        let source_path_text = source_path.to_string_lossy().to_string();

        let library = store
            .create_library(&LibraryCreateRequest {
                name: String::from("Library"),
            })
            .expect("library should be created")
            .library;
        let library_id = required_text_field(&library, "id", "test library").unwrap();

        store
            .import_library_prepared_tracks(&LibraryPreparedTrackImportRequest {
                library_id: library_id.clone(),
                tracks: vec![prepared_indexed_track(
                    "deleted-track",
                    &library_id,
                    &source_path,
                )],
            })
            .expect("indexed track should import");
        store
            .delete_track_from_library(&TrackDeleteRequest {
                track_id: String::from("deleted-track"),
            })
            .expect("track should delete from the library");

        let deleted_keys = deleted_import_path_keys(&store);
        assert!(deleted_keys.contains(&normalize_import_path(&source_path_text)));

        let candidate_file = LibraryImportFileInput {
            source_path: source_path_text.clone(),
            file_name: Some(String::from("silksong.flac")),
            original_path: None,
        };
        let automatic_candidates = store
            .filter_library_import_candidates(&LibraryImportCandidatesRequest {
                library_id: library_id.clone(),
                files: vec![candidate_file.clone()],
                respect_deleted_import_paths: Some(true),
            })
            .expect("automatic candidate filter should run");

        assert!(automatic_candidates.is_empty());

        let manual_candidates = store
            .filter_library_import_candidates(&LibraryImportCandidatesRequest {
                library_id: library_id.clone(),
                files: vec![candidate_file],
                respect_deleted_import_paths: Some(false),
            })
            .expect("manual candidate filter should run");

        assert_eq!(manual_candidates.len(), 1);
        assert_eq!(manual_candidates[0].source_path, source_path_text);

        let mut recovered_track =
            prepared_indexed_track("recovered-track", "unused-library-id", &source_path);
        recovered_track
            .as_object_mut()
            .expect("prepared track should be an object")
            .insert(
                String::from("artwork"),
                Value::String(String::from("https://example.test/recovered-cover.jpg")),
            );

        store
            .import_library_prepared_tracks(&LibraryPreparedTrackImportRequest {
                library_id,
                tracks: vec![recovered_track],
            })
            .expect("manual recovery import should persist");

        let cleared_keys = deleted_import_path_keys(&store);
        assert!(!cleared_keys.contains(&normalize_import_path(&source_path_text)));
        let recovered_track = store
            .get_track("recovered-track", true)
            .unwrap()
            .expect("recovered track should exist");
        assert_eq!(
            optional_text_field(&recovered_track, "artwork").as_deref(),
            Some("https://example.test/recovered-cover.jpg")
        );

        cleanup(&root);
    }

    #[test]
    fn local_index_invalidation_removes_deleted_sources_and_playlist_relations() {
        let (store, root) = temp_store("local-index-invalidation");
        let music_root = root.join("music");
        fs::create_dir_all(&music_root).expect("music root should be created");
        let present_path = music_root.join("present.mp3");
        let missing_path = music_root.join("missing.mp3");
        fs::write(&present_path, b"present").expect("present source should be writable");

        let library = store
            .create_library(&LibraryCreateRequest {
                name: String::from("Library"),
            })
            .expect("library should be created")
            .library;
        let library_id = required_text_field(&library, "id", "test library").unwrap();
        let playlist = store
            .create_playlist(&PlaylistCreateRequest {
                library_id: library_id.clone(),
                name: String::from("Pinned"),
            })
            .expect("playlist should be created");
        let playlist_id = required_text_field(&playlist, "id", "test playlist").unwrap();

        store
            .import_library_prepared_tracks(&LibraryPreparedTrackImportRequest {
                library_id: library_id.clone(),
                tracks: vec![
                    prepared_indexed_track("present-track", &library_id, &present_path),
                    prepared_indexed_track("missing-track", &library_id, &missing_path),
                ],
            })
            .expect("indexed tracks should import");
        store
            .add_track_to_playlist(&PlaylistTrackMutationRequest {
                playlist_id,
                track_id: String::from("missing-track"),
                index: None,
            })
            .expect("playlist relation should be created");

        let result = store
            .invalidate_missing_local_indexed_tracks(&LocalIndexInvalidationRequest {
                library_id: library_id.clone(),
                directories: vec![music_root.to_string_lossy().to_string()],
                discovered_paths: vec![present_path.to_string_lossy().to_string()],
            })
            .expect("missing indexed source should invalidate");

        assert_eq!(result.invalidated_track_ids, vec!["missing-track"]);
        assert_eq!(result.invalidated_relation_ids.len(), 1);
        assert_eq!(result.reordered_tracks.len(), 1);
        assert_eq!(
            required_text_field(&result.reordered_tracks[0], "id", "remaining track").unwrap(),
            "present-track"
        );
        assert!(store.get_track("missing-track", false).unwrap().is_none());
        assert!(store.get_track("present-track", false).unwrap().is_some());

        cleanup(&root);
    }

    #[test]
    fn album_artwork_snapshot_key_is_scoped_per_library() {
        let first_library_track = json!({
            "libraryId": "library-a",
            "album": "Shared Album",
            "albumArtist": "Shared Artist",
            "artist": "Track Artist"
        });
        let second_library_track = json!({
            "libraryId": "library-b",
            "album": "Shared Album",
            "albumArtist": "Shared Artist",
            "artist": "Track Artist"
        });

        assert_ne!(
            track_album_artwork_key(&first_library_track),
            track_album_artwork_key(&second_library_track)
        );
    }
}
