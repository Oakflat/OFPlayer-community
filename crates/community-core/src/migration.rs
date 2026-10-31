//! One-way, source-preserving migration of the legacy local catalog.
use rusqlite::{backup::Backup, Connection, OpenFlags, OptionalExtension};
use serde_json::{json, Value};
use std::{
    fs,
    path::{Path, PathBuf},
    time::Duration,
};

const DATABASE: &str = "ofplayer-desktop.sqlite3";
const TABLES: &[&str] = &[
    "app_state",
    "libraries",
    "playlists",
    "playlist_track_relations",
    "tracks",
    "track_artwork",
    "playback_history",
    "playback_history_latest",
    "track_query_projection",
];
fn reject_link(path: &Path) -> Result<(), String> {
    let metadata = fs::symlink_metadata(path).map_err(|e| e.to_string())?;
    if metadata.file_type().is_symlink() {
        return Err("Migration refuses linked source files".into());
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if metadata.file_attributes() & 0x400 != 0 {
            return Err("Migration refuses reparse points".into());
        }
    }
    Ok(())
}
fn copy_artwork(source: &Path, target: &Path, count: &mut usize) -> Result<(), String> {
    reject_link(source)?;
    fs::create_dir(target).map_err(|e| e.to_string())?;
    for item in fs::read_dir(source).map_err(|e| e.to_string())? {
        let item = item.map_err(|e| e.to_string())?;
        let path = item.path();
        reject_link(&path)?;
        if item.file_type().map_err(|e| e.to_string())?.is_dir() {
            copy_artwork(&path, &target.join(item.file_name()), count)?;
        } else {
            *count += 1;
            if *count > 200000 {
                return Err("Artwork migration exceeds file limit".into());
            }
            fs::copy(&path, target.join(item.file_name())).map_err(|e| e.to_string())?;
        }
    }
    Ok(())
}
fn column_exists(db: &Connection, table: &str, column: &str) -> Result<bool, String> {
    let mut stmt = db
        .prepare(&format!("PRAGMA table_info(\"{table}\")"))
        .map_err(|e| e.to_string())?;
    let names = stmt
        .query_map([], |r| r.get::<_, String>(1))
        .map_err(|e| e.to_string())?
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| e.to_string())?;
    Ok(names.iter().any(|name| name == column))
}
fn local_source(value: &Value) -> bool {
    let source = &value["source"];
    if source
        .get("connectionId")
        .and_then(Value::as_str)
        .is_some_and(|s| !s.is_empty())
    {
        return false;
    }
    if matches!(
        source["kind"].as_str(),
        Some(
            "webdav"
                | "navidrome"
                | "subsonic"
                | "external-url"
                | "external-index"
                | "cloud"
                | "hub"
        )
    ) {
        return false;
    }
    let path = source["path"].as_str().unwrap_or("");
    if path.is_empty() {
        return true;
    }
    let path = path.strip_prefix("\\\\?\\").unwrap_or(path);
    let b = path.as_bytes();
    b.len() > 2 && b[0].is_ascii_alphabetic() && b[1] == b':' && matches!(b[2], b'/' | b'\\')
}
fn relocate_artwork(value: &mut Value, source: &Path, target: &Path) {
    match value {
        Value::String(text) => {
            let file_url = text.starts_with("file:");
            let path = if file_url {
                url::Url::parse(text)
                    .ok()
                    .and_then(|u| u.to_file_path().ok())
            } else {
                Some(PathBuf::from(&*text))
            };
            if let Some(path) = path {
                let path = path.canonicalize().unwrap_or(path);
                if let Ok(relative) = path.strip_prefix(source) {
                    let new = target.join(relative);
                    *text = if file_url {
                        url::Url::from_file_path(&new)
                            .map(|u| u.to_string())
                            .unwrap_or_else(|_| new.to_string_lossy().into())
                    } else {
                        new.to_string_lossy().into()
                    };
                }
            }
        }
        Value::Array(items) => {
            for item in items {
                relocate_artwork(item, source, target);
            }
        }
        Value::Object(items) => {
            for item in items.values_mut() {
                relocate_artwork(item, source, target);
            }
        }
        _ => (),
    }
}
pub fn migrate(source: &Path, destination: &Path) -> Result<Value, String> {
    if !source.is_absolute() || !destination.is_absolute() {
        return Err("Absolute migration paths are required".into());
    }
    if destination.exists() {
        return Ok(json!({"migrated":false,"reason":"existing-target-preserved"}));
    }
    if !source.join(DATABASE).is_file() {
        return Ok(json!({"migrated":false,"reason":"source-database-missing"}));
    }
    reject_link(source)?;
    reject_link(&source.join(DATABASE))?;
    let source = source.canonicalize().map_err(|e| e.to_string())?;
    let parent = destination
        .parent()
        .ok_or("Destination parent missing")?
        .canonicalize()
        .map_err(|e| e.to_string())?;
    if parent.starts_with(&source) {
        return Err("Destination must be outside the legacy data root".into());
    }
    let stage = parent.join(format!(".community-import-{}", uuid::Uuid::new_v4()));
    fs::create_dir(&stage).map_err(|e| e.to_string())?;
    let source_db =
        Connection::open_with_flags(source.join(DATABASE), OpenFlags::SQLITE_OPEN_READ_ONLY)
            .map_err(|e| e.to_string())?;
    let mut target = Connection::open(stage.join(DATABASE)).map_err(|e| e.to_string())?;
    Backup::new(&source_db, &mut target)
        .map_err(|e| e.to_string())?
        .run_to_completion(64, Duration::from_millis(10), None)
        .map_err(|e| e.to_string())?;
    drop(source_db);
    target
        .execute_batch(
            "PRAGMA foreign_keys=OFF; PRAGMA journal_mode=DELETE; PRAGMA secure_delete=ON;",
        )
        .map_err(|e| e.to_string())?;
    let transaction = target.transaction().map_err(|e| e.to_string())?;
    // The snapshot may contain triggers for tables that Community intentionally
    // drops. Remove snapshot triggers before cleaning rows; only this checkout's
    // schema installs triggers in the resulting catalog. The source is read-only.
    let triggers = {
        let mut statement = transaction
            .prepare("SELECT name FROM sqlite_master WHERE type='trigger'")
            .map_err(|e| e.to_string())?;
        let rows = statement
            .query_map([], |row| row.get::<_, String>(0))
            .map_err(|e| e.to_string())?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|e| e.to_string())?;
        rows
    };
    for name in triggers {
        transaction
            .execute_batch(&format!("DROP TRIGGER \"{}\"", name.replace('"', "\"\"")))
            .map_err(|e| e.to_string())?;
    }
    let names = {
        let mut stmt = transaction
            .prepare(
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'",
            )
            .map_err(|e| e.to_string())?;
        let rows = stmt
            .query_map([], |r| r.get::<_, String>(0))
            .map_err(|e| e.to_string())?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|e| e.to_string())?;
        rows
    };
    for name in names {
        if !TABLES.contains(&name.as_str()) {
            transaction
                .execute_batch(&format!("DROP TABLE \"{}\"", name.replace('"', "\"\"")))
                .map_err(|e| e.to_string())?;
        }
    }
    if !column_exists(&transaction, "tracks", "payload_json")?
        || !column_exists(&transaction, "app_state", "key")?
    {
        return Err("Legacy catalog schema is not recognized; source retained".into());
    }
    let tracks = {
        let mut stmt = transaction
            .prepare("SELECT id,payload_json FROM tracks")
            .map_err(|e| e.to_string())?;
        let rows = stmt
            .query_map([], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?)))
            .map_err(|e| e.to_string())?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|e| e.to_string())?;
        rows
    };
    let mut skipped = 0;
    for (id, payload) in tracks {
        let mut value: Value = serde_json::from_str(&payload).map_err(|e| e.to_string())?;
        if !local_source(&value) {
            transaction
                .execute("DELETE FROM tracks WHERE id=?1", [&id])
                .map_err(|e| e.to_string())?;
            skipped += 1;
        } else {
            relocate_artwork(
                &mut value,
                &source.join("track-artwork"),
                &destination.join("track-artwork"),
            );
            transaction
                .execute(
                    "UPDATE tracks SET payload_json=?2 WHERE id=?1",
                    [id, value.to_string()],
                )
                .map_err(|e| e.to_string())?;
        }
    }
    for table in [
        "playlist_track_relations",
        "track_artwork",
        "playback_history",
        "playback_history_latest",
        "track_query_projection",
    ] {
        if column_exists(&transaction, table, "track_id")? {
            transaction
                .execute(
                    &format!(
                        "DELETE FROM \"{table}\" WHERE track_id NOT IN (SELECT id FROM tracks)"
                    ),
                    [],
                )
                .map_err(|e| e.to_string())?;
        }
    }
    let preferences: Option<String> = transaction
        .query_row(
            "SELECT value_json FROM app_state WHERE key='preferences'",
            [],
            |r| r.get(0),
        )
        .optional()
        .map_err(|e| e.to_string())?;
    transaction
        .execute(
            "DELETE FROM app_state WHERE key NOT IN ('preferences','session')",
            [],
        )
        .map_err(|e| e.to_string())?;
    if let Some(preferences) = preferences {
        let value: Value = serde_json::from_str(&preferences).map_err(|e| e.to_string())?;
        let mut clean = serde_json::Map::new();
        for key in [
            "volume",
            "rememberVolume",
            "theme",
            "language",
            "activeLibrary",
            "activeCollectionRef",
            "sortOption",
            "playbackOutputDeviceId",
            "lyricsFontSize",
            "repeatMode",
            "shuffleEnabled",
            "lyricCapsule",
            "lyricsScanDirectories",
            "scanDirectories",
            "autoScanOnLaunch",
            "newTracksFirst",
            "artistSortOption",
            "collectionSortOptions",
            "closeBehavior",
            "immersiveTaskbarMode",
        ] {
            if let Some(v) = value.get(key) {
                clean.insert(key.into(), v.clone());
            }
        }
        transaction
            .execute(
                "UPDATE app_state SET value_json=?1 WHERE key='preferences'",
                [Value::Object(clean).to_string()],
            )
            .map_err(|e| e.to_string())?;
    }
    let session: Option<String> = transaction
        .query_row(
            "SELECT value_json FROM app_state WHERE key='session'",
            [],
            |r| r.get(0),
        )
        .optional()
        .map_err(|e| e.to_string())?;
    if let Some(session) = session {
        let mut session: crate::desktop_types::SessionStateSnapshot =
            serde_json::from_str(&session).map_err(|e| e.to_string())?;
        session.playback_status = "paused".into();
        transaction
            .execute(
                "UPDATE app_state SET value_json=?1 WHERE key='session'",
                [serde_json::to_string(&session).map_err(|e| e.to_string())?],
            )
            .map_err(|e| e.to_string())?;
    }
    transaction.commit().map_err(|e| e.to_string())?;
    crate::schema::initialize_schema(&target)?;
    crate::catalog_browse::initialize(&target)?;
    // Artwork is also stored outside track JSON. Rewrite these references so the
    // new catalog does not depend on keeping the old profile at the same path.
    let artwork = {
        let mut statement = target
            .prepare("SELECT track_id,artwork_path,artwork_text FROM track_artwork")
            .map_err(|e| e.to_string())?;
        let rows = statement
            .query_map([], |r| {
                Ok((
                    r.get::<_, String>(0)?,
                    r.get::<_, String>(1)?,
                    r.get::<_, String>(2)?,
                ))
            })
            .map_err(|e| e.to_string())?
            .collect::<Result<Vec<_>, _>>()
            .map_err(|e| e.to_string())?;
        rows
    };
    for (id, path, text) in artwork {
        let mut path = Value::String(path);
        let mut text = Value::String(text);
        relocate_artwork(
            &mut path,
            &source.join("track-artwork"),
            &destination.join("track-artwork"),
        );
        relocate_artwork(
            &mut text,
            &source.join("track-artwork"),
            &destination.join("track-artwork"),
        );
        target
            .execute(
                "UPDATE track_artwork SET artwork_path=?2,artwork_text=?3 WHERE track_id=?1",
                [
                    id,
                    path.as_str().unwrap_or_default().into(),
                    text.as_str().unwrap_or_default().into(),
                ],
            )
            .map_err(|e| e.to_string())?;
    }
    // A backup contains the source's freelist too. Rebuild the target so deleted
    // account/connection records cannot survive as bytes in unused database pages.
    target.execute_batch("VACUUM;").map_err(|e| e.to_string())?;
    let integrity: String = target
        .query_row("PRAGMA integrity_check", [], |r| r.get(0))
        .map_err(|e| e.to_string())?;
    if integrity != "ok" {
        return Err("Migrated snapshot integrity check failed; source retained".into());
    }
    let track_count: i64 = target
        .query_row("SELECT COUNT(*) FROM tracks", [], |r| r.get(0))
        .map_err(|e| e.to_string())?;
    drop(target);
    let mut artwork_files = 0;
    if source.join("track-artwork").exists() {
        copy_artwork(
            &source.join("track-artwork"),
            &stage.join("track-artwork"),
            &mut artwork_files,
        )?;
    }
    let receipt = json!({"migrated":true,"sourcePreserved":true,"source":source,"localTracks":track_count,"excludedRemoteTracks":skipped,"artworkFiles":artwork_files,"credentialsMigrated":false,"at":chrono::Utc::now().to_rfc3339()});
    fs::write(
        stage.join("community-migration.json"),
        serde_json::to_vec_pretty(&receipt).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())?;
    fs::rename(&stage, destination).map_err(|e| {
        format!(
            "Cannot publish migrated snapshot; staged copy retained at {}: {e}",
            stage.display()
        )
    })?;
    Ok(receipt)
}
