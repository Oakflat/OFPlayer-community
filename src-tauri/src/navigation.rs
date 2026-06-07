use crate::{
    db_helpers::{
        elapsed_ms, load_json_from_connection, load_track_id_query, nonnegative_i64_to_u64,
        optional_number_as_f64, optional_number_as_u64, optional_text_field,
        required_boolean_field, required_text_field,
    },
    desktop_types::{
        CollectionTrackQueryRequest, NavigationQueryDiagnostics, NavigationQueryRequest,
        NavigationQueryResult,
    },
    diagnostics::{
        build_diagnostic_step_profile, build_process_resource_diagnostics, DiagnosticStepProfile,
        ProcessResourceSnapshot,
    },
    session_ops::SESSION_STATE_KEY,
    sorting::{
        query_tracks, QueryTrackRow, QueryTracksDiagnostics, QueryTracksRequest, QueryTracksResult,
        SortableTrack,
    },
};
use rusqlite::{params, Connection};
use serde_json::Value;
use std::{
    cell::RefCell,
    collections::{BTreeSet, HashMap, HashSet},
    time::Instant,
};

const PLAYLIST_COLLECTION_PREFIX: &str = "playlist:";
const VIEW_COLLECTION_PREFIX: &str = "view:";
const SYSTEM_PLAYLIST_ALL_TRACKS_KEY: &str = "all-tracks";
const SMART_VIEW_RECENT_IMPORTS: &str = "recent-imports";
const SMART_VIEW_RECENTLY_PLAYED: &str = "all-plays";
const SMART_VIEW_FAVORITES: &str = "all-favorites";
const SMART_VIEW_CURRENT_QUEUE: &str = "current-queue";
const SMART_VIEW_ALBUMS: &str = "albums";
const SMART_VIEW_ARTISTS: &str = "artists";

#[derive(Debug, Clone)]
struct LibraryHeader {
    id: String,
}

#[derive(Debug, Clone)]
struct PlaylistHeader {
    id: String,
    system_key: Option<String>,
}

#[derive(Debug, Clone, Default)]
struct SortableTrackLoadDiagnostics {
    payload_query_ms: u64,
    payload_deserialize_ms: u64,
    project_ms: u64,
    cache_used: bool,
    cache_build_ms: u64,
    cache_entries: usize,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum CollectionRefKind<'a> {
    Playlist(&'a str),
    View(&'a str),
    Invalid,
}

pub(crate) fn create_navigation_request_from_preferences(
    preferences: Option<&Value>,
) -> NavigationQueryRequest {
    NavigationQueryRequest {
        active_library: preferences.and_then(|value| optional_text_field(value, "activeLibrary")),
        active_collection_ref: preferences
            .and_then(|value| optional_text_field(value, "activeCollection")),
    }
}

pub(crate) fn resolve_navigation_summary_from_connection(
    connection: &Connection,
    request: &NavigationQueryRequest,
) -> Result<NavigationQueryResult, String> {
    let total_start = Instant::now();
    let process_start = crate::diagnostics::capture_process_resource_snapshot();
    let mut step_profiles = Vec::new();
    let libraries = load_library_headers(connection)?;

    if libraries.is_empty() {
        return Ok(NavigationQueryResult::default());
    }

    let library_counts_start = Instant::now();
    let library_counts_resource_start = crate::diagnostics::capture_process_resource_snapshot();
    let library_track_counts = load_library_track_counts(connection)?;
    let library_counts_ms = elapsed_ms(library_counts_start);
    let library_counts_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "libraryCounts",
        library_counts_ms,
        library_counts_resource_start.as_ref(),
        library_counts_resource_end.as_ref(),
    ));
    let active_library = resolve_active_library_id(&libraries, request.active_library.as_deref());
    let Some(active_library_id) = active_library.clone() else {
        let process_end = crate::diagnostics::capture_process_resource_snapshot();
        return Ok(NavigationQueryResult {
            active_library,
            library_track_counts,
            diagnostics: NavigationQueryDiagnostics {
                library_counts_ms,
                total_ms: elapsed_ms(total_start),
                process: build_process_resource_diagnostics(
                    process_start.as_ref(),
                    process_end.as_ref(),
                ),
                step_profiles,
                ..NavigationQueryDiagnostics::default()
            },
            ..NavigationQueryResult::default()
        });
    };

