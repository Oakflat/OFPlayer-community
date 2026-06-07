use crate::{
    app_paths, artwork_store,
    db_helpers::{current_iso_timestamp, elapsed_ms, load_payloads},
    desktop_types::{
        StorageGarbageCollectionItem, StorageGarbageCollectionResult, StorageUsageItem,
        StorageUsageSnapshot,
    },
    storage,
};
use rusqlite::Connection;
use serde_json::{json, Value};
use std::{
    collections::HashSet,
    fs,
    io::ErrorKind,
    path::{Path, PathBuf},
    time::Instant,
};

const CAPSULE_ARTWORK_CACHE_DIR: &str = "lyric-capsule-artwork";
const EXTERNAL_PLAYBACK_CACHE_DIR: &str = "external-sources";
const DIAGNOSTICS_ARCHIVE_FILE_NAME: &str = "ofplayer-diagnostics.previous.ndjson";

#[derive(Debug, Clone, Copy, Default)]
struct DirectoryFootprint {
    bytes: u64,
    file_count: usize,
    directory_count: usize,
}

impl DirectoryFootprint {
    fn add_file(&mut self, bytes: u64) {
        self.bytes = self.bytes.saturating_add(bytes);
        self.file_count += 1;
    }

    fn add_directory(&mut self) {
        self.directory_count += 1;
    }

    fn add(&mut self, other: DirectoryFootprint) {
        self.bytes = self.bytes.saturating_add(other.bytes);
        self.file_count += other.file_count;
        self.directory_count += other.directory_count;
    }
}

#[derive(Debug, Clone)]
struct FileRecord {
    path: PathBuf,
    canonical_path: Option<PathBuf>,
    bytes: u64,
}

#[derive(Debug, Default)]
struct DirectoryScan {
    footprint: DirectoryFootprint,
    files: Vec<FileRecord>,
    warnings: Vec<String>,
}

pub(crate) fn analyze_storage_usage(
    connection: &Connection,
    database_path: &Path,
    preferences: Option<&Value>,
) -> Result<StorageUsageSnapshot, String> {
    let track_source_paths = load_track_source_paths(connection)?;
    Ok(build_storage_usage_snapshot(
        database_path,
        preferences,
        &track_source_paths,
    ))
}

pub(crate) fn collect_storage_garbage(
    connection: &Connection,
    database_path: &Path,
    preferences: Option<&Value>,
) -> Result<StorageGarbageCollectionResult, String> {
    let started_at = current_iso_timestamp();
    let started = Instant::now();
    let track_source_paths = load_track_source_paths(connection)?;
    let before = build_storage_usage_snapshot(database_path, preferences, &track_source_paths);
    let mut warnings = before.warnings.clone();
    let items = vec![
        collect_managed_storage_orphans(preferences, &track_source_paths, &mut warnings),
        compact_database(connection, database_path, &mut warnings),
        clear_cache_subdir(
            CAPSULE_ARTWORK_CACHE_DIR,
            "capsuleArtworkCache",
            &mut warnings,
        ),
        clear_cache_subdir(
            EXTERNAL_PLAYBACK_CACHE_DIR,
            "externalPlaybackCache",
            &mut warnings,
        ),
        clear_diagnostics_archive(&mut warnings),
    ];

    let after = build_storage_usage_snapshot(database_path, preferences, &track_source_paths);
    warnings.extend(after.warnings.clone());
    let reclaimed_bytes = items.iter().fold(0u64, |total, item| {
        total
            .saturating_add(item.removed_bytes)
            .saturating_add(item.compacted_bytes)
    });
    let removed_files = items.iter().map(|item| item.removed_files).sum();
    let removed_directories = items.iter().map(|item| item.removed_directories).sum();

    Ok(StorageGarbageCollectionResult {
        started_at,
        completed_at: current_iso_timestamp(),
        total_ms: elapsed_ms(started),
        before,
        after,
        reclaimed_bytes,
        removed_files,
        removed_directories,
        items,
        warnings,
    })
}

