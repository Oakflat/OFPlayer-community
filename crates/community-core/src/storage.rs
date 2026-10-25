use crate::{
    audio_formats,
    metadata::{self, ParseAudioMetadataRequest, ParsedAudioMetadata},
};
use serde::{Deserialize, Serialize};
#[cfg(test)]
use serde_json::json;
use serde_json::Value;
use std::{
    collections::VecDeque,
    fs,
    io::ErrorKind,
    path::{Path, PathBuf},
    time::Instant,
};
use uuid::Uuid;

const MANAGED_STORAGE_DIR_NAME: &str = "OFPlayer Library";
const MANAGED_STORAGE_MARKER_FILE_NAME: &str = ".ofplayer-managed-storage.json";
const MANAGED_STORAGE_MARKER_APP: &str = "OFPlayer";
const MANAGED_STORAGE_MARKER_KIND: &str = "managed-storage";
#[cfg(test)]
const MANAGED_STORAGE_MARKER_VERSION: u64 = 1;
#[cfg(test)]
const LIBRARIES_DIR_NAME: &str = "libraries";

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct IndexedFileResult {
    pub source_path: String,
    pub indexed_path: String,
    pub file_name: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PrepareTrackImportsRequest {
    pub library_id: String,
    pub files: Vec<PrepareTrackImportInput>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PrepareTrackImportInput {
    pub source_path: String,
    pub file_name: Option<String>,
    pub original_path: Option<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PreparedTrackImport {
    pub id: String,
    pub library_id: String,
    pub library_order: u32,
    pub is_favorite: bool,
    pub title: String,
    pub artist: String,
    pub album_artist: String,
    pub album: String,
    pub genre: String,
    pub year: u32,
    pub track_number: u32,
    pub track_total: u32,
    pub disc_number: u32,
    pub disc_total: u32,
    pub composer: String,
    pub lyricist: String,
    pub comment: String,
    pub display_title: String,
    pub file_name: String,
    pub file_size: u64,
    pub size: u64,
    pub duration: f64,
    pub format: String,
    pub bitrate: u32,
    pub sample_rate: u32,
    pub bit_depth: u32,
    pub artwork: String,
    pub mime_type: String,
    pub imported_at: Option<String>,
    pub metadata_version: u32,
    pub source: PreparedTrackSource,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PreparedTrackSource {
    pub kind: String,
    pub path: String,
    pub origin_path: String,
    pub indexed: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ScanDirectoriesRequest {
    pub directories: Vec<String>,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ScannedAudioFile {
    pub path: String,
    pub file_name: String,
}

#[derive(Debug, Default, Clone, Copy)]
pub struct PrepareTrackImportsDiagnostics {
    pub copy_ms: u64,
    pub metadata_ms: u64,
    pub metadata_fallback_count: usize,
}

#[derive(Debug)]
pub struct PrepareTrackImportsReport {
    pub tracks: Vec<PreparedTrackImport>,
    pub diagnostics: PrepareTrackImportsDiagnostics,
}

#[derive(Debug, Clone)]
pub struct PrepareTrackImportsProgress {
    pub processed: usize,
    pub total: usize,
    pub current_file: String,
}

#[derive(Debug, Default, Clone, Copy)]
pub struct ScanAudioFilesDiagnostics {
    pub directories_scanned: usize,
    pub entries_scanned: usize,
}

#[derive(Debug)]
pub struct ScanAudioFilesReport {
    pub files: Vec<ScannedAudioFile>,
    pub diagnostics: ScanAudioFilesDiagnostics,
}

#[derive(Debug, Clone)]
pub struct ScanAudioFilesProgress {
    pub directories_processed: usize,
    pub directories_discovered: usize,
    pub discovered_total: usize,
    pub entries_scanned: usize,
    pub current_directory_entries_scanned: usize,
    pub current_directory_entries_total: usize,
    pub current_path: String,
}

pub fn prepare_track_imports_with_progress<F>(
    request: PrepareTrackImportsRequest,
    mut on_progress: F,
) -> Result<PrepareTrackImportsReport, String>
where
    F: FnMut(PrepareTrackImportsProgress),
{
    let total = request.files.len();
    let mut diagnostics = PrepareTrackImportsDiagnostics::default();
    let mut prepared_tracks = Vec::with_capacity(total);

    for (index, file) in request.files.into_iter().enumerate() {
        let original_path = file
            .original_path
            .filter(|value| !value.trim().is_empty())
            .unwrap_or_else(|| file.source_path.clone());
        let indexed_file = resolve_indexed_file(file.source_path, file.file_name)?;
        let current_file = indexed_file.file_name.clone();
        let metadata_started_at = Instant::now();
        let metadata_request = ParseAudioMetadataRequest {
            path: indexed_file.indexed_path.clone(),
            file_name: Some(indexed_file.file_name.clone()),
        };
        let metadata = match metadata::parse_audio_metadata(metadata_request) {
            Ok(metadata) => metadata,
            Err(_error) => {
                diagnostics.metadata_fallback_count += 1;
                metadata::create_fallback_audio_metadata(
                    Path::new(&indexed_file.indexed_path),
                    Some(&indexed_file.file_name),
                )
            }
        };
        diagnostics.metadata_ms += elapsed_ms(metadata_started_at);

        prepared_tracks.push(create_prepared_track_import(
            &request.library_id,
            index as u32,
            &original_path,
            indexed_file,
            metadata,
        ));

        on_progress(PrepareTrackImportsProgress {
            processed: index + 1,
            total,
            current_file,
        });
    }

    Ok(PrepareTrackImportsReport {
        tracks: prepared_tracks,
        diagnostics,
    })
}

pub fn scan_audio_files_with_progress<F>(
    request: ScanDirectoriesRequest,
    mut on_progress: F,
) -> Result<ScanAudioFilesReport, String>
where
    F: FnMut(ScanAudioFilesProgress),
{
    let mut queue: VecDeque<PathBuf> = request
        .directories
        .into_iter()
        .map(|directory| PathBuf::from(directory.trim()))
        .filter(|directory| directory.is_dir())
        .collect();
    let mut found_files = Vec::new();
    let mut diagnostics = ScanAudioFilesDiagnostics::default();
    let mut directories_processed = 0usize;
    let mut entry_progress_tick = 0usize;

    while let Some(directory) = queue.pop_front() {
        if is_managed_storage_dir(&directory) {
            continue;
        }

        let entries = fs::read_dir(&directory).map_err(|error| {
            format!(
                "Failed to read '{}' during library scan: {error}",
                directory.display()
            )
        })?;
        let entries = entries
            .collect::<Result<Vec<_>, _>>()
            .map_err(|error| format!("Failed to read a scanned directory entry: {error}"))?;
        let directory_entry_total = entries.len();

        for (entry_index, entry) in entries.into_iter().enumerate() {
            let path = entry.path();
            diagnostics.entries_scanned += 1;
            entry_progress_tick += 1;
            let file_type = entry.file_type().map_err(|error| {
                format!("Failed to resolve a scanned directory entry type: {error}")
            })?;

            if file_type.is_dir() {
                queue.push_back(path);
            } else if file_type.is_file() && is_supported_audio_file(&path) {
                let file_name = path
                    .file_name()
                    .map(|name| name.to_string_lossy().to_string())
                    .unwrap_or_else(|| String::from("track"));

                found_files.push(ScannedAudioFile {
                    path: path.to_string_lossy().to_string(),
                    file_name,
                });
            }

            if entry_progress_tick >= 96 {
                entry_progress_tick = 0;
                on_progress(ScanAudioFilesProgress {
                    directories_processed,
                    directories_discovered: directories_processed + queue.len() + 1,
                    discovered_total: found_files.len(),
                    entries_scanned: diagnostics.entries_scanned,
                    current_directory_entries_scanned: entry_index + 1,
                    current_directory_entries_total: directory_entry_total,
                    current_path: directory.to_string_lossy().to_string(),
                });
            }
        }

        directories_processed += 1;
        diagnostics.directories_scanned = directories_processed;
        on_progress(ScanAudioFilesProgress {
            directories_processed,
            directories_discovered: directories_processed + queue.len(),
            discovered_total: found_files.len(),
            entries_scanned: diagnostics.entries_scanned,
            current_directory_entries_scanned: directory_entry_total,
            current_directory_entries_total: directory_entry_total,
            current_path: directory.to_string_lossy().to_string(),
        });
    }

    found_files.sort_by(|left, right| left.path.cmp(&right.path));
    Ok(ScanAudioFilesReport {
        files: found_files,
        diagnostics,
    })
}

fn resolve_indexed_file(
    source_path: String,
    file_name: Option<String>,
) -> Result<IndexedFileResult, String> {
    let source_path = PathBuf::from(source_path.trim());
    let Some(indexed_path) = canonical_existing_file(&source_path)? else {
        return Err(format!(
            "The import source '{}' is no longer available.",
            source_path.display()
        ));
    };

    let file_name = file_name
        .filter(|name| !name.trim().is_empty())
        .or_else(|| {
            indexed_path
                .file_name()
                .map(|name| name.to_string_lossy().to_string())
        })
        .unwrap_or_else(|| String::from("track"));

    Ok(IndexedFileResult {
        source_path: indexed_path.to_string_lossy().to_string(),
        indexed_path: indexed_path.to_string_lossy().to_string(),
        file_name,
    })
}

fn create_prepared_track_import(
    library_id: &str,
    library_order: u32,
    original_path: &str,
    indexed_file: IndexedFileResult,
    metadata: ParsedAudioMetadata,
) -> PreparedTrackImport {
    let title = sanitize_track_title(&metadata.title, &indexed_file.file_name);
    let artist = metadata.artist.trim().to_string();

    PreparedTrackImport {
        id: Uuid::new_v4().to_string(),
        library_id: library_id.to_string(),
        library_order,
        is_favorite: false,
        title: title.clone(),
        artist: artist.clone(),
        album_artist: metadata.album_artist,
        album: metadata.album,
        genre: metadata.genre,
        year: metadata.year,
        track_number: metadata.track_number,
        track_total: metadata.track_total,
        disc_number: metadata.disc_number,
        disc_total: metadata.disc_total,
        composer: metadata.composer,
        lyricist: metadata.lyricist,
        comment: metadata.comment,
        display_title: create_display_title(&title, &artist),
        file_name: indexed_file.file_name.clone(),
        file_size: metadata.file_size,
        size: metadata.file_size,
        duration: metadata.duration,
        format: metadata.format,
        bitrate: metadata.bitrate,
        sample_rate: metadata.sample_rate,
        bit_depth: metadata.bit_depth,
        artwork: metadata.artwork,
        mime_type: resolve_mime_type(&indexed_file.file_name),
        imported_at: None,
        metadata_version: metadata.metadata_version,
        source: PreparedTrackSource {
            kind: String::from("native-file"),
            path: indexed_file.indexed_path,
            origin_path: normalize_optional_text(original_path),
            indexed: true,
        },
    }
}

fn create_display_title(title: &str, artist: &str) -> String {
    let title = normalize_optional_text(title);
    let artist = normalize_optional_text(artist);

    if artist.is_empty() {
        title
    } else {
        format!("{title} - {artist}")
    }
}

fn sanitize_track_title(title: &str, file_name: &str) -> String {
    let normalized_title = normalize_optional_text(title);

    if !normalized_title.is_empty() {
        return normalized_title;
    }

    let from_file_name = Path::new(file_name)
        .file_stem()
        .map(|value| value.to_string_lossy().trim().to_string())
        .unwrap_or_default();

    if from_file_name.is_empty() {
        String::from("Untitled")
    } else {
        from_file_name
    }
}

fn normalize_optional_text(value: &str) -> String {
    value.trim().to_string()
}

fn elapsed_ms(started_at: Instant) -> u64 {
    u64::try_from(started_at.elapsed().as_millis()).unwrap_or(u64::MAX)
}

fn resolve_mime_type(file_name: &str) -> String {
    audio_formats::mime_type_for_extension(
        &Path::new(file_name)
            .extension()
            .map(|value| value.to_string_lossy())
            .unwrap_or_default(),
    )
    .to_string()
}

pub fn managed_storage_display_path(storage_root: &str) -> Option<PathBuf> {
    let trimmed_root = storage_root.trim();

    if trimmed_root.is_empty() {
        return None;
    }

    Some(managed_storage_root(&PathBuf::from(trimmed_root)))
}

pub fn owned_managed_storage_root(storage_root: &str) -> Result<Option<PathBuf>, String> {
    let Some(managed_root) = managed_storage_display_path(storage_root) else {
        return Ok(None);
    };

    if !managed_root.exists() {
        return Ok(None);
    }

    let storage_root = PathBuf::from(storage_root.trim());
    canonical_owned_managed_storage_root(&storage_root, &managed_root).map(Some)
}

pub fn is_managed_storage_marker_file(path: &Path) -> bool {
    path.file_name()
        .map(|name| name.to_string_lossy() == MANAGED_STORAGE_MARKER_FILE_NAME)
        .unwrap_or(false)
}

pub fn clear_managed_storage_root(storage_root: &str) -> Result<Option<PathBuf>, String> {
    let trimmed_root = storage_root.trim();

    if trimmed_root.is_empty() {
        return Ok(None);
    }

    let storage_root = PathBuf::from(trimmed_root);
    let managed_root = managed_storage_root(&storage_root);

    if !managed_root.exists() {
        return Ok(None);
    }

    validate_owned_managed_storage_root(&storage_root, &managed_root)?;

    fs::remove_dir_all(&managed_root).map_err(|error| {
        format!(
            "Failed to clear the OFPlayer managed storage directory '{}': {error}",
            managed_root.display()
        )
    })?;

    Ok(Some(managed_root))
}

#[cfg(test)]
fn cleanup_prepared_import_files(storage_root: &str, tracks: &[Value]) -> Result<usize, String> {
    let trimmed_root = storage_root.trim();

    if trimmed_root.is_empty() {
        return Ok(0);
    }

    let storage_root = PathBuf::from(trimmed_root);
    let managed_root = managed_storage_root(&storage_root);
    let canonical_managed_root =
        match canonical_owned_managed_storage_root(&storage_root, &managed_root) {
            Ok(root) => root,
            Err(_error) if !managed_root.exists() => return Ok(0),
            Err(error) => return Err(error),
        };
    let mut removed = 0usize;

    for track in tracks {
        let Some(staged_path_text) = track
            .get("source")
            .and_then(|source| source.get("path"))
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|value| !value.is_empty())
        else {
            continue;
        };
        let staged_path = PathBuf::from(staged_path_text);

        if path_is_symlink(&staged_path)? {
            return Err(format!(
                "Refusing to remove staged import file '{}' because it is a symbolic link.",
                staged_path.display()
            ));
        }

        let Some(canonical_staged_path) = canonical_existing_file(&staged_path)? else {
            continue;
        };

        if !canonical_staged_path.starts_with(&canonical_managed_root) {
            continue;
        }

        let origin_path = track
            .get("source")
            .and_then(|source| source.get("originPath"))
            .and_then(Value::as_str)
            .map(str::trim)
            .filter(|value| !value.is_empty());

        let origin_matches_staged = origin_path
            .map(PathBuf::from)
            .map(|path| canonical_existing_file(&path))
            .transpose()?
            .flatten()
            .is_some_and(|origin_path| origin_path == canonical_staged_path);

        if origin_matches_staged {
            continue;
        }

        fs::remove_file(&staged_path).map_err(|error| {
            format!(
                "Failed to remove staged import file '{}': {error}",
                staged_path.display()
            )
        })?;
        removed += 1;
    }

    Ok(removed)
}

#[cfg(test)]
fn prepare_managed_storage_root(storage_root: &Path) -> Result<PathBuf, String> {
    validate_storage_root(storage_root)?;
    let managed_root = managed_storage_root(storage_root);

    match fs::symlink_metadata(&managed_root) {
        Ok(metadata) => {
            if metadata.file_type().is_symlink() {
                return Err(format!(
                    "Refusing to use OFPlayer managed storage '{}' because it is a symbolic link.",
                    managed_root.display()
                ));
            }

            if !metadata.is_dir() {
                return Err(format!(
                    "The OFPlayer managed storage path '{}' exists but is not a directory.",
                    managed_root.display()
                ));
            }

            ensure_managed_storage_marker(&managed_root)?;
        }
        Err(error) if error.kind() == ErrorKind::NotFound => {
            fs::create_dir(&managed_root).map_err(|error| {
                format!(
                    "Failed to create OFPlayer managed storage '{}': {error}",
                    managed_root.display()
                )
            })?;
            write_managed_storage_marker(&managed_root)?;
        }
        Err(error) => {
            return Err(format!(
                "Failed to inspect OFPlayer managed storage '{}': {error}",
                managed_root.display()
            ));
        }
    }

    validate_owned_managed_storage_root(storage_root, &managed_root)?;
    Ok(managed_root)
}

fn validate_storage_root(storage_root: &Path) -> Result<(), String> {
    let metadata = fs::symlink_metadata(storage_root).map_err(|error| {
        format!(
            "The selected storage directory '{}' is not available: {error}",
            storage_root.display()
        )
    })?;

    if metadata.file_type().is_symlink() {
        return Err(format!(
            "Refusing to use storage directory '{}' because it is a symbolic link.",
            storage_root.display()
        ));
    }

    if !metadata.is_dir() {
        return Err(String::from(
            "The selected storage directory is not available.",
        ));
    }

    Ok(())
}

#[cfg(test)]
fn ensure_managed_storage_marker(managed_root: &Path) -> Result<(), String> {
    let marker_path = managed_storage_marker_path(managed_root);

    if marker_path.is_file() {
        return validate_managed_storage_marker(managed_root);
    }

    if !managed_storage_directory_is_empty(managed_root)? {
        return Err(format!(
            "Refusing to use existing OFPlayer managed storage '{}' because it does not contain an ownership marker. Choose an empty storage directory or move the existing folder aside before importing.",
            managed_root.display()
        ));
    }

    write_managed_storage_marker(managed_root)
}

fn validate_owned_managed_storage_root(
    storage_root: &Path,
    managed_root: &Path,
) -> Result<(), String> {
    canonical_owned_managed_storage_root(storage_root, managed_root).map(|_| ())
}

fn canonical_owned_managed_storage_root(
    storage_root: &Path,
    managed_root: &Path,
) -> Result<PathBuf, String> {
    validate_storage_root(storage_root)?;

    let metadata = fs::symlink_metadata(managed_root).map_err(|error| {
        format!(
            "Failed to inspect OFPlayer managed storage '{}': {error}",
            managed_root.display()
        )
    })?;

    if metadata.file_type().is_symlink() {
        return Err(format!(
            "Refusing to use OFPlayer managed storage '{}' because it is a symbolic link.",
            managed_root.display()
        ));
    }

    if !metadata.is_dir() {
        return Err(format!(
            "The OFPlayer managed storage path '{}' exists but is not a directory.",
            managed_root.display()
        ));
    }

    validate_managed_storage_marker(managed_root)?;

    let canonical_storage_root = storage_root.canonicalize().map_err(|error| {
        format!(
            "Failed to canonicalize storage directory '{}': {error}",
            storage_root.display()
        )
    })?;
    let canonical_managed_root = managed_root.canonicalize().map_err(|error| {
        format!(
            "Failed to canonicalize OFPlayer managed storage '{}': {error}",
            managed_root.display()
        )
    })?;

    if !canonical_managed_root.starts_with(&canonical_storage_root) {
        return Err(format!(
            "Refusing to use OFPlayer managed storage '{}' because it resolves outside the selected storage directory.",
            managed_root.display()
        ));
    }

    if canonical_managed_root
        .file_name()
        .map(|name| name.to_string_lossy() == MANAGED_STORAGE_DIR_NAME)
        != Some(true)
    {
        return Err(format!(
            "Refusing to use unexpected managed storage path '{}'.",
            canonical_managed_root.display()
        ));
    }

    Ok(canonical_managed_root)
}

#[cfg(test)]
fn write_managed_storage_marker(managed_root: &Path) -> Result<(), String> {
    let marker_path = managed_storage_marker_path(managed_root);
    let marker = json!({
        "app": MANAGED_STORAGE_MARKER_APP,
        "kind": MANAGED_STORAGE_MARKER_KIND,
        "version": MANAGED_STORAGE_MARKER_VERSION,
    });
    let marker_json = serde_json::to_vec_pretty(&marker)
        .map_err(|error| format!("Failed to encode OFPlayer managed storage marker: {error}"))?;

    fs::write(&marker_path, marker_json).map_err(|error| {
        format!(
            "Failed to write OFPlayer managed storage marker '{}': {error}",
            marker_path.display()
        )
    })
}

fn validate_managed_storage_marker(managed_root: &Path) -> Result<(), String> {
    let marker_path = managed_storage_marker_path(managed_root);
    let marker_text = fs::read_to_string(&marker_path).map_err(|error| {
        if error.kind() == ErrorKind::NotFound {
            format!(
                "Refusing to use OFPlayer managed storage '{}' because it does not contain an ownership marker.",
                managed_root.display()
            )
        } else {
            format!(
                "Failed to read OFPlayer managed storage marker '{}': {error}",
                marker_path.display()
            )
        }
    })?;
    let marker: Value = serde_json::from_str(&marker_text).map_err(|error| {
        format!(
            "Failed to parse OFPlayer managed storage marker '{}': {error}",
            marker_path.display()
        )
    })?;
    let valid = marker.get("app").and_then(Value::as_str) == Some(MANAGED_STORAGE_MARKER_APP)
        && marker.get("kind").and_then(Value::as_str) == Some(MANAGED_STORAGE_MARKER_KIND)
        && marker
            .get("version")
            .and_then(Value::as_u64)
            .is_some_and(|version| version >= 1);

    if valid {
        Ok(())
    } else {
        Err(format!(
            "Refusing to use OFPlayer managed storage '{}' because its ownership marker is invalid.",
            managed_root.display()
        ))
    }
}

#[cfg(test)]
fn managed_storage_directory_is_empty(managed_root: &Path) -> Result<bool, String> {
    let mut entries = fs::read_dir(managed_root).map_err(|error| {
        format!(
            "Failed to inspect OFPlayer managed storage '{}': {error}",
            managed_root.display()
        )
    })?;

    Ok(entries.next().is_none())
}

fn managed_storage_marker_path(managed_root: &Path) -> PathBuf {
    managed_root.join(MANAGED_STORAGE_MARKER_FILE_NAME)
}

#[cfg(test)]
fn path_is_symlink(path: &Path) -> Result<bool, String> {
    match fs::symlink_metadata(path) {
        Ok(metadata) => Ok(metadata.file_type().is_symlink()),
        Err(error) if error.kind() == ErrorKind::NotFound => Ok(false),
        Err(error) => Err(format!("Failed to inspect '{}': {error}", path.display())),
    }
}

fn canonical_existing_file(path: &Path) -> Result<Option<PathBuf>, String> {
    match fs::symlink_metadata(path) {
        Ok(metadata) => {
            if metadata.file_type().is_symlink() {
                return Err(format!(
                    "Refusing to use '{}' because it is a symbolic link.",
                    path.display()
                ));
            }

            if !metadata.is_file() {
                return Ok(None);
            }
        }
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(format!("Failed to inspect '{}': {error}", path.display())),
    }

    path.canonicalize()
        .map(Some)
        .map_err(|error| format!("Failed to canonicalize '{}': {error}", path.display()))
}

pub fn is_supported_audio_file(path: &Path) -> bool {
    audio_formats::is_supported_audio_path(path)
}

pub fn is_path_inside_managed_storage(path: &Path, storage_root: &Path) -> bool {
    path.starts_with(managed_storage_root(storage_root))
}

fn managed_storage_root(storage_root: &Path) -> PathBuf {
    storage_root.join(MANAGED_STORAGE_DIR_NAME)
}

fn is_managed_storage_dir(path: &Path) -> bool {
    path.file_name()
        .map(|name| {
            name.to_string_lossy()
                .eq_ignore_ascii_case(MANAGED_STORAGE_DIR_NAME)
        })
        .unwrap_or(false)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::env;

    fn temp_storage_root(label: &str) -> PathBuf {
        let root = env::temp_dir().join(format!("ofplayer-storage-{label}-{}", Uuid::new_v4()));
        fs::create_dir_all(&root).expect("test storage root should be created");
        root
    }

    fn cleanup(root: &Path) {
        let _ = fs::remove_dir_all(root);
    }

    fn truncated_wav_fixture() -> Vec<u8> {
        let mut bytes = Vec::new();
        bytes.extend_from_slice(b"RIFF");
        bytes.extend_from_slice(&48u32.to_le_bytes());
        bytes.extend_from_slice(b"WAVE");
        bytes.extend_from_slice(b"fmt ");
        bytes.extend_from_slice(&16u32.to_le_bytes());
        bytes.extend_from_slice(&1u16.to_le_bytes());
        bytes.extend_from_slice(&1u16.to_le_bytes());
        bytes.extend_from_slice(&44_100u32.to_le_bytes());
        bytes.extend_from_slice(&88_200u32.to_le_bytes());
        bytes.extend_from_slice(&2u16.to_le_bytes());
        bytes.extend_from_slice(&16u16.to_le_bytes());
        bytes.extend_from_slice(b"data");
        bytes.extend_from_slice(&12u32.to_le_bytes());
        bytes
    }

    #[test]
    fn prepare_track_imports_falls_back_when_metadata_read_fails() {
        let root = temp_storage_root("metadata-fallback");
        let track_path = root.join("Broken Metadata.wav");
        fs::write(&track_path, b"not a real wave file")
            .expect("invalid audio fixture should be writable");
        let mut progress = Vec::new();

        let report = prepare_track_imports_with_progress(
            PrepareTrackImportsRequest {
                library_id: String::from("library-1"),
                files: vec![PrepareTrackImportInput {
                    source_path: track_path.to_string_lossy().to_string(),
                    file_name: None,
                    original_path: None,
                }],
            },
            |next_progress| progress.push(next_progress),
        )
        .expect("metadata failures should not abort import preparation");

        assert_eq!(report.tracks.len(), 1);
        assert_eq!(report.diagnostics.metadata_fallback_count, 1);
        assert_eq!(report.tracks[0].title, "Broken Metadata");
        assert_eq!(report.tracks[0].format, "WAV");
        assert_eq!(
            report.tracks[0].file_size,
            b"not a real wave file".len() as u64
        );
        assert_eq!(report.tracks[0].metadata_version, 0);
        assert_eq!(progress.len(), 1);
        assert_eq!(progress[0].processed, 1);

        cleanup(&root);
    }

    #[test]
    fn prepare_track_imports_falls_back_when_wav_buffer_is_truncated() {
        let root = temp_storage_root("truncated-wav-metadata-fallback");
        let track_path = root.join("Truncated Wave.wav");
        fs::write(&track_path, truncated_wav_fixture())
            .expect("truncated wav fixture should be writable");

        let report = prepare_track_imports_with_progress(
            PrepareTrackImportsRequest {
                library_id: String::from("library-1"),
                files: vec![PrepareTrackImportInput {
                    source_path: track_path.to_string_lossy().to_string(),
                    file_name: None,
                    original_path: None,
                }],
            },
            |_| {},
        )
        .expect("truncated wav metadata should not abort import preparation");

        assert_eq!(report.tracks.len(), 1);
        assert_eq!(report.diagnostics.metadata_fallback_count, 1);
        assert_eq!(report.tracks[0].title, "Truncated Wave");
        assert_eq!(report.tracks[0].format, "WAV");
        assert_eq!(report.tracks[0].metadata_version, 0);

        cleanup(&root);
    }

    #[test]
    fn prepare_managed_storage_root_writes_marker_and_clear_only_removes_managed_dir() {
        let root = temp_storage_root("prepare-clear");
        fs::write(root.join("keep.txt"), b"keep").expect("sibling fixture should be writable");

        let managed_root =
            prepare_managed_storage_root(&root).expect("managed root should be prepared");

        assert!(managed_storage_marker_path(&managed_root).is_file());
        assert!(managed_root.is_dir());

        let cleared = clear_managed_storage_root(root.to_string_lossy().as_ref())
            .expect("owned managed storage should be clearable");

        assert_eq!(cleared, Some(managed_root.clone()));
        assert!(!managed_root.exists());
        assert!(root.join("keep.txt").is_file());

        cleanup(&root);
    }

    #[test]
    fn clear_managed_storage_root_refuses_unmarked_existing_directory() {
        let root = temp_storage_root("unmarked-clear");
        let managed_root = managed_storage_root(&root);
        fs::create_dir_all(&managed_root).expect("managed fixture should be created");
        fs::write(managed_root.join("user-file.txt"), b"user")
            .expect("user fixture should be writable");

        let error = clear_managed_storage_root(root.to_string_lossy().as_ref())
            .expect_err("unmarked storage must not be cleared");

        assert!(error.contains("ownership marker"));
        assert!(managed_root.join("user-file.txt").is_file());

        cleanup(&root);
    }

    #[test]
    fn cleanup_prepared_import_files_only_removes_owned_managed_files() {
        let root = temp_storage_root("cleanup-owned");
        let managed_root =
            prepare_managed_storage_root(&root).expect("managed root should be prepared");
        let library_root = managed_root.join(LIBRARIES_DIR_NAME).join("library");
        fs::create_dir_all(&library_root).expect("library fixture should be created");
        let staged_path = library_root.join("track.mp3");
        fs::write(&staged_path, b"staged").expect("staged fixture should be writable");
        let outside_path = root.join("outside.mp3");
        fs::write(&outside_path, b"outside").expect("outside fixture should be writable");
        let tracks = vec![
            json!({
                "source": {
                    "path": staged_path.to_string_lossy(),
                    "originPath": outside_path.to_string_lossy()
                }
            }),
            json!({
                "source": {
                    "path": outside_path.to_string_lossy(),
                    "originPath": outside_path.to_string_lossy()
                }
            }),
        ];

        let removed = cleanup_prepared_import_files(root.to_string_lossy().as_ref(), &tracks)
            .expect("owned staged cleanup should succeed");

        assert_eq!(removed, 1);
        assert!(!staged_path.exists());
        assert!(outside_path.is_file());

        cleanup(&root);
    }

    #[test]
    fn cleanup_prepared_import_files_keeps_origin_when_it_matches_staged_path() {
        let root = temp_storage_root("cleanup-origin");
        let managed_root =
            prepare_managed_storage_root(&root).expect("managed root should be prepared");
        let library_root = managed_root.join(LIBRARIES_DIR_NAME).join("library");
        fs::create_dir_all(&library_root).expect("library fixture should be created");
        let staged_path = library_root.join("track.mp3");
        fs::write(&staged_path, b"staged").expect("staged fixture should be writable");
        let tracks = vec![json!({
            "source": {
                "path": staged_path.to_string_lossy(),
                "originPath": staged_path.to_string_lossy()
            }
        })];

        let removed = cleanup_prepared_import_files(root.to_string_lossy().as_ref(), &tracks)
            .expect("cleanup should succeed");

        assert_eq!(removed, 0);
        assert!(staged_path.is_file());

        cleanup(&root);
    }
}