    let collection_ids_start = Instant::now();
    let collection_ids_resource_start = crate::diagnostics::capture_process_resource_snapshot();
    let library_track_ids = load_library_track_ids_by_order(connection, &active_library_id)?;
    let library_track_id_set = library_track_ids.iter().cloned().collect::<HashSet<_>>();
    let recent_import_track_ids =
        load_library_track_ids_by_import_date(connection, &active_library_id)?;
    let favorite_track_ids = load_favorite_track_ids(connection, &active_library_id)?;
    let recent_history_track_count =
        load_recently_played_track_count(connection, &active_library_id)?;
    let queue_track_ids = load_current_queue_track_ids(connection, &library_track_id_set)?;
    let album_group_count = load_album_group_count(connection, &active_library_id)?;
    let artist_group_count = load_artist_group_count(connection, &active_library_id)?;
    let playlists = load_playlist_headers_for_library(connection, &active_library_id)?;
    let collection_ids_ms = elapsed_ms(collection_ids_start);
    let collection_ids_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "collectionIds",
        collection_ids_ms,
        collection_ids_resource_start.as_ref(),
        collection_ids_resource_end.as_ref(),
    ));

    let playlist_counts_start = Instant::now();
    let playlist_counts_resource_start = crate::diagnostics::capture_process_resource_snapshot();
    let mut playlist_track_counts = HashMap::new();

    for playlist in &playlists {
        let count = if playlist.system_key.as_deref() == Some(SYSTEM_PLAYLIST_ALL_TRACKS_KEY) {
            library_track_ids.len()
        } else {
            load_playlist_track_ids(connection, &playlist.id, &library_track_id_set)?.len()
        };

        playlist_track_counts.insert(playlist.id.clone(), count);
    }
    let playlist_counts_ms = elapsed_ms(playlist_counts_start);
    let playlist_counts_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "playlistCounts",
        playlist_counts_ms,
        playlist_counts_resource_start.as_ref(),
        playlist_counts_resource_end.as_ref(),
    ));

    let smart_counts_start = Instant::now();
    let smart_counts_resource_start = crate::diagnostics::capture_process_resource_snapshot();
    let smart_collection_counts = HashMap::from([
        (
            String::from(SMART_VIEW_RECENT_IMPORTS),
            recent_import_track_ids.len(),
        ),
        (
            String::from(SMART_VIEW_RECENTLY_PLAYED),
            recent_history_track_count,
        ),
        (String::from(SMART_VIEW_FAVORITES), favorite_track_ids.len()),
        (
            String::from(SMART_VIEW_CURRENT_QUEUE),
            queue_track_ids.len(),
        ),
        (String::from(SMART_VIEW_ALBUMS), album_group_count),
        (String::from(SMART_VIEW_ARTISTS), artist_group_count),
    ]);
    let smart_counts_ms = elapsed_ms(smart_counts_start);
    let smart_counts_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "smartCounts",
        smart_counts_ms,
        smart_counts_resource_start.as_ref(),
        smart_counts_resource_end.as_ref(),
    ));

    let default_collection_key = resolve_default_collection_key(&playlists);
    let active_collection_key = resolve_active_collection_key(
        request.active_collection_ref.as_deref(),
        &playlists,
        default_collection_key.as_deref(),
    );
    let process_end = crate::diagnostics::capture_process_resource_snapshot();
    Ok(NavigationQueryResult {
        active_library: Some(active_library_id),
        active_collection_key,
        library_track_counts,
        playlist_track_counts,
        smart_collection_counts,
        diagnostics: NavigationQueryDiagnostics {
            library_counts_ms,
            collection_ids_ms,
            playlist_counts_ms,
            smart_counts_ms,
            total_ms: elapsed_ms(total_start),
            process: build_process_resource_diagnostics(
                process_start.as_ref(),
                process_end.as_ref(),
            ),
            step_profiles,
            ..NavigationQueryDiagnostics::default()
        },
    })
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn query_collection_tracks_from_connection(
    connection: &Connection,
    request: &CollectionTrackQueryRequest,
    track_query_cache: &RefCell<Option<HashMap<String, SortableTrack>>>,
    total_start: Instant,
    process_start: Option<ProcessResourceSnapshot>,
    connection_ms: u64,
    catalog_consistency_ms: u64,
    mut step_profiles: Vec<DiagnosticStepProfile>,
) -> Result<QueryTracksResult, String> {
    let collection_resolve_start = Instant::now();
    let library_headers_start = Instant::now();
    let library_headers_resource_start = crate::diagnostics::capture_process_resource_snapshot();
    let libraries = load_library_headers(connection)?;
    let library_headers_ms = elapsed_ms(library_headers_start);
    let library_headers_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "libraryHeaders",
        library_headers_ms,
        library_headers_resource_start.as_ref(),
        library_headers_resource_end.as_ref(),
    ));
    let Some(active_library_id) =
        resolve_active_library_id(&libraries, request.active_library.as_deref())
    else {
        let mut diagnostics = QueryTracksDiagnostics::default();
        let process_end = crate::diagnostics::capture_process_resource_snapshot();
        diagnostics.connection_ms = connection_ms;
        diagnostics.catalog_consistency_ms = catalog_consistency_ms;
        diagnostics.library_headers_ms = library_headers_ms;
        diagnostics.total_ms = elapsed_ms(total_start);
        diagnostics.process =
            build_process_resource_diagnostics(process_start.as_ref(), process_end.as_ref());
        diagnostics.step_profiles = step_profiles;
        return Ok(QueryTracksResult {
            total_count: 0,
            collection_total_count: 0,
            offset: 0,
            available_formats: Vec::new(),
            track_ids: Vec::new(),
            rows: Vec::new(),
            diagnostics,
        });
    };

    let playlist_headers_start = Instant::now();
    let playlist_headers_resource_start = crate::diagnostics::capture_process_resource_snapshot();
    let playlists = load_playlist_headers_for_library(connection, &active_library_id)?;
    let playlist_headers_ms = elapsed_ms(playlist_headers_start);
    let playlist_headers_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "playlistHeaders",
        playlist_headers_ms,
        playlist_headers_resource_start.as_ref(),
        playlist_headers_resource_end.as_ref(),
    ));
    let default_collection_key = resolve_default_collection_key(&playlists);
    let active_collection_key = resolve_active_collection_key(
        request.active_collection_ref.as_deref(),
        &playlists,
        default_collection_key.as_deref(),
    );

    if matches!(
        active_collection_key.as_deref().map(parse_collection_ref),
        Some(CollectionRefKind::View(SMART_VIEW_RECENTLY_PLAYED))
    ) {
        let collection_resolve_ms = elapsed_ms(collection_resolve_start);
        return query_recently_played_tracks_from_connection(
            connection,
            request,
            &active_library_id,
            total_start,
            process_start,
            connection_ms,
            catalog_consistency_ms,
            library_headers_ms,
            playlist_headers_ms,
            collection_resolve_ms,
            step_profiles,
        );
    }

    let library_track_ids_start = Instant::now();
    let library_track_ids_resource_start = crate::diagnostics::capture_process_resource_snapshot();
    let library_track_ids = load_library_track_ids_by_order(connection, &active_library_id)?;
    let library_track_ids_ms = elapsed_ms(library_track_ids_start);
    let library_track_ids_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "libraryTrackIds",
        library_track_ids_ms,
        library_track_ids_resource_start.as_ref(),
        library_track_ids_resource_end.as_ref(),
    ));
    let library_track_count = library_track_ids.len();
    let library_track_id_set = library_track_ids.iter().cloned().collect::<HashSet<_>>();
    let collection_track_ids_start = Instant::now();
    let collection_track_ids_resource_start =
        crate::diagnostics::capture_process_resource_snapshot();
    let collection_track_ids = match active_collection_key.as_deref().map(parse_collection_ref) {
        Some(CollectionRefKind::Playlist(playlist_id)) => {
            let playlist = playlists.iter().find(|playlist| playlist.id == playlist_id);

            if playlist.and_then(|playlist| playlist.system_key.as_deref())
                == Some(SYSTEM_PLAYLIST_ALL_TRACKS_KEY)
            {
                library_track_ids.clone()
            } else {
                load_playlist_track_ids(connection, playlist_id, &library_track_id_set)?
            }
        }
        Some(CollectionRefKind::View(view_key)) => match view_key {
            SMART_VIEW_RECENT_IMPORTS => {
                load_library_track_ids_by_import_date(connection, &active_library_id)?
            }
            SMART_VIEW_RECENTLY_PLAYED => {
                load_recently_played_track_ids(connection, &active_library_id)?
            }
            SMART_VIEW_FAVORITES => load_favorite_track_ids(connection, &active_library_id)?,
            SMART_VIEW_CURRENT_QUEUE => {
                load_current_queue_track_ids(connection, &library_track_id_set)?
            }
            SMART_VIEW_ALBUMS | SMART_VIEW_ARTISTS => library_track_ids.clone(),
            _ => Vec::new(),
        },
        _ => library_track_ids.clone(),
    };
    let collection_track_ids_ms = elapsed_ms(collection_track_ids_start);
    let collection_track_ids_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "collectionTrackIds",
        collection_track_ids_ms,
        collection_track_ids_resource_start.as_ref(),
        collection_track_ids_resource_end.as_ref(),
    ));
    let collection_track_count = collection_track_ids.len();
    let collection_resolve_ms = elapsed_ms(collection_resolve_start);

    let sortable_tracks_start = Instant::now();
    let sortable_tracks_resource_start = crate::diagnostics::capture_process_resource_snapshot();
    let (sortable_tracks, sortable_track_diagnostics) =
        load_sortable_tracks_for_ids(connection, &collection_track_ids, track_query_cache)?;
    let sortable_tracks_ms = elapsed_ms(sortable_tracks_start);
    let sortable_tracks_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "sortableTracks",
        sortable_tracks_ms,
        sortable_tracks_resource_start.as_ref(),
        sortable_tracks_resource_end.as_ref(),
    ));

    let query_tracks_start = Instant::now();
    let query_tracks_resource_start = crate::diagnostics::capture_process_resource_snapshot();
    let mut result = query_tracks(QueryTracksRequest {
        search_query: request.search_query.clone(),
        type_filter: request.type_filter.clone(),
        sort_option: request.sort_option.clone(),
        offset: request.offset,
        limit: request.limit,
        include_track_ids: request.include_track_ids,
        tracks: sortable_tracks,
    })?;
    let query_tracks_ms = elapsed_ms(query_tracks_start);
    let query_tracks_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "queryTracks",
        query_tracks_ms,
        query_tracks_resource_start.as_ref(),
        query_tracks_resource_end.as_ref(),
    ));
    step_profiles.extend(result.diagnostics.step_profiles.clone());
    let process_end = crate::diagnostics::capture_process_resource_snapshot();
    result.diagnostics.connection_ms = connection_ms;
    result.diagnostics.catalog_consistency_ms = catalog_consistency_ms;
    result.diagnostics.library_headers_ms = library_headers_ms;
    result.diagnostics.playlist_headers_ms = playlist_headers_ms;
    result.diagnostics.library_track_ids_ms = library_track_ids_ms;
    result.diagnostics.collection_track_ids_ms = collection_track_ids_ms;
    result.diagnostics.library_track_count = library_track_count;
    result.diagnostics.collection_track_count = collection_track_count;
    result.diagnostics.collection_resolve_ms = collection_resolve_ms;
    result.diagnostics.sortable_tracks_ms = sortable_tracks_ms;
    result.diagnostics.payload_query_ms = sortable_track_diagnostics.payload_query_ms;
    result.diagnostics.payload_deserialize_ms = sortable_track_diagnostics.payload_deserialize_ms;
    result.diagnostics.sortable_project_ms = sortable_track_diagnostics.project_ms;
    result.diagnostics.track_cache_used = sortable_track_diagnostics.cache_used;
    result.diagnostics.track_cache_build_ms = sortable_track_diagnostics.cache_build_ms;
    result.diagnostics.track_cache_entries = sortable_track_diagnostics.cache_entries;
    result.diagnostics.total_ms = elapsed_ms(total_start);
    result.diagnostics.process =
        build_process_resource_diagnostics(process_start.as_ref(), process_end.as_ref());
    result.diagnostics.step_profiles = step_profiles;
    Ok(result)
}