fn build_storage_usage_snapshot(
    database_path: &Path,
    preferences: Option<&Value>,
    track_source_paths: &[String],
) -> StorageUsageSnapshot {
    let mut warnings = Vec::new();
    let items = vec![
        analyze_managed_storage_item(preferences, track_source_paths, &mut warnings),
        analyze_database_item(database_path),
        analyze_track_artwork_assets_item(&mut warnings),
        analyze_cache_subdir_item(
            CAPSULE_ARTWORK_CACHE_DIR,
            "capsuleArtworkCache",
            CacheReclaimPolicy::All,
            &mut warnings,
        ),
        analyze_cache_subdir_item(
            EXTERNAL_PLAYBACK_CACHE_DIR,
            "externalPlaybackCache",
            CacheReclaimPolicy::All,
            &mut warnings,
        ),
        analyze_other_cache_item(&mut warnings),
        analyze_diagnostics_item(&mut warnings),
    ];

    let total_bytes = items
        .iter()
        .fold(0u64, |total, item| total.saturating_add(item.bytes));
    let reclaimable_bytes = items.iter().fold(0u64, |total, item| {
        total.saturating_add(item.reclaimable_bytes)
    });

    StorageUsageSnapshot {
        generated_at: current_iso_timestamp(),
        total_bytes,
        reclaimable_bytes,
        items,
        warnings,
    }
}

fn load_track_source_paths(connection: &Connection) -> Result<Vec<String>, String> {
    let tracks = load_payloads(
        connection,
        "SELECT payload_json FROM tracks",
        [],
        "desktop track storage source",
    )?;

    Ok(tracks
        .iter()
        .filter_map(|track| track_source_text(track, "path"))
        .collect())
}

fn track_source_text(track: &Value, field: &str) -> Option<String> {
    track
        .get("source")
        .and_then(Value::as_object)
        .and_then(|source| source.get(field))
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(String::from)
}

fn storage_root_from_preferences(preferences: Option<&Value>) -> Option<String> {
    preferences
        .and_then(|value| value.get("storageRoot"))
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(String::from)
}

fn analyze_managed_storage_item(
    preferences: Option<&Value>,
    track_source_paths: &[String],
    warnings: &mut Vec<String>,
) -> StorageUsageItem {
    let storage_root = storage_root_from_preferences(preferences);
    let display_path = storage_root
        .as_deref()
        .and_then(storage::managed_storage_display_path);
    let Some(managed_root) = resolve_owned_managed_root(storage_root.as_deref(), warnings) else {
        return usage_item(
            "managedAudio",
            display_path.as_deref(),
            DirectoryFootprint::default(),
            0,
            0,
            Some(json!({
                "configured": storage_root.is_some(),
                "referencedBytes": 0,
                "referencedFileCount": 0,
                "orphanedBytes": 0,
                "orphanedFileCount": 0,
            })),
        );
    };

    let scan = scan_path(&managed_root);
    warnings.extend(scan.warnings);
    let referenced_paths = canonical_referenced_managed_paths(track_source_paths, &managed_root);
    let mut referenced_bytes = 0u64;
    let mut referenced_file_count = 0usize;
    let mut orphaned_bytes = 0u64;
    let mut orphaned_file_count = 0usize;

    for file in &scan.files {
        if storage::is_managed_storage_marker_file(&file.path) {
            continue;
        }

        if file
            .canonical_path
            .as_ref()
            .is_some_and(|path| referenced_paths.contains(path))
        {
            referenced_bytes = referenced_bytes.saturating_add(file.bytes);
            referenced_file_count += 1;
        } else {
            orphaned_bytes = orphaned_bytes.saturating_add(file.bytes);
            orphaned_file_count += 1;
        }
    }

    usage_item(
        "managedAudio",
        Some(&managed_root),
        scan.footprint,
        orphaned_bytes,
        orphaned_file_count,
        Some(json!({
            "configured": true,
            "referencedBytes": referenced_bytes,
            "referencedFileCount": referenced_file_count,
            "orphanedBytes": orphaned_bytes,
            "orphanedFileCount": orphaned_file_count,
        })),
    )
}

fn analyze_database_item(database_path: &Path) -> StorageUsageItem {
    let mut footprint = DirectoryFootprint::default();
    let mut wal_bytes = 0u64;
    let mut files = Vec::new();

    for (kind, path) in database_related_paths(database_path) {
        let bytes = file_size(&path).unwrap_or(0);

        if bytes > 0 {
            footprint.add_file(bytes);
        }

        if kind == "wal" {
            wal_bytes = bytes;
        }

        files.push(json!({
            "kind": kind,
            "path": display_path(&path),
            "bytes": bytes,
        }));
    }

    usage_item(
        "database",
        Some(database_path),
        footprint,
        wal_bytes,
        usize::from(wal_bytes > 0),
        Some(json!({ "files": files })),
    )
}

