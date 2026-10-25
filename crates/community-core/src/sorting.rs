use crate::diagnostics::{
    build_diagnostic_step_profile, build_process_resource_diagnostics,
    capture_process_resource_snapshot, DiagnosticStepProfile, ProcessResourceDiagnostics,
};
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;
use std::{cmp::Ordering, time::Instant};

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SortableTrack {
    pub id: String,
    pub library_id: String,
    pub original_index: u32,
    pub display_title: Option<String>,
    pub title: Option<String>,
    pub artist: Option<String>,
    pub album_artist: Option<String>,
    pub album: Option<String>,
    pub genre: Option<String>,
    pub composer: Option<String>,
    pub lyricist: Option<String>,
    pub comment: Option<String>,
    pub file_name: Option<String>,
    pub format: Option<String>,
    pub duration: Option<f64>,
    pub file_size: Option<u64>,
    pub bitrate: Option<u64>,
    pub sample_rate: Option<u64>,
    pub bit_depth: Option<u64>,
    pub is_favorite: bool,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct QueryTrackRow {
    pub id: String,
    pub library_id: String,
    pub display_title: Option<String>,
    pub title: Option<String>,
    pub artist: Option<String>,
    pub album_artist: Option<String>,
    pub file_name: Option<String>,
    pub format: Option<String>,
    pub duration: Option<f64>,
    pub file_size: Option<u64>,
    pub bitrate: Option<u64>,
    pub sample_rate: Option<u64>,
    pub bit_depth: Option<u64>,
    pub is_favorite: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct QueryTracksRequest {
    pub search_query: String,
    pub type_filter: String,
    pub sort_option: String,
    pub offset: Option<usize>,
    pub limit: Option<usize>,
    pub include_track_ids: Option<bool>,
    pub tracks: Vec<SortableTrack>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct QueryTracksResult {
    pub total_count: usize,
    pub collection_total_count: usize,
    pub offset: usize,
    pub available_formats: Vec<String>,
    pub track_ids: Vec<String>,
    pub rows: Vec<QueryTrackRow>,
    pub diagnostics: QueryTracksDiagnostics,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct QueryTracksDiagnostics {
    pub input_count: usize,
    pub filtered_count: usize,
    pub library_track_count: usize,
    pub collection_track_count: usize,
    pub include_track_ids: bool,
    pub command_lock_wait_ms: u64,
    pub connection_ms: u64,
    pub catalog_consistency_ms: u64,
    pub library_headers_ms: u64,
    pub playlist_headers_ms: u64,
    pub library_track_ids_ms: u64,
    pub collection_track_ids_ms: u64,
    pub collection_resolve_ms: u64,
    pub sortable_tracks_ms: u64,
    pub payload_query_ms: u64,
    pub payload_deserialize_ms: u64,
    pub sortable_project_ms: u64,
    pub track_cache_used: bool,
    pub track_cache_build_ms: u64,
    pub track_cache_entries: usize,
    pub filter_ms: u64,
    pub sort_ms: u64,
    pub track_ids_ms: u64,
    pub rows_ms: u64,
    pub total_ms: u64,
    pub process: ProcessResourceDiagnostics,
    pub step_profiles: Vec<DiagnosticStepProfile>,
}

pub fn query_tracks(request: QueryTracksRequest) -> Result<QueryTracksResult, String> {
    let total_start = Instant::now();
    let process_start = capture_process_resource_snapshot();
    let mut step_profiles = Vec::new();
    let search_query = normalize_query(&request.search_query);
    let type_filter = normalize_type_filter(&request.type_filter);
    let sort_option = normalize_sort_option(&request.sort_option);
    let include_track_ids = request.include_track_ids.unwrap_or(false);
    let collection_total_count = request.tracks.len();
    let available_formats = collect_available_formats(&request.tracks);
    let filter_start = Instant::now();
    let filter_resource_start = capture_process_resource_snapshot();
    let mut tracks: Vec<SortableTrack> = request
        .tracks
        .into_iter()
        .filter(|track| {
            matches_search(track, &search_query) && matches_type_filter(track, &type_filter)
        })
        .collect();
    let filter_ms = elapsed_ms(filter_start);
    let filter_resource_end = capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "filter",
        filter_ms,
        filter_resource_start.as_ref(),
        filter_resource_end.as_ref(),
    ));

    let sort_start = Instant::now();
    let sort_resource_start = capture_process_resource_snapshot();
    tracks.sort_by(|left, right| compare_tracks(left, right, sort_option));
    let sort_ms = elapsed_ms(sort_start);
    let sort_resource_end = capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "sort",
        sort_ms,
        sort_resource_start.as_ref(),
        sort_resource_end.as_ref(),
    ));

    let total_count = tracks.len();
    let limit = match request.limit {
        Some(limit) => limit.min(total_count),
        None => total_count,
    };
    let max_offset = total_count.saturating_sub(limit.min(total_count));
    let offset = request.offset.unwrap_or(0).min(max_offset);
    let track_ids_start = Instant::now();
    let track_ids_resource_start = capture_process_resource_snapshot();
    let track_ids = if include_track_ids {
        tracks.iter().map(|track| track.id.clone()).collect()
    } else {
        Vec::new()
    };
    let track_ids_ms = elapsed_ms(track_ids_start);
    let track_ids_resource_end = capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "trackIds",
        track_ids_ms,
        track_ids_resource_start.as_ref(),
        track_ids_resource_end.as_ref(),
    ));
    let rows_start = Instant::now();
    let rows_resource_start = capture_process_resource_snapshot();
    let rows = tracks
        .into_iter()
        .skip(offset)
        .take(limit)
        .map(project_track_row)
        .collect();
    let rows_ms = elapsed_ms(rows_start);
    let rows_resource_end = capture_process_resource_snapshot();
    step_profiles.push(build_diagnostic_step_profile(
        "rows",
        rows_ms,
        rows_resource_start.as_ref(),
        rows_resource_end.as_ref(),
    ));
    let process_end = capture_process_resource_snapshot();

    Ok(QueryTracksResult {
        total_count,
        collection_total_count,
        offset,
        available_formats,
        track_ids,
        rows,
        diagnostics: QueryTracksDiagnostics {
            input_count: collection_total_count,
            filtered_count: total_count,
            include_track_ids,
            filter_ms,
            sort_ms,
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

fn collect_available_formats(tracks: &[SortableTrack]) -> Vec<String> {
    tracks
        .iter()
        .map(resolve_track_format)
        .filter(|format| !format.is_empty())
        .collect::<BTreeSet<_>>()
        .into_iter()
        .collect()
}

fn project_track_row(track: SortableTrack) -> QueryTrackRow {
    QueryTrackRow {
        id: track.id,
        library_id: track.library_id,
        display_title: track.display_title,
        title: track.title,
        artist: track.artist,
        album_artist: track.album_artist,
        file_name: track.file_name,
        format: track.format,
        duration: track.duration,
        file_size: track.file_size,
        bitrate: track.bitrate,
        sample_rate: track.sample_rate,
        bit_depth: track.bit_depth,
        is_favorite: track.is_favorite,
    }
}

fn normalize_sort_option(value: &str) -> &str {
    match value.trim() {
        "title" => "title",
        "duration" => "duration",
        "size" => "size",
        _ => "recent",
    }
}

fn normalize_query(value: &str) -> String {
    value.trim().to_lowercase()
}

fn normalize_type_filter(value: &str) -> String {
    value.trim().to_uppercase()
}

fn compare_tracks(left: &SortableTrack, right: &SortableTrack, sort_option: &str) -> Ordering {
    match sort_option {
        "title" => compare_text(&track_sort_label(left), &track_sort_label(right))
            .then_with(|| left.original_index.cmp(&right.original_index)),
        "duration" => compare_optional_f64_desc(left.duration, right.duration)
            .then_with(|| left.original_index.cmp(&right.original_index)),
        "size" => compare_optional_u64_desc(left.file_size, right.file_size)
            .then_with(|| left.original_index.cmp(&right.original_index)),
        _ => right.original_index.cmp(&left.original_index),
    }
}

fn matches_search(track: &SortableTrack, query: &str) -> bool {
    if query.is_empty() {
        return true;
    }

    let searchable_text = [
        track.display_title.as_deref(),
        track.title.as_deref(),
        track.artist.as_deref(),
        track.album_artist.as_deref(),
        track.album.as_deref(),
        track.genre.as_deref(),
        track.composer.as_deref(),
        track.lyricist.as_deref(),
        track.comment.as_deref(),
        track.file_name.as_deref(),
    ]
    .into_iter()
    .flatten()
    .map(|value| value.trim().to_lowercase())
    .filter(|value| !value.is_empty())
    .collect::<Vec<String>>()
    .join(" ");

    searchable_text.contains(query)
}

fn matches_type_filter(track: &SortableTrack, type_filter: &str) -> bool {
    if type_filter.is_empty() || type_filter == "ALL" {
        return true;
    }

    resolve_track_format(track) == type_filter
}

fn track_sort_label(track: &SortableTrack) -> String {
    let display_title = normalize_text(track.display_title.as_deref());

    if !display_title.is_empty() {
        return display_title;
    }

    let title = normalize_text(track.title.as_deref());

    if !title.is_empty() {
        return title;
    }

    String::from("untitled")
}

fn resolve_track_format(track: &SortableTrack) -> String {
    let format = track
        .format
        .as_deref()
        .unwrap_or_default()
        .trim()
        .to_uppercase();

    if !format.is_empty() {
        return format;
    }

    track
        .file_name
        .as_deref()
        .and_then(|value| value.rsplit('.').next())
        .map(|value| value.trim().to_uppercase())
        .filter(|value| !value.is_empty())
        .unwrap_or_else(|| String::from("AUDIO"))
}

fn normalize_text(value: Option<&str>) -> String {
    value.unwrap_or_default().trim().to_lowercase()
}

fn compare_text(left: &str, right: &str) -> Ordering {
    left.cmp(right)
}

fn compare_optional_u64_desc(left: Option<u64>, right: Option<u64>) -> Ordering {
    right.unwrap_or(0).cmp(&left.unwrap_or(0))
}

fn compare_optional_f64_desc(left: Option<f64>, right: Option<f64>) -> Ordering {
    let left = normalize_f64(left);
    let right = normalize_f64(right);

    right.partial_cmp(&left).unwrap_or(Ordering::Equal)
}

fn normalize_f64(value: Option<f64>) -> f64 {
    match value {
        Some(number) if number.is_finite() && number >= 0.0 => number,
        _ => 0.0,
    }
}

fn elapsed_ms(start: Instant) -> u64 {
    start.elapsed().as_millis().try_into().unwrap_or(u64::MAX)
}