#[allow(clippy::too_many_arguments)]
fn query_recently_played_tracks_from_connection(
    connection: &Connection,
    request: &CollectionTrackQueryRequest,
    library_id: &str,
    total_start: Instant,
    process_start: Option<ProcessResourceSnapshot>,
    connection_ms: u64,
    catalog_consistency_ms: u64,
    library_headers_ms: u64,
    playlist_headers_ms: u64,
    collection_resolve_ms: u64,
    mut step_profiles: Vec<DiagnosticStepProfile>,
) -> Result<QueryTracksResult, String> {
    let include_track_ids = request.include_track_ids.unwrap_or(false);
    let search_query = normalize_recent_query(&request.search_query);
    let search_pattern = format!("%{}%", escape_sql_like(&search_query));
    let type_filter = normalize_recent_type_filter(&request.type_filter);
    let type_file_pattern = format!("%.{}", escape_sql_like(&type_filter));

    let library_track_count_start = Instant::now();
    let library_track_count_resource_start =
        crate::diagnostics::capture_process_resource_snapshot();
    let library_track_count = load_library_track_count(connection, library_id)?;
    let library_track_count_ms = elapsed_ms(library_track_count_start);
    let library_track_count_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "libraryTrackCount",
        library_track_count_ms,
        library_track_count_resource_start.as_ref(),
        library_track_count_resource_end.as_ref(),
    ));

    let collection_count_start = Instant::now();
    let collection_count_resource_start = crate::diagnostics::capture_process_resource_snapshot();
    let collection_track_count = load_recently_played_collection_count(connection, library_id)?;
    let collection_track_ids_ms = elapsed_ms(collection_count_start);
    let collection_count_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "recentPlayedCollectionCount",
        collection_track_ids_ms,
        collection_count_resource_start.as_ref(),
        collection_count_resource_end.as_ref(),
    ));

    let formats_start = Instant::now();
    let formats_resource_start = crate::diagnostics::capture_process_resource_snapshot();
    let available_formats = load_recently_played_available_formats(connection, library_id)?;
    let formats_ms = elapsed_ms(formats_start);
    let formats_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "recentPlayedFormats",
        formats_ms,
        formats_resource_start.as_ref(),
        formats_resource_end.as_ref(),
    ));

    let filter_start = Instant::now();
    let filter_resource_start = crate::diagnostics::capture_process_resource_snapshot();
    let total_count = count_recently_played_filtered_tracks(
        connection,
        library_id,
        &search_query,
        &search_pattern,
        &type_filter,
        &type_file_pattern,
    )?;
    let filter_ms = elapsed_ms(filter_start);
    let filter_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "recentPlayedFilterCount",
        filter_ms,
        filter_resource_start.as_ref(),
        filter_resource_end.as_ref(),
    ));

    let limit = match request.limit {
        Some(limit) => limit.min(total_count),
        None => total_count,
    };
    let max_offset = total_count.saturating_sub(limit.min(total_count));
    let offset = request.offset.unwrap_or(0).min(max_offset);

    let track_ids_start = Instant::now();
    let track_ids_resource_start = crate::diagnostics::capture_process_resource_snapshot();
    let track_ids = if include_track_ids {
        load_recently_played_track_ids_for_query(
            connection,
            library_id,
            &search_query,
            &search_pattern,
            &type_filter,
            &type_file_pattern,
        )?
    } else {
        Vec::new()
    };
    let track_ids_ms = elapsed_ms(track_ids_start);
    let track_ids_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "recentPlayedTrackIds",
        track_ids_ms,
        track_ids_resource_start.as_ref(),
        track_ids_resource_end.as_ref(),
    ));

    let rows_start = Instant::now();
    let rows_resource_start = crate::diagnostics::capture_process_resource_snapshot();
    let rows = load_recently_played_rows(
        connection,
        library_id,
        &search_query,
        &search_pattern,
        &type_filter,
        &type_file_pattern,
        offset,
        limit,
    )?;
    let rows_ms = elapsed_ms(rows_start);
    let rows_resource_end = crate::diagnostics::capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "recentPlayedRows",
        rows_ms,
        rows_resource_start.as_ref(),
        rows_resource_end.as_ref(),
    ));

    let process_end = crate::diagnostics::capture_process_resource_snapshot();

    Ok(QueryTracksResult {
        total_count,
        collection_total_count: collection_track_count,
        offset,
        available_formats,
        track_ids,
        rows,
        diagnostics: QueryTracksDiagnostics {
            input_count: collection_track_count,
            filtered_count: total_count,
            library_track_count,
            collection_track_count,
            include_track_ids,
            connection_ms,
            catalog_consistency_ms,
            library_headers_ms,
            playlist_headers_ms,
            library_track_ids_ms: library_track_count_ms,
            collection_track_ids_ms,
            collection_resolve_ms,
            filter_ms,
            sort_ms: 0,
            track_ids_ms,
            rows_ms,
            total_ms: elapsed_ms(total_start),
            process: build_process_resource_diagnostics(
                process_start.as_ref(),
                process_end.as_ref(),
            ),
            step_profiles,
            ..QueryTracksDiagnostics::default()
        },
    })
}

