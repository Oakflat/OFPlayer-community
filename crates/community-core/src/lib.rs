//! Standalone Community local playback and catalog. No Tauri, Hub, account or network service.
pub mod app_paths;
pub mod artwork_store;
pub mod audio_formats;
pub mod catalog_db;
pub mod db_helpers;
pub mod desktop_state;
pub mod desktop_types;
pub mod diagnostics;
pub mod dsd_playback;
pub mod lyrics;
pub mod metadata;
pub mod migration;
pub mod navigation;
pub mod playback;
pub mod schema;
pub mod session_ops;
pub mod sorting;
pub mod storage;
pub mod storage_maintenance;
pub mod system_media;
pub mod watcher;

pub mod catalog_browse;
pub mod external_sources;
