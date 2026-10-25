use crate::diagnostics::{DiagnosticStepProfile, ProcessResourceDiagnostics};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::HashMap;

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CatalogSnapshot {
    pub libraries: Vec<Value>,
    pub playlists: Vec<Value>,
    pub tracks: Vec<Value>,
    pub playlist_track_relations: Vec<Value>,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct CatalogLoadSnapshotRequest {
    pub track_artwork_mode: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TrackLookupRequest {
    pub track_id: String,
    pub include_artwork: Option<bool>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TrackUpdateRequest {
    pub track_id: String,
    pub patch: Value,
}

#[derive(Debug, Clone, Deserialize)]
pub struct UpsertRecordsRequest {
    pub records: Vec<Value>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct DeleteRecordsRequest {
    pub ids: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
pub struct HistoryLoadRequest {
    pub limit: usize,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ListeningStatsRequest {
    pub library_id: Option<String>,
    pub days: Option<usize>,
    pub track_limit: Option<usize>,
    pub album_limit: Option<usize>,
    pub album_track_limit: Option<usize>,
    pub timezone_offset_minutes: Option<i32>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ListeningStatsSummary {
    pub total_seconds: f64,
    pub play_count: usize,
    pub track_count: usize,
    pub album_count: usize,
    pub active_days: usize,
    pub peak_day: Option<String>,
    pub peak_day_seconds: f64,
    pub longest_streak_days: usize,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ListeningStatsDailyBucket {
    pub date: String,
    pub seconds: f64,
    pub play_count: usize,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ListeningStatsTrackRank {
    pub track_id: String,
    pub title: String,
    pub artist: String,
    pub album: String,
    pub album_artist: String,
    pub artwork: String,
    pub duration: f64,
    pub listen_seconds: f64,
    pub play_count: usize,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ListeningStatsAlbumGroup {
    pub key: String,
    pub album: String,
    pub album_artist: String,
    pub artwork: String,
    pub listen_seconds: f64,
    pub play_count: usize,
    pub track_count: usize,
    pub tracks: Vec<ListeningStatsTrackRank>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ListeningStatsSnapshot {
    pub generated_at: String,
    pub library_id: Option<String>,
    pub days: usize,
    pub summary: ListeningStatsSummary,
    pub daily: Vec<ListeningStatsDailyBucket>,
    pub top_tracks: Vec<ListeningStatsTrackRank>,
    pub album_groups: Vec<ListeningStatsAlbumGroup>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionStateSnapshot {
    pub id: String,
    pub started_at: String,
    pub last_interacted_at: String,
    pub current_track_id: Option<String>,
    pub queue_track_ids: Vec<String>,
    #[serde(default = "default_session_playback_status")]
    pub playback_status: String,
    #[serde(default)]
    pub current_time: f64,
    #[serde(default)]
    pub duration: f64,
}

fn default_session_playback_status() -> String {
    String::from("idle")
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct SessionQueueRequest {
    pub track_ids: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionSelectTrackRequest {
    pub track_id: String,
    pub queue_track_ids: Option<Vec<String>>,
    pub autoplay: Option<bool>,
    pub playback_source: Option<Value>,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct SessionPreviousRequest {
    pub restart_threshold_seconds: Option<f64>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaylistCreateRequest {
    pub library_id: String,
    pub name: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaylistRenameRequest {
    pub playlist_id: String,
    pub name: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaylistDeleteRequest {
    pub playlist_id: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaylistReorderRequest {
    pub library_id: String,
    pub ordered_playlist_ids: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaylistTrackMutationRequest {
    pub playlist_id: String,
    pub track_id: String,
    pub index: Option<usize>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaylistTrackRemoveRequest {
    pub playlist_id: String,
    pub track_id: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaylistTrackReorderRequest {
    pub playlist_id: String,
    pub ordered_track_ids: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaylistDeleteResult {
    pub deleted_playlist_id: String,
    pub deleted_relation_ids: Vec<String>,
    pub library_id: String,
    pub playlists: Vec<Value>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaylistTrackMutationResult {
    pub relation: Option<Value>,
    pub relations: Vec<Value>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaylistTrackRemoveResult {
    pub deleted_relation_id: Option<String>,
    pub relations: Vec<Value>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryCreateRequest {
    pub name: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryRenameRequest {
    pub library_id: String,
    pub name: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryDeleteRequest {
    pub library_id: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryReorderRequest {
    pub ordered_library_ids: Vec<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryCreateResult {
    pub library: Value,
    pub default_playlist: Value,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryDeleteResult {
    pub deleted_library_id: String,
    pub deleted_playlist_ids: Vec<String>,
    pub deleted_track_ids: Vec<String>,
    pub deleted_relation_ids: Vec<String>,
    pub fallback_library_id: Option<String>,
    pub libraries: Vec<Value>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TrackDeleteRequest {
    pub track_id: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TrackBatchDeleteRequest {
    pub track_ids: Vec<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TrackFavoriteRequest {
    pub track_id: String,
    pub is_favorite: bool,
}

#[derive(Debug, Clone, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryImportFileInput {
    pub source_path: String,
    pub file_name: Option<String>,
    pub original_path: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryImportCandidatesRequest {
    pub library_id: String,
    pub files: Vec<LibraryImportFileInput>,
    pub respect_deleted_import_paths: Option<bool>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryPreparedTrackImportRequest {
    pub library_id: String,
    pub tracks: Vec<Value>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalIndexInvalidationRequest {
    pub library_id: String,
    pub directories: Vec<String>,
    pub discovered_paths: Vec<String>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LocalIndexInvalidationResult {
    pub invalidated_track_ids: Vec<String>,
    pub invalidated_relation_ids: Vec<String>,
    pub reordered_tracks: Vec<Value>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TrackDeleteResult {
    pub deleted_track_id: String,
    pub deleted_relation_ids: Vec<String>,
    pub library_id: String,
    pub reordered_tracks: Vec<Value>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct TrackBatchDeleteResult {
    pub deleted_track_ids: Vec<String>,
    pub deleted_relation_ids: Vec<String>,
    pub library_ids: Vec<String>,
    pub reordered_tracks: Vec<Value>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NavigationQueryRequest {
    pub active_library: Option<String>,
    pub active_collection_ref: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CollectionTrackQueryRequest {
    pub active_library: Option<String>,
    pub active_collection_ref: Option<String>,
    pub search_query: String,
    pub type_filter: String,
    pub sort_option: String,
    pub offset: Option<usize>,
    pub limit: Option<usize>,
    pub include_track_ids: Option<bool>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NavigationQueryResult {
    pub active_library: Option<String>,
    pub active_collection_key: Option<String>,
    pub library_track_counts: HashMap<String, usize>,
    pub playlist_track_counts: HashMap<String, usize>,
    pub smart_collection_counts: HashMap<String, usize>,
    pub diagnostics: NavigationQueryDiagnostics,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NavigationQueryDiagnostics {
    pub command_lock_wait_ms: u64,
    pub library_counts_ms: u64,
    pub collection_ids_ms: u64,
    pub playlist_counts_ms: u64,
    pub smart_counts_ms: u64,
    pub total_ms: u64,
    pub process: ProcessResourceDiagnostics,
    pub step_profiles: Vec<DiagnosticStepProfile>,
}

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct DesktopBootstrapRequest {
    pub history_limit: Option<usize>,
    pub include_catalog_tracks: Option<bool>,
    pub include_playlist_track_relations: Option<bool>,
    pub warm_track_query_cache: Option<bool>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DesktopBootstrapDiagnostics {
    pub connection_ms: u64,
    pub revisions_ms: u64,
    pub preferences_ms: u64,
    pub session_ms: u64,
    pub catalog_cache_hit: bool,
    pub catalog_cache_ms: u64,
    pub catalog_consistency_ms: u64,
    pub catalog_load_ms: u64,
    pub catalog_ms: u64,
    pub catalog_tracks_included: bool,
    pub catalog_track_count: usize,
    pub catalog_relation_count: usize,
    pub track_cache_warm_ms: u64,
    pub track_cache_entries: usize,
    pub history_ms: u64,
    pub navigation_ms: u64,
    pub total_ms: u64,
    pub process: ProcessResourceDiagnostics,
    pub step_profiles: Vec<DiagnosticStepProfile>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DesktopStateRevisions {
    pub catalog: u64,
    pub navigation: u64,
    pub history: u64,
    pub preferences: u64,
    pub session: u64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DesktopBootstrapManifest {
    pub version: String,
    pub generated_at: String,
    pub revisions: DesktopStateRevisions,
    pub catalog_consistency_checked: bool,
    pub track_query_cache_ready: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DesktopBootstrapSnapshot {
    pub manifest: DesktopBootstrapManifest,
    pub preferences: Option<Value>,
    pub session: Option<Value>,
    pub catalog: CatalogSnapshot,
    pub history: Vec<Value>,
    pub navigation: NavigationQueryResult,
    pub diagnostics: DesktopBootstrapDiagnostics,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DesktopStateResetResult {
    pub managed_storage_deleted: bool,
    pub managed_storage_path: Option<String>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StorageUsageItem {
    pub key: String,
    pub path: Option<String>,
    pub bytes: u64,
    pub file_count: usize,
    pub directory_count: usize,
    pub reclaimable_bytes: u64,
    pub reclaimable_file_count: usize,
    pub details: Option<Value>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StorageUsageSnapshot {
    pub generated_at: String,
    pub total_bytes: u64,
    pub reclaimable_bytes: u64,
    pub items: Vec<StorageUsageItem>,
    pub warnings: Vec<String>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StorageGarbageCollectionItem {
    pub key: String,
    pub removed_bytes: u64,
    pub removed_files: usize,
    pub removed_directories: usize,
    pub compacted_bytes: u64,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StorageGarbageCollectionResult {
    pub started_at: String,
    pub completed_at: String,
    pub total_ms: u64,
    pub before: StorageUsageSnapshot,
    pub after: StorageUsageSnapshot,
    pub reclaimed_bytes: u64,
    pub removed_files: usize,
    pub removed_directories: usize,
    pub items: Vec<StorageGarbageCollectionItem>,
    pub warnings: Vec<String>,
}