pub(crate) fn build_sortable_track_cache_from_records(
    track_records: &[Value],
) -> Result<HashMap<String, SortableTrack>, String> {
    let mut sortable_track_cache = HashMap::with_capacity(track_records.len());

    for record in track_records {
        let mut sortable_track = create_sortable_track(record, 0)?;
        sortable_track.original_index = 0;
        sortable_track_cache.insert(sortable_track.id.clone(), sortable_track);
    }

    Ok(sortable_track_cache)
}

fn load_library_headers(connection: &Connection) -> Result<Vec<LibraryHeader>, String> {
    let mut statement = connection
        .prepare("SELECT id FROM libraries ORDER BY order_index ASC, created_at_text ASC, id ASC")
        .map_err(|error| format!("Failed to prepare desktop library header query: {error}"))?;
    let rows = statement
        .query_map([], |row| Ok(LibraryHeader { id: row.get(0)? }))
        .map_err(|error| format!("Failed to query desktop library headers: {error}"))?;

    let mut libraries = Vec::new();

    for row in rows {
        libraries
            .push(row.map_err(|error| format!("Failed to read desktop library header: {error}"))?);
    }

    Ok(libraries)
}

fn load_playlist_headers_for_library(
    connection: &Connection,
    library_id: &str,
) -> Result<Vec<PlaylistHeader>, String> {
    let mut statement = connection
        .prepare(
            "SELECT id, system_key
             FROM playlists
             WHERE library_id = ?1
             ORDER BY order_index ASC, created_at_text ASC, id ASC",
        )
        .map_err(|error| format!("Failed to prepare desktop playlist header query: {error}"))?;
    let rows = statement
        .query_map([library_id], |row| {
            Ok(PlaylistHeader {
                id: row.get(0)?,
                system_key: row.get(1)?,
            })
        })
        .map_err(|error| format!("Failed to query desktop playlist headers: {error}"))?;

    let mut playlists = Vec::new();

    for row in rows {
        playlists
            .push(row.map_err(|error| format!("Failed to read desktop playlist header: {error}"))?);
    }

    Ok(playlists)
}

fn load_library_track_counts(connection: &Connection) -> Result<HashMap<String, usize>, String> {
    let mut statement = connection
        .prepare("SELECT library_id, COUNT(id) FROM tracks GROUP BY library_id")
        .map_err(|error| format!("Failed to prepare desktop library count query: {error}"))?;
    let rows = statement
        .query_map([], |row| {
            Ok((row.get::<_, String>(0)?, row.get::<_, i64>(1)?))
        })
        .map_err(|error| format!("Failed to query desktop library counts: {error}"))?;

    let mut counts = HashMap::new();

    for row in rows {
        let (library_id, count) =
            row.map_err(|error| format!("Failed to read desktop library count: {error}"))?;
        counts.insert(
            library_id,
            usize::try_from(count.max(0)).unwrap_or_default(),
        );
    }

    Ok(counts)
}

fn load_library_track_count(connection: &Connection, library_id: &str) -> Result<usize, String> {
    let count = connection
        .query_row(
            "SELECT COUNT(*) FROM tracks WHERE library_id = ?1",
            [library_id],
            |row| row.get::<_, i64>(0),
        )
        .map_err(|error| format!("Failed to count desktop library tracks: {error}"))?;

    Ok(usize::try_from(count.max(0)).unwrap_or_default())
}

fn load_recently_played_collection_count(
    connection: &Connection,
    library_id: &str,
) -> Result<usize, String> {
    let count = connection
        .query_row(
            "SELECT COUNT(*)
             FROM playback_history_latest latest INDEXED BY idx_playback_history_latest_recorded_at
             CROSS JOIN track_query_projection tracks
             WHERE tracks.id = latest.track_id
               AND tracks.library_id = ?1",
            [library_id],
            |row| row.get::<_, i64>(0),
        )
        .map_err(|error| format!("Failed to count desktop recently played tracks: {error}"))?;

    Ok(usize::try_from(count.max(0)).unwrap_or_default())
}

fn load_recently_played_available_formats(
    connection: &Connection,
    library_id: &str,
) -> Result<Vec<String>, String> {
    let mut statement = connection
        .prepare(
            "SELECT tracks.format, tracks.file_name
             FROM playback_history_latest latest INDEXED BY idx_playback_history_latest_recorded_at
             CROSS JOIN track_query_projection tracks
             WHERE tracks.id = latest.track_id
               AND tracks.library_id = ?1",
        )
        .map_err(|error| {
            format!("Failed to prepare desktop recently played format query: {error}")
        })?;
    let rows = statement
        .query_map([library_id], |row| {
            Ok((
                row.get::<_, Option<String>>(0)?,
                row.get::<_, Option<String>>(1)?,
            ))
        })
        .map_err(|error| format!("Failed to query desktop recently played formats: {error}"))?;
    let mut formats = BTreeSet::new();

    for row in rows {
        let (format, file_name) =
            row.map_err(|error| format!("Failed to read desktop recently played format: {error}"))?;
        let resolved = resolve_projection_format(format.as_deref(), file_name.as_deref());

        if !resolved.is_empty() {
            formats.insert(resolved);
        }
    }

    Ok(formats.into_iter().collect())
}