fn analyze_track_artwork_assets_item(warnings: &mut Vec<String>) -> StorageUsageItem {
    let Some(path) = artwork_store::track_artwork_asset_dir()
        .map_err(|error| warnings.push(error))
        .ok()
    else {
        return usage_item(
            "trackArtworkAssets",
            None,
            DirectoryFootprint::default(),
            0,
            0,
            None,
        );
    };
    let scan = scan_path(&path);
    warnings.extend(scan.warnings);

    usage_item(
        "trackArtworkAssets",
        Some(&path),
        scan.footprint,
        0,
        0,
        None,
    )
}

enum CacheReclaimPolicy {
    All,
}

fn analyze_cache_subdir_item(
    dir_name: &str,
    key: &str,
    policy: CacheReclaimPolicy,
    warnings: &mut Vec<String>,
) -> StorageUsageItem {
    let Some(path) = cache_path(dir_name, warnings) else {
        return usage_item(key, None, DirectoryFootprint::default(), 0, 0, None);
    };
    let scan = scan_path(&path);
    warnings.extend(scan.warnings);
    let reclaimable = match policy {
        CacheReclaimPolicy::All => scan.footprint,
    };

    usage_item(
        key,
        Some(&path),
        scan.footprint,
        reclaimable.bytes,
        reclaimable.file_count,
        None,
    )
}

fn analyze_other_cache_item(warnings: &mut Vec<String>) -> StorageUsageItem {
    let mut footprint = DirectoryFootprint::default();
    let Some(cache_root) = cache_root_path(warnings) else {
        return usage_item(
            "otherCache",
            None,
            DirectoryFootprint::default(),
            0,
            0,
            None,
        );
    };
    let known_dirs = HashSet::from([CAPSULE_ARTWORK_CACHE_DIR, EXTERNAL_PLAYBACK_CACHE_DIR]);

    match fs::read_dir(&cache_root) {
        Ok(entries) => {
            for entry in entries {
                let Ok(entry) = entry else {
                    warnings.push(String::from(
                        "Failed to read one OFPlayer cache directory entry.",
                    ));
                    continue;
                };
                let file_name = entry.file_name().to_string_lossy().to_string();

                if known_dirs.contains(file_name.as_str()) {
                    continue;
                }

                let scan = scan_path(&entry.path());
                warnings.extend(scan.warnings);
                footprint.add(scan.footprint);
            }
        }
        Err(error) if error.kind() == ErrorKind::NotFound => {}
        Err(error) => warnings.push(format!(
            "Failed to inspect OFPlayer cache directory '{}': {error}",
            cache_root.display()
        )),
    }

    usage_item("otherCache", Some(&cache_root), footprint, 0, 0, None)
}

fn analyze_diagnostics_item(warnings: &mut Vec<String>) -> StorageUsageItem {
    let Some(path) = diagnostics_path(warnings) else {
        return usage_item(
            "diagnostics",
            None,
            DirectoryFootprint::default(),
            0,
            0,
            None,
        );
    };
    let scan = scan_path(&path);
    warnings.extend(scan.warnings);
    let archive_path = path.join(DIAGNOSTICS_ARCHIVE_FILE_NAME);
    let archive_bytes = file_size(&archive_path).unwrap_or(0);

    usage_item(
        "diagnostics",
        Some(&path),
        scan.footprint,
        archive_bytes,
        usize::from(archive_bytes > 0),
        Some(json!({
            "archiveBytes": archive_bytes,
            "archivePath": display_path(&archive_path),
        })),
    )
}

