use notify::{recommended_watcher, Event, EventKind, RecommendedWatcher, RecursiveMode, Watcher};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};
use tauri::{AppHandle, Emitter};

use crate::storage::{is_path_inside_managed_storage, is_supported_audio_file};

const STORAGE_WATCH_EVENT: &str = "storage://watch-changed";

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConfigureStorageWatchRequest {
    pub storage_root: String,
    pub directories: Vec<String>,
    pub enabled: bool,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct StorageWatchSnapshot {
    pub enabled: bool,
    pub directories: Vec<String>,
}

#[derive(Debug, Serialize, Clone)]
#[serde(rename_all = "camelCase")]
struct StorageWatchEventPayload {
    pub kind: String,
    pub paths: Vec<String>,
}

#[derive(Default)]
pub struct StorageWatchManager {
    watcher: Option<RecommendedWatcher>,
    directories: Vec<String>,
}

impl StorageWatchManager {
    pub fn configure(
        &mut self,
        app: &AppHandle,
        request: ConfigureStorageWatchRequest,
    ) -> Result<StorageWatchSnapshot, String> {
        if !request.enabled {
            self.stop();
            return Ok(StorageWatchSnapshot {
                enabled: false,
                directories: Vec::new(),
            });
        }

        let storage_root = normalize_directory(&request.storage_root);
        let directories = collect_watch_directories(request.directories);

        if directories.is_empty() {
            self.stop();
            return Ok(StorageWatchSnapshot {
                enabled: false,
                directories: Vec::new(),
            });
        }

        let mut watcher = create_watcher(app.clone(), storage_root)?;

        for directory in &directories {
            watcher
                .watch(directory, RecursiveMode::Recursive)
                .map_err(|error| format!("Failed to watch '{}': {error}", directory.display()))?;
        }

        self.watcher = Some(watcher);
        self.directories = directories
            .iter()
            .map(|directory| directory.to_string_lossy().to_string())
            .collect();

        Ok(StorageWatchSnapshot {
            enabled: true,
            directories: self.directories.clone(),
        })
    }

    pub fn stop(&mut self) {
        self.watcher = None;
        self.directories.clear();
    }
}

fn create_watcher(
    app: AppHandle,
    storage_root: Option<PathBuf>,
) -> Result<RecommendedWatcher, String> {
    recommended_watcher(move |result: notify::Result<Event>| match result {
        Ok(event) => emit_watch_event(&app, storage_root.as_deref(), event),
        Err(error) => {
            let _ = app.emit(
                STORAGE_WATCH_EVENT,
                StorageWatchEventPayload {
                    kind: String::from("error"),
                    paths: vec![error.to_string()],
                },
            );
        }
    })
    .map_err(|error| format!("Failed to initialize the storage watcher: {error}"))
}

fn emit_watch_event(app: &AppHandle, storage_root: Option<&Path>, event: Event) {
    if !should_forward_event(&event) {
        return;
    }

    let invalidates_index = event_invalidates_index(&event);
    let paths = event
        .paths
        .into_iter()
        .filter(|path| {
            let is_managed = storage_root
                .map(|root| is_path_inside_managed_storage(path, root))
                .unwrap_or(false);

            !is_managed && (invalidates_index || is_supported_audio_file(path))
        })
        .map(|path| path.to_string_lossy().to_string())
        .collect::<Vec<_>>();

    if paths.is_empty() {
        return;
    }

    let _ = app.emit(
        STORAGE_WATCH_EVENT,
        StorageWatchEventPayload {
            kind: format!("{:?}", event.kind),
            paths,
        },
    );
}

fn should_forward_event(event: &Event) -> bool {
    matches!(
        event.kind,
        EventKind::Create(_) | EventKind::Modify(_) | EventKind::Remove(_)
    )
}

fn event_invalidates_index(event: &Event) -> bool {
    matches!(event.kind, EventKind::Remove(_))
}

fn collect_watch_directories(directories: Vec<String>) -> Vec<PathBuf> {
    let mut next_directories = Vec::new();

    for directory in directories {
        let Some(path) = normalize_directory(&directory) else {
            continue;
        };

        if next_directories.iter().any(|current| current == &path) {
            continue;
        }

        next_directories.push(path);
    }

    next_directories
}

fn normalize_directory(value: &str) -> Option<PathBuf> {
    let trimmed = value.trim();

    if trimmed.is_empty() {
        return None;
    }

    let path = PathBuf::from(trimmed);

    if path.is_dir() {
        Some(path)
    } else {
        None
    }
}