fn count_recently_played_filtered_tracks(
    connection: &Connection,
    library_id: &str,
    search_query: &str,
    search_pattern: &str,
    type_filter: &str,
    type_file_pattern: &str,
) -> Result<usize, String> {
    let count = connection
        .query_row(
            &format!(
                "SELECT COUNT(*)
                 {}
                 {}",
                recently_played_projection_sql(),
                recently_played_filter_sql()
            ),
            params![
                library_id,
                search_query,
                search_pattern,
                type_filter,
                type_file_pattern
            ],
            |row| row.get::<_, i64>(0),
        )
        .map_err(|error| {
            format!("Failed to count filtered desktop recently played tracks: {error}")
        })?;

    Ok(usize::try_from(count.max(0)).unwrap_or_default())
}

fn load_recently_played_track_ids_for_query(
    connection: &Connection,
    library_id: &str,
    search_query: &str,
    search_pattern: &str,
    type_filter: &str,
    type_file_pattern: &str,
) -> Result<Vec<String>, String> {
    let mut statement = connection
        .prepare(&format!(
            "SELECT tracks.id
             {}
             {}
             ORDER BY latest.recorded_at_text DESC, latest.latest_history_id DESC, latest.track_id ASC",
            recently_played_projection_sql(),
            recently_played_filter_sql()
        ))
        .map_err(|error| {
            format!("Failed to prepare desktop recently played track id query: {error}")
        })?;
    let rows = statement
        .query_map(
            params![
                library_id,
                search_query,
                search_pattern,
                type_filter,
                type_file_pattern
            ],
            |row| row.get::<_, String>(0),
        )
        .map_err(|error| format!("Failed to query desktop recently played track ids: {error}"))?;
    let mut track_ids = Vec::new();

    for row in rows {
        track_ids.push(row.map_err(|error| {
            format!("Failed to read desktop recently played track id: {error}")
        })?);
    }

    Ok(track_ids)
}

#[allow(clippy::too_many_arguments)]
fn load_recently_played_rows(
    connection: &Connection,
    library_id: &str,
    search_query: &str,
    search_pattern: &str,
    type_filter: &str,
    type_file_pattern: &str,
    offset: usize,
    limit: usize,
) -> Result<Vec<QueryTrackRow>, String> {
    let limit = i64::try_from(limit).unwrap_or(i64::MAX);
    let offset = i64::try_from(offset).unwrap_or(i64::MAX);
    let mut statement = connection
        .prepare(&format!(
            "SELECT tracks.id,
                    tracks.library_id,
                    tracks.display_title,
                    tracks.title,
                    tracks.artist,
                    tracks.album_artist,
                    tracks.file_name,
                    tracks.format,
                    tracks.duration,
                    tracks.file_size,
                    tracks.bitrate,
                    tracks.sample_rate,
                    tracks.bit_depth,
                    tracks.is_favorite
             {}
             {}
             ORDER BY latest.recorded_at_text DESC, latest.latest_history_id DESC, latest.track_id ASC
             LIMIT ?6 OFFSET ?7",
            recently_played_projection_sql(),
            recently_played_filter_sql()
        ))
        .map_err(|error| format!("Failed to prepare desktop recently played row query: {error}"))?;
    let rows = statement
        .query_map(
            params![
                library_id,
                search_query,
                search_pattern,
                type_filter,
                type_file_pattern,
                limit,
                offset
            ],
            |row| {
                Ok(QueryTrackRow {
                    id: row.get(0)?,
                    library_id: row.get(1)?,
                    display_title: row.get(2)?,
                    title: row.get(3)?,
                    artist: row.get(4)?,
                    album_artist: row.get(5)?,
                    file_name: row.get(6)?,
                    format: row.get(7)?,
                    duration: row.get(8)?,
                    file_size: row.get::<_, Option<i64>>(9)?.map(nonnegative_i64_to_u64),
                    bitrate: row.get::<_, Option<i64>>(10)?.map(nonnegative_i64_to_u64),
                    sample_rate: row.get::<_, Option<i64>>(11)?.map(nonnegative_i64_to_u64),
                    bit_depth: row.get::<_, Option<i64>>(12)?.map(nonnegative_i64_to_u64),
                    is_favorite: row.get::<_, i64>(13)? != 0,
                })
            },
        )
        .map_err(|error| format!("Failed to query desktop recently played rows: {error}"))?;
    let mut track_rows = Vec::new();

    for row in rows {
        track_rows.push(
            row.map_err(|error| format!("Failed to read desktop recently played row: {error}"))?,
        );
    }

    Ok(track_rows)
}

fn load_recently_played_track_ids(
    connection: &Connection,
    library_id: &str,
) -> Result<Vec<String>, String> {
    load_track_id_query(
        connection,
        "SELECT tracks.id
         FROM playback_history_latest latest INDEXED BY idx_playback_history_latest_recorded_at
         CROSS JOIN track_query_projection tracks
         WHERE tracks.id = latest.track_id
           AND tracks.library_id = ?1
         ORDER BY latest.recorded_at_text DESC, latest.latest_history_id DESC, latest.track_id ASC",
        [library_id],
        "desktop recently played tracks",
    )
}

fn load_recently_played_track_count(
    connection: &Connection,
    library_id: &str,
) -> Result<usize, String> {
    load_recently_played_collection_count(connection, library_id)
}

fn recently_played_projection_sql() -> &'static str {
    "FROM playback_history_latest latest INDEXED BY idx_playback_history_latest_recorded_at
      CROSS JOIN track_query_projection tracks"
}

fn recently_played_filter_sql() -> &'static str {
    "WHERE tracks.id = latest.track_id
       AND tracks.library_id = ?1
       AND (
         ?2 = ''
         OR lower(
           COALESCE(tracks.display_title, '') || ' ' ||
           COALESCE(tracks.title, '') || ' ' ||
           COALESCE(tracks.artist, '') || ' ' ||
           COALESCE(tracks.album_artist, '') || ' ' ||
           COALESCE(tracks.album, '') || ' ' ||
           COALESCE(tracks.genre, '') || ' ' ||
           COALESCE(tracks.composer, '') || ' ' ||
           COALESCE(tracks.lyricist, '') || ' ' ||
           COALESCE(tracks.comment, '') || ' ' ||
           COALESCE(tracks.file_name, '')
         ) LIKE ?3 ESCAPE '\\'
       )
       AND (
         ?4 = ''
         OR ?4 = 'ALL'
         OR upper(trim(COALESCE(tracks.format, ''))) = ?4
         OR (
           trim(COALESCE(tracks.format, '')) = ''
           AND upper(COALESCE(tracks.file_name, '')) LIKE ?5 ESCAPE '\\'
         )
       )"
}