fn collect_managed_storage_orphans(
    preferences: Option<&Value>,
    track_source_paths: &[String],
    warnings: &mut Vec<String>,
) -> StorageGarbageCollectionItem {
    let Some(managed_root) = resolve_owned_managed_root(
        storage_root_from_preferences(preferences).as_deref(),
        warnings,
    ) else {
        return gc_item("managedAudio", 0, 0, 0, 0);
    };
    let scan = scan_path(&managed_root);
    warnings.extend(scan.warnings);
    let referenced_paths = canonical_referenced_managed_paths(track_source_paths, &managed_root);
    let mut removed_bytes = 0u64;
    let mut removed_files = 0usize;

    for file in scan.files {
        if storage::is_managed_storage_marker_file(&file.path) {
            continue;
        }

        if file
            .canonical_path
            .as_ref()
            .is_some_and(|path| referenced_paths.contains(path))
        {
            continue;
        }

        match fs::remove_file(&file.path) {
            Ok(()) => {
                removed_bytes = removed_bytes.saturating_add(file.bytes);
                removed_files += 1;
            }
            Err(error) if error.kind() == ErrorKind::NotFound => {}
            Err(error) => warnings.push(format!(
                "Failed to remove orphaned managed file '{}': {error}",
                file.path.display()
            )),
        }
    }

    let removed_directories = remove_empty_child_directories(&managed_root, warnings);
    gc_item(
        "managedAudio",
        removed_bytes,
        removed_files,
        removed_directories,
        0,
    )
}

fn compact_database(
    connection: &Connection,
    database_path: &Path,
    warnings: &mut Vec<String>,
) -> StorageGarbageCollectionItem {
    let before = database_related_paths(database_path)
        .iter()
        .fold(0u64, |total, (_, path)| {
            total.saturating_add(file_size(path).unwrap_or(0))
        });

    if let Err(error) = connection.execute_batch(
        "PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA wal_checkpoint(TRUNCATE); PRAGMA optimize;",
    ) {
        warnings.push(format!(
            "Failed to compact the OFPlayer desktop database '{}': {error}",
            database_path.display()
        ));
    }

    let after = database_related_paths(database_path)
        .iter()
        .fold(0u64, |total, (_, path)| {
            total.saturating_add(file_size(path).unwrap_or(0))
        });
    let compacted_bytes = before.saturating_sub(after);

    gc_item("database", 0, 0, 0, compacted_bytes)
}

fn clear_cache_subdir(
    dir_name: &str,
    key: &str,
    warnings: &mut Vec<String>,
) -> StorageGarbageCollectionItem {
    let Some(path) = cache_path(dir_name, warnings) else {
        return gc_item(key, 0, 0, 0, 0);
    };

    clear_directory_contents(&path, key, warnings)
}

fn clear_diagnostics_archive(warnings: &mut Vec<String>) -> StorageGarbageCollectionItem {
    let Some(path) = diagnostics_path(warnings) else {
        return gc_item("diagnostics", 0, 0, 0, 0);
    };
    let archive_path = path.join(DIAGNOSTICS_ARCHIVE_FILE_NAME);
    let archive_bytes = file_size(&archive_path).unwrap_or(0);

    if archive_bytes == 0 {
        return gc_item("diagnostics", 0, 0, 0, 0);
    }

    match fs::remove_file(&archive_path) {
        Ok(()) => gc_item("diagnostics", archive_bytes, 1, 0, 0),
        Err(error) if error.kind() == ErrorKind::NotFound => gc_item("diagnostics", 0, 0, 0, 0),
        Err(error) => {
            warnings.push(format!(
                "Failed to remove diagnostics archive '{}': {error}",
                archive_path.display()
            ));
            gc_item("diagnostics", 0, 0, 0, 0)
        }
    }
}

fn clear_directory_contents(
    path: &Path,
    key: &str,
    warnings: &mut Vec<String>,
) -> StorageGarbageCollectionItem {
    let before = scan_path(path);
    warnings.extend(before.warnings);
    let mut removed_bytes = 0u64;
    let mut removed_files = 0usize;
    let mut removed_directories = 0usize;

    match fs::read_dir(path) {
        Ok(entries) => {
            for entry in entries {
                let Ok(entry) = entry else {
                    warnings.push(format!(
                        "Failed to read an OFPlayer cache entry under '{}'.",
                        path.display()
                    ));
                    continue;
                };
                let entry_path = entry.path();
                let entry_scan = scan_path(&entry_path);
                let footprint = entry_scan.footprint;
                warnings.extend(entry_scan.warnings);
                let metadata = match fs::symlink_metadata(&entry_path) {
                    Ok(metadata) => metadata,
                    Err(error) if error.kind() == ErrorKind::NotFound => continue,
                    Err(error) => {
                        warnings.push(format!(
                            "Failed to inspect cache entry '{}': {error}",
                            entry_path.display()
                        ));
                        continue;
                    }
                };
                let remove_result = if metadata.is_dir() && !metadata.file_type().is_symlink() {
                    fs::remove_dir_all(&entry_path)
                } else {
                    fs::remove_file(&entry_path)
                };

                match remove_result {
                    Ok(()) => {
                        removed_bytes = removed_bytes.saturating_add(footprint.bytes);
                        removed_files += footprint.file_count;
                        removed_directories += footprint.directory_count;
                    }
                    Err(error) if error.kind() == ErrorKind::NotFound => {}
                    Err(error) => warnings.push(format!(
                        "Failed to remove cache entry '{}': {error}",
                        entry_path.display()
                    )),
                }
            }
        }
        Err(error) if error.kind() == ErrorKind::NotFound => {}
        Err(error) => warnings.push(format!(
            "Failed to inspect OFPlayer cache directory '{}': {error}",
            path.display()
        )),
    }

    gc_item(key, removed_bytes, removed_files, removed_directories, 0)
}

fn resolve_owned_managed_root(
    storage_root: Option<&str>,
    warnings: &mut Vec<String>,
) -> Option<PathBuf> {
    let storage_root = storage_root?;

    match storage::owned_managed_storage_root(storage_root) {
        Ok(root) => root,
        Err(error) => {
            warnings.push(error);
            None
        }
    }
}

fn canonical_referenced_managed_paths(
    track_source_paths: &[String],
    managed_root: &Path,
) -> HashSet<PathBuf> {
    let mut paths = HashSet::new();

    for source_path in track_source_paths {
        let path = PathBuf::from(source_path);
        let Ok(metadata) = fs::symlink_metadata(&path) else {
            continue;
        };

        if metadata.file_type().is_symlink() || !metadata.is_file() {
            continue;
        }

        let Ok(canonical_path) = path.canonicalize() else {
            continue;
        };

        if canonical_path.starts_with(managed_root) {
            paths.insert(canonical_path);
        }
    }

    paths
}

fn scan_path(path: &Path) -> DirectoryScan {
    let mut scan = DirectoryScan::default();
    let metadata = match fs::symlink_metadata(path) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == ErrorKind::NotFound => return scan,
        Err(error) => {
            scan.warnings.push(format!(
                "Failed to inspect OFPlayer storage path '{}': {error}",
                path.display()
            ));
            return scan;
        }
    };

    if metadata.file_type().is_symlink() {
        scan.warnings.push(format!(
            "Skipped symbolic link while analyzing OFPlayer storage '{}'.",
            path.display()
        ));
        return scan;
    }

    if metadata.is_file() {
        push_file_record(path, &metadata, &mut scan);
        return scan;
    }

    if metadata.is_dir() {
        scan.footprint.add_directory();
        scan_directory(path, &mut scan);
    }

    scan
}

fn scan_directory(path: &Path, scan: &mut DirectoryScan) {
    let entries = match fs::read_dir(path) {
        Ok(entries) => entries,
        Err(error) if error.kind() == ErrorKind::NotFound => return,
        Err(error) => {
            scan.warnings.push(format!(
                "Failed to read OFPlayer storage directory '{}': {error}",
                path.display()
            ));
            return;
        }
    };

    for entry in entries {
        let entry = match entry {
            Ok(entry) => entry,
            Err(error) => {
                scan.warnings
                    .push(format!("Failed to read an OFPlayer storage entry: {error}"));
                continue;
            }
        };
        let entry_path = entry.path();
        let metadata = match fs::symlink_metadata(&entry_path) {
            Ok(metadata) => metadata,
            Err(error) if error.kind() == ErrorKind::NotFound => continue,
            Err(error) => {
                scan.warnings.push(format!(
                    "Failed to inspect OFPlayer storage path '{}': {error}",
                    entry_path.display()
                ));
                continue;
            }
        };

        if metadata.file_type().is_symlink() {
            scan.warnings.push(format!(
                "Skipped symbolic link while analyzing OFPlayer storage '{}'.",
                entry_path.display()
            ));
        } else if metadata.is_file() {
            push_file_record(&entry_path, &metadata, scan);
        } else if metadata.is_dir() {
            scan.footprint.add_directory();
            scan_directory(&entry_path, scan);
        }
    }
}