fn normalize_recent_query(value: &str) -> String {
    value.trim().to_lowercase()
}

fn normalize_recent_type_filter(value: &str) -> String {
    value.trim().to_uppercase()
}

fn escape_sql_like(value: &str) -> String {
    let mut escaped = String::with_capacity(value.len());

    for character in value.chars() {
        if matches!(character, '\\' | '%' | '_') {
            escaped.push('\\');
        }

        escaped.push(character);
    }

    escaped
}

fn resolve_projection_format(format: Option<&str>, file_name: Option<&str>) -> String {
    let format = format.unwrap_or_default().trim().to_uppercase();

    if !format.is_empty() {
        return format;
    }

    file_name
        .unwrap_or_default()
        .rsplit('.')
        .next()
        .map(|value| value.trim().to_uppercase())
        .filter(|value| !value.is_empty())
        .unwrap_or_else(|| String::from("AUDIO"))
}

fn load_current_queue_track_ids(
    connection: &Connection,
    valid_track_ids: &HashSet<String>,
) -> Result<Vec<String>, String> {
    let queue_track_ids = load_json_from_connection(connection, SESSION_STATE_KEY)?
        .and_then(|session| {
            session
                .get("queueTrackIds")
                .and_then(Value::as_array)
                .map(|items| {
                    items
                        .iter()
                        .filter_map(Value::as_str)
                        .map(String::from)
                        .collect::<Vec<_>>()
                })
        })
        .unwrap_or_default();

    Ok(unique_existing_track_ids(&queue_track_ids, valid_track_ids))
}

fn load_library_track_ids_by_order(
    connection: &Connection,
    library_id: &str,
) -> Result<Vec<String>, String> {
    load_track_id_query(
        connection,
        "SELECT id FROM tracks
         WHERE library_id = ?1
         ORDER BY library_order ASC, imported_at_text ASC, id ASC",
        [library_id],
        "desktop library track order",
    )
}

fn load_library_track_ids_by_import_date(
    connection: &Connection,
    library_id: &str,
) -> Result<Vec<String>, String> {
    load_track_id_query(
        connection,
        "SELECT id FROM tracks
         WHERE library_id = ?1
         ORDER BY imported_at_text DESC, id DESC",
        [library_id],
        "desktop recent imports",
    )
}

fn load_favorite_track_ids(
    connection: &Connection,
    library_id: &str,
) -> Result<Vec<String>, String> {
    load_track_id_query(
        connection,
        "SELECT id FROM tracks
         WHERE library_id = ?1 AND is_favorite = 1
         ORDER BY library_order ASC, imported_at_text ASC, id ASC",
        [library_id],
        "desktop favorite tracks",
    )
}

fn load_album_group_count(connection: &Connection, library_id: &str) -> Result<usize, String> {
    let count = connection
        .query_row(
            "SELECT COUNT(DISTINCT
                lower(trim(COALESCE(NULLIF(trim(album), ''), '未知专辑')))
                || char(31) ||
                lower(trim(COALESCE(NULLIF(trim(album_artist), ''), NULLIF(trim(artist), ''), '未知艺术家')))
             )
             FROM tracks
                  INDEXED BY idx_tracks_library_album_group
             WHERE library_id = ?1",
            [library_id],
            |row| row.get::<_, i64>(0),
        )
        .map_err(|error| format!("Failed to query desktop album group count: {error}"))?;

    Ok(usize::try_from(count.max(0)).unwrap_or_default())
}

fn load_artist_group_count(connection: &Connection, library_id: &str) -> Result<usize, String> {
    let count = connection
        .query_row(
            "SELECT COUNT(DISTINCT
                lower(trim(COALESCE(NULLIF(trim(artist), ''), NULLIF(trim(album_artist), ''), '未知艺术家')))
             )
             FROM tracks
                  INDEXED BY idx_tracks_library_artist_group
             WHERE library_id = ?1",
            [library_id],
            |row| row.get::<_, i64>(0),
        )
        .map_err(|error| format!("Failed to query desktop artist group count: {error}"))?;

    Ok(usize::try_from(count.max(0)).unwrap_or_default())
}

fn load_playlist_track_ids(
    connection: &Connection,
    playlist_id: &str,
    valid_track_ids: &HashSet<String>,
) -> Result<Vec<String>, String> {
    let ordered_track_ids = load_track_id_query(
        connection,
        "SELECT track_id FROM playlist_track_relations
         WHERE playlist_id = ?1
         ORDER BY order_index ASC, added_at_text ASC, id ASC",
        [playlist_id],
        "desktop playlist tracks",
    )?;

    Ok(unique_existing_track_ids(
        &ordered_track_ids,
        valid_track_ids,
    ))
}

fn load_sortable_tracks_for_ids(
    connection: &Connection,
    ordered_track_ids: &[String],
    track_query_cache: &RefCell<Option<HashMap<String, SortableTrack>>>,
) -> Result<(Vec<SortableTrack>, SortableTrackLoadDiagnostics), String> {
    if ordered_track_ids.is_empty() {
        return Ok((Vec::new(), SortableTrackLoadDiagnostics::default()));
    }

    let mut diagnostics = SortableTrackLoadDiagnostics::default();
    {
        let mut cache = track_query_cache.borrow_mut();

        if cache.is_none() {
            let cache_build_start = Instant::now();
            let (loaded_cache, cache_diagnostics) = load_sortable_track_cache(connection)?;
            diagnostics.payload_query_ms = cache_diagnostics.payload_query_ms;
            diagnostics.payload_deserialize_ms = cache_diagnostics.payload_deserialize_ms;
            diagnostics.project_ms = cache_diagnostics.project_ms;
            diagnostics.cache_build_ms = elapsed_ms(cache_build_start);
            diagnostics.cache_entries = loaded_cache.len();
            *cache = Some(loaded_cache);
        } else if let Some(entries) = cache.as_ref() {
            diagnostics.cache_used = true;
            diagnostics.cache_entries = entries.len();
        }
    }

    let sortable_tracks = {
        let cache = track_query_cache.borrow();
        let Some(entries) = cache.as_ref() else {
            return Ok((Vec::new(), diagnostics));
        };

        resolve_sortable_tracks_from_cache(entries, ordered_track_ids)
    };

    Ok((sortable_tracks, diagnostics))
}