fn push_file_record(path: &Path, metadata: &fs::Metadata, scan: &mut DirectoryScan) {
    let bytes = metadata.len();
    let canonical_path = path.canonicalize().ok();

    scan.footprint.add_file(bytes);
    scan.files.push(FileRecord {
        path: path.to_path_buf(),
        canonical_path,
        bytes,
    });
}

fn remove_empty_child_directories(root: &Path, warnings: &mut Vec<String>) -> usize {
    let mut directories = Vec::new();
    collect_child_directories(root, &mut directories, warnings);
    directories.sort_by(|left, right| {
        right
            .components()
            .count()
            .cmp(&left.components().count())
            .then_with(|| right.cmp(left))
    });

    let mut removed = 0usize;

    for directory in directories {
        if directory == root {
            continue;
        }

        match fs::remove_dir(&directory) {
            Ok(()) => removed += 1,
            Err(error) if error.kind() == ErrorKind::NotFound => {}
            Err(error) if error.kind() == ErrorKind::DirectoryNotEmpty => {}
            Err(error) => warnings.push(format!(
                "Failed to remove empty OFPlayer directory '{}': {error}",
                directory.display()
            )),
        }
    }

    removed
}

fn collect_child_directories(
    root: &Path,
    directories: &mut Vec<PathBuf>,
    warnings: &mut Vec<String>,
) {
    let entries = match fs::read_dir(root) {
        Ok(entries) => entries,
        Err(error) if error.kind() == ErrorKind::NotFound => return,
        Err(error) => {
            warnings.push(format!(
                "Failed to read OFPlayer directory '{}': {error}",
                root.display()
            ));
            return;
        }
    };

    for entry in entries {
        let Ok(entry) = entry else {
            warnings.push(String::from("Failed to read an OFPlayer directory entry."));
            continue;
        };
        let path = entry.path();
        let Ok(metadata) = fs::symlink_metadata(&path) else {
            continue;
        };

        if metadata.is_dir() && !metadata.file_type().is_symlink() {
            directories.push(path.clone());
            collect_child_directories(&path, directories, warnings);
        }
    }
}

fn database_related_paths(database_path: &Path) -> Vec<(&'static str, PathBuf)> {
    vec![
        ("main", database_path.to_path_buf()),
        ("wal", database_sidecar_path(database_path, "wal")),
        ("shm", database_sidecar_path(database_path, "shm")),
    ]
}

fn database_sidecar_path(database_path: &Path, suffix: &str) -> PathBuf {
    let file_name = database_path
        .file_name()
        .map(|value| value.to_string_lossy().to_string())
        .unwrap_or_else(|| String::from("ofplayer-desktop.sqlite3"));

    database_path.with_file_name(format!("{file_name}-{suffix}"))
}

fn file_size(path: &Path) -> Option<u64> {
    fs::symlink_metadata(path)
        .ok()
        .filter(|metadata| metadata.is_file() && !metadata.file_type().is_symlink())
        .map(|metadata| metadata.len())
}

fn cache_root_path(warnings: &mut Vec<String>) -> Option<PathBuf> {
    app_paths::cache_dir()
        .map_err(|error| warnings.push(error))
        .ok()
}

fn cache_path(name: &str, warnings: &mut Vec<String>) -> Option<PathBuf> {
    cache_root_path(warnings).map(|root| root.join(name))
}

fn diagnostics_path(warnings: &mut Vec<String>) -> Option<PathBuf> {
    app_paths::diagnostics_dir()
        .map_err(|error| warnings.push(error))
        .ok()
}

fn usage_item(
    key: &str,
    path: Option<&Path>,
    footprint: DirectoryFootprint,
    reclaimable_bytes: u64,
    reclaimable_file_count: usize,
    details: Option<Value>,
) -> StorageUsageItem {
    StorageUsageItem {
        key: String::from(key),
        path: path.map(display_path),
        bytes: footprint.bytes,
        file_count: footprint.file_count,
        directory_count: footprint.directory_count,
        reclaimable_bytes,
        reclaimable_file_count,
        details,
    }
}

fn gc_item(
    key: &str,
    removed_bytes: u64,
    removed_files: usize,
    removed_directories: usize,
    compacted_bytes: u64,
) -> StorageGarbageCollectionItem {
    StorageGarbageCollectionItem {
        key: String::from(key),
        removed_bytes,
        removed_files,
        removed_directories,
        compacted_bytes,
    }
}

fn display_path(path: &Path) -> String {
    path.display().to_string()
}