pub(crate) fn load_sortable_track_cache_from_connection(
    connection: &Connection,
) -> Result<HashMap<String, SortableTrack>, String> {
    load_sortable_track_cache(connection).map(|(cache, _diagnostics)| cache)
}

fn load_sortable_track_cache(
    connection: &Connection,
) -> Result<(HashMap<String, SortableTrack>, SortableTrackLoadDiagnostics), String> {
    let payload_query_start = Instant::now();
    let mut statement = connection
        .prepare(
            "SELECT id,
                    library_id,
                    display_title,
                    title,
                    artist,
                    album_artist,
                    album,
                    genre,
                    composer,
                    lyricist,
                    comment,
                    file_name,
                    format,
                    duration,
                    file_size,
                    bitrate,
                    sample_rate,
                    bit_depth,
                    is_favorite
             FROM track_query_projection",
        )
        .map_err(|error| {
            format!("Failed to prepare desktop track cache projection lookup: {error}")
        })?;
    let rows = statement
        .query_map([], |row| {
            Ok(SortableTrack {
                id: row.get(0)?,
                library_id: row.get(1)?,
                original_index: 0,
                display_title: Some(row.get(2)?),
                title: Some(row.get(3)?),
                artist: Some(row.get(4)?),
                album_artist: Some(row.get(5)?),
                album: Some(row.get(6)?),
                genre: Some(row.get(7)?),
                composer: Some(row.get(8)?),
                lyricist: Some(row.get(9)?),
                comment: Some(row.get(10)?),
                file_name: Some(row.get(11)?),
                format: Some(row.get(12)?),
                duration: Some(row.get(13)?),
                file_size: Some(nonnegative_i64_to_u64(row.get(14)?)),
                bitrate: Some(nonnegative_i64_to_u64(row.get(15)?)),
                sample_rate: Some(nonnegative_i64_to_u64(row.get(16)?)),
                bit_depth: Some(nonnegative_i64_to_u64(row.get(17)?)),
                is_favorite: row.get::<_, i64>(18)? != 0,
            })
        })
        .map_err(|error| format!("Failed to query desktop track cache projections: {error}"))?;
    let mut sortable_track_cache = HashMap::new();

    for row in rows {
        let sortable_track =
            row.map_err(|error| format!("Failed to read desktop track cache projection: {error}"))?;
        sortable_track_cache.insert(sortable_track.id.clone(), sortable_track);
    }
    let payload_query_ms = elapsed_ms(payload_query_start);

    Ok((
        sortable_track_cache,
        SortableTrackLoadDiagnostics {
            payload_query_ms,
            ..SortableTrackLoadDiagnostics::default()
        },
    ))
}

fn resolve_sortable_tracks_from_cache(
    cache: &HashMap<String, SortableTrack>,
    ordered_track_ids: &[String],
) -> Vec<SortableTrack> {
    let mut sortable_tracks = Vec::with_capacity(ordered_track_ids.len());

    for (original_index, track_id) in ordered_track_ids.iter().enumerate() {
        let Some(cached_track) = cache.get(track_id) else {
            continue;
        };

        let mut sortable_track = cached_track.clone();
        sortable_track.original_index = u32::try_from(original_index).unwrap_or(u32::MAX);
        sortable_tracks.push(sortable_track);
    }

    sortable_tracks
}

fn create_sortable_track(record: &Value, original_index: u32) -> Result<SortableTrack, String> {
    Ok(SortableTrack {
        id: required_text_field(record, "id", "desktop queried track")?,
        library_id: required_text_field(record, "libraryId", "desktop queried track")?,
        original_index,
        display_title: optional_text_field(record, "displayTitle"),
        title: optional_text_field(record, "title"),
        artist: optional_text_field(record, "artist"),
        album_artist: optional_text_field(record, "albumArtist"),
        album: optional_text_field(record, "album"),
        genre: optional_text_field(record, "genre"),
        composer: optional_text_field(record, "composer"),
        lyricist: optional_text_field(record, "lyricist"),
        comment: optional_text_field(record, "comment"),
        file_name: optional_text_field(record, "fileName"),
        format: optional_text_field(record, "format"),
        duration: optional_number_as_f64(record, "duration"),
        file_size: optional_number_as_u64(record, "fileSize")
            .or_else(|| optional_number_as_u64(record, "size")),
        bitrate: optional_number_as_u64(record, "bitrate"),
        sample_rate: optional_number_as_u64(record, "sampleRate"),
        bit_depth: optional_number_as_u64(record, "bitDepth"),
        is_favorite: required_boolean_field(record, "isFavorite", "desktop queried track")?,
    })
}

fn unique_existing_track_ids(
    track_ids: &[String],
    valid_track_ids: &HashSet<String>,
) -> Vec<String> {
    let mut seen_track_ids = HashSet::new();
    let mut next_track_ids = Vec::new();

    for track_id in track_ids {
        if !valid_track_ids.contains(track_id) || seen_track_ids.contains(track_id) {
            continue;
        }

        seen_track_ids.insert(track_id.clone());
        next_track_ids.push(track_id.clone());
    }

    next_track_ids
}

fn resolve_active_library_id(
    libraries: &[LibraryHeader],
    preferred_library_id: Option<&str>,
) -> Option<String> {
    if let Some(preferred_library_id) = preferred_library_id {
        if libraries
            .iter()
            .any(|library| library.id == preferred_library_id)
        {
            return Some(String::from(preferred_library_id));
        }
    }

    libraries.first().map(|library| library.id.clone())
}

fn resolve_default_collection_key(playlists: &[PlaylistHeader]) -> Option<String> {
    playlists
        .iter()
        .find(|playlist| playlist.system_key.as_deref() == Some(SYSTEM_PLAYLIST_ALL_TRACKS_KEY))
        .map(|playlist| format!("{PLAYLIST_COLLECTION_PREFIX}{}", playlist.id))
        .or_else(|| {
            playlists
                .first()
                .map(|playlist| format!("{PLAYLIST_COLLECTION_PREFIX}{}", playlist.id))
        })
        .or_else(|| {
            Some(format!(
                "{VIEW_COLLECTION_PREFIX}{SMART_VIEW_RECENT_IMPORTS}"
            ))
        })
}

fn resolve_active_collection_key(
    preferred_collection_ref: Option<&str>,
    playlists: &[PlaylistHeader],
    default_collection_key: Option<&str>,
) -> Option<String> {
    match preferred_collection_ref.map(parse_collection_ref) {
        Some(CollectionRefKind::Playlist(playlist_id))
            if playlists.iter().any(|playlist| playlist.id == playlist_id) =>
        {
            Some(String::from(preferred_collection_ref.unwrap_or_default()))
        }
        Some(CollectionRefKind::View(view_key)) if is_known_smart_view_key(view_key) => {
            Some(String::from(preferred_collection_ref.unwrap_or_default()))
        }
        _ => default_collection_key.map(String::from),
    }
}

fn parse_collection_ref(collection_ref: &str) -> CollectionRefKind<'_> {
    if let Some(playlist_id) = collection_ref.strip_prefix(PLAYLIST_COLLECTION_PREFIX) {
        if !playlist_id.trim().is_empty() {
            return CollectionRefKind::Playlist(playlist_id);
        }
    }

    if let Some(view_key) = collection_ref.strip_prefix(VIEW_COLLECTION_PREFIX) {
        if !view_key.trim().is_empty() {
            return CollectionRefKind::View(view_key);
        }
    }

    CollectionRefKind::Invalid
}

fn is_known_smart_view_key(view_key: &str) -> bool {
    matches!(
        view_key,
        SMART_VIEW_RECENT_IMPORTS
            | SMART_VIEW_RECENTLY_PLAYED
            | SMART_VIEW_FAVORITES
            | SMART_VIEW_CURRENT_QUEUE
            | SMART_VIEW_ALBUMS
            | SMART_VIEW_ARTISTS
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::schema::initialize_schema;
    use rusqlite::{params, Connection};

    fn setup_connection() -> Connection {
        let connection = Connection::open_in_memory().expect("in-memory db should open");
        initialize_schema(&connection).expect("schema should initialize");
        connection
    }

    fn insert_track(connection: &Connection, id: &str, library_id: &str, order: i64) {
        connection
            .execute(
                "INSERT INTO tracks
                   (id, library_id, library_order, imported_at_text, is_favorite,
                    title, display_title, file_name, format, payload_json, updated_at)
                 VALUES (?1, ?2, ?3, ?4, 0, ?1, ?1, ?1, 'MP3', '{}', 1)",
                params![
                    id,
                    library_id,
                    order,
                    format!("2026-05-09T00:00:{order:02}Z")
                ],
            )
            .expect("track should insert");
    }

    fn insert_history(
        connection: &Connection,
        id: &str,
        track_id: Option<&str>,
        recorded_at: &str,
    ) {
        insert_history_with_type(connection, id, track_id, recorded_at, "played");
    }

    fn insert_history_with_type(
        connection: &Connection,
        id: &str,
        track_id: Option<&str>,
        recorded_at: &str,
        entry_type: &str,
    ) {
        connection
            .execute(
                "INSERT INTO playback_history
                   (id, track_id, recorded_at_text, payload_json, updated_at)
                 VALUES (?1, ?2, ?3, ?4, 1)",
                params![
                    id,
                    track_id,
                    recorded_at,
                    format!("{{\"type\":\"{entry_type}\"}}")
                ],
            )
            .expect("history entry should insert");

        if entry_type == "played" {
            if let Some(track_id) = track_id {
                connection
                    .execute(
                        "INSERT INTO playback_history_latest
                           (track_id, latest_history_id, recorded_at_text, updated_at)
                         VALUES (?1, ?2, ?3, 1)
                         ON CONFLICT(track_id) DO UPDATE SET
                           latest_history_id = excluded.latest_history_id,
                           recorded_at_text = excluded.recorded_at_text,
                           updated_at = excluded.updated_at
                         WHERE excluded.recorded_at_text > playback_history_latest.recorded_at_text
                            OR (
                              excluded.recorded_at_text = playback_history_latest.recorded_at_text
                              AND excluded.latest_history_id > playback_history_latest.latest_history_id
                            )",
                        params![track_id, id, recorded_at],
                    )
                    .expect("latest history projection should upsert");
            }
        }
    }

    #[test]
    fn recently_played_ignores_non_played_history_entries() {
        let connection = setup_connection();
        insert_track(&connection, "track-a", "library-a", 1);
        insert_track(&connection, "track-b", "library-a", 2);
        insert_history_with_type(
            &connection,
            "history-a-played",
            Some("track-a"),
            "2026-05-09T10:00:00Z",
            "played",
        );
        insert_history_with_type(
            &connection,
            "history-b-paused",
            Some("track-b"),
            "2026-05-09T10:10:00Z",
            "paused",
        );

        let track_ids = load_recently_played_track_ids(&connection, "library-a")
            .expect("recently played ids should load");
        let count = load_recently_played_track_count(&connection, "library-a")
            .expect("recently played count should load");

        assert_eq!(track_ids, vec!["track-a"]);
        assert_eq!(count, 1);
    }

    #[test]
    fn recently_played_track_ids_are_unique_and_ordered_by_latest_history() {
        let connection = setup_connection();
        insert_track(&connection, "track-a", "library-a", 1);
        insert_track(&connection, "track-b", "library-a", 2);
        insert_track(&connection, "track-c", "library-b", 3);
        insert_history(
            &connection,
            "history-a-old",
            Some("track-a"),
            "2026-05-09T10:00:00Z",
        );
        insert_history(
            &connection,
            "history-b",
            Some("track-b"),
            "2026-05-09T10:05:00Z",
        );
        insert_history(
            &connection,
            "history-a-new",
            Some("track-a"),
            "2026-05-09T10:10:00Z",
        );
        insert_history(
            &connection,
            "history-c-other-library",
            Some("track-c"),
            "2026-05-09T10:20:00Z",
        );
        insert_history(&connection, "history-null", None, "2026-05-09T10:30:00Z");

        let track_ids = load_recently_played_track_ids(&connection, "library-a")
            .expect("recently played ids should load");

        assert_eq!(track_ids, vec!["track-a", "track-b"]);
    }

    #[test]
    fn recently_played_track_count_counts_distinct_library_tracks() {
        let connection = setup_connection();
        insert_track(&connection, "track-a", "library-a", 1);
        insert_track(&connection, "track-b", "library-a", 2);
        insert_track(&connection, "track-c", "library-b", 3);
        insert_history(
            &connection,
            "history-a-old",
            Some("track-a"),
            "2026-05-09T10:00:00Z",
        );
        insert_history(
            &connection,
            "history-a-new",
            Some("track-a"),
            "2026-05-09T10:10:00Z",
        );
        insert_history(
            &connection,
            "history-b",
            Some("track-b"),
            "2026-05-09T10:05:00Z",
        );
        insert_history(
            &connection,
            "history-c-other-library",
            Some("track-c"),
            "2026-05-09T10:20:00Z",
        );

        let count = load_recently_played_track_count(&connection, "library-a")
            .expect("recently played count should load");

        assert_eq!(count, 2);
    }
}
