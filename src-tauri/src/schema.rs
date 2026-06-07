use crate::{
    artwork_store::store_track_artwork,
    db_helpers::{
        current_unix_timestamp, deserialize_value, load_json_from_connection,
        optional_number_as_f64, optional_number_as_u64, optional_text_field, saturating_u64_to_i64,
        save_json_to_connection, serialize_value,
    },
};
use rusqlite::{params, Connection, OptionalExtension};
use serde_json::{Map, Value};
use std::{collections::HashSet, path::PathBuf};

const TRACK_PROJECTION_SCHEMA_STATE_KEY: &str = "schema.trackProjection";
const TRACK_PROJECTION_SCHEMA_VERSION: &str = "track-projection-v1";
const TRACK_QUERY_PROJECTION_SCHEMA_STATE_KEY: &str = "schema.trackQueryProjection";
const TRACK_QUERY_PROJECTION_SCHEMA_VERSION: &str = "track-query-projection-v3";
const TRACK_ARTWORK_SCHEMA_STATE_KEY: &str = "schema.trackArtwork";
const TRACK_ARTWORK_SCHEMA_VERSION: &str = "track-artwork-v2";
const PLAYBACK_HISTORY_LATEST_SCHEMA_STATE_KEY: &str = "schema.playbackHistoryLatest";
const PLAYBACK_HISTORY_LATEST_SCHEMA_VERSION: &str = "playback-history-latest-v1";
const EXTERNAL_SOURCE_INDEX_SCHEMA_STATE_KEY: &str = "schema.externalSourceIndex";
const EXTERNAL_SOURCE_INDEX_SCHEMA_VERSION: &str = "external-source-index-v1";
const LOCAL_SOURCE_INDEX_SCHEMA_STATE_KEY: &str = "schema.localSourceIndex";
const LOCAL_SOURCE_INDEX_SCHEMA_VERSION: &str = "local-source-index-v1";

#[derive(Debug, Clone, Default)]
pub(crate) struct TrackProjection {
    pub(crate) artist: String,
    pub(crate) album_artist: String,
    pub(crate) album: String,
    pub(crate) genre: String,
    pub(crate) composer: String,
    pub(crate) lyricist: String,
    pub(crate) comment: String,
    pub(crate) duration: f64,
    pub(crate) file_size: i64,
    pub(crate) bitrate: i64,
    pub(crate) sample_rate: i64,
    pub(crate) bit_depth: i64,
}

pub(crate) fn initialize_schema(connection: &Connection) -> Result<(), String> {
    connection
        .execute_batch(
            "
            CREATE TABLE IF NOT EXISTS app_state (
              key TEXT PRIMARY KEY NOT NULL,
              value_json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS libraries (
              id TEXT PRIMARY KEY NOT NULL,
              order_index INTEGER NOT NULL,
              created_at_text TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_libraries_order
              ON libraries(order_index, created_at_text, id);

            CREATE TABLE IF NOT EXISTS playlists (
              id TEXT PRIMARY KEY NOT NULL,
              library_id TEXT NOT NULL,
              order_index INTEGER NOT NULL,
              kind TEXT NOT NULL,
              system_key TEXT,
              created_at_text TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_playlists_library_order
              ON playlists(library_id, order_index, created_at_text, id);
            CREATE INDEX IF NOT EXISTS idx_playlists_library_kind
              ON playlists(library_id, kind);
            CREATE INDEX IF NOT EXISTS idx_playlists_system_key
              ON playlists(system_key);

            CREATE TABLE IF NOT EXISTS playlist_track_relations (
              id TEXT PRIMARY KEY NOT NULL,
              playlist_id TEXT NOT NULL,
              track_id TEXT NOT NULL,
              order_index INTEGER NOT NULL,
              added_at_text TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_playlist_track_relations_playlist_order
              ON playlist_track_relations(playlist_id, order_index, added_at_text, id);
            CREATE INDEX IF NOT EXISTS idx_playlist_track_relations_track
              ON playlist_track_relations(track_id);

            CREATE TABLE IF NOT EXISTS tracks (
              id TEXT PRIMARY KEY NOT NULL,
              library_id TEXT NOT NULL,
              library_order INTEGER NOT NULL,
              imported_at_text TEXT NOT NULL,
              is_favorite INTEGER NOT NULL,
              title TEXT NOT NULL,
              display_title TEXT NOT NULL,
              artist TEXT NOT NULL DEFAULT '',
              album_artist TEXT NOT NULL DEFAULT '',
              album TEXT NOT NULL DEFAULT '',
              genre TEXT NOT NULL DEFAULT '',
              composer TEXT NOT NULL DEFAULT '',
              lyricist TEXT NOT NULL DEFAULT '',
              comment TEXT NOT NULL DEFAULT '',
              file_name TEXT NOT NULL,
              format TEXT NOT NULL,
              duration REAL NOT NULL DEFAULT 0,
              file_size INTEGER NOT NULL DEFAULT 0,
              bitrate INTEGER NOT NULL DEFAULT 0,
              sample_rate INTEGER NOT NULL DEFAULT 0,
              bit_depth INTEGER NOT NULL DEFAULT 0,
              payload_json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_tracks_library_order
              ON tracks(library_id, library_order, imported_at_text, id);
            CREATE INDEX IF NOT EXISTS idx_tracks_imported_at
              ON tracks(imported_at_text, id);
            CREATE INDEX IF NOT EXISTS idx_tracks_library_imported_at
              ON tracks(library_id, imported_at_text DESC, id DESC);
            CREATE INDEX IF NOT EXISTS idx_tracks_favorite
              ON tracks(is_favorite, imported_at_text, id);
            CREATE INDEX IF NOT EXISTS idx_tracks_library_favorite
              ON tracks(library_id, is_favorite, library_order, imported_at_text, id);
            CREATE INDEX IF NOT EXISTS idx_tracks_library_album_group
              ON tracks(library_id, album, album_artist, artist);
            CREATE INDEX IF NOT EXISTS idx_tracks_library_artist_group
              ON tracks(library_id, artist, album_artist);

            CREATE TABLE IF NOT EXISTS track_artwork (
              track_id TEXT PRIMARY KEY NOT NULL,
              artwork_text TEXT NOT NULL,
              artwork_path TEXT NOT NULL DEFAULT '',
              mime_type TEXT NOT NULL DEFAULT '',
              content_hash TEXT NOT NULL DEFAULT '',
              byte_length INTEGER NOT NULL DEFAULT 0,
              updated_at INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS playback_history (
              id TEXT PRIMARY KEY NOT NULL,
              track_id TEXT,
              recorded_at_text TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_playback_history_recorded_at
              ON playback_history(recorded_at_text DESC, id DESC);
            CREATE INDEX IF NOT EXISTS idx_playback_history_track_recorded_at
              ON playback_history(track_id, recorded_at_text DESC, id DESC);

            CREATE TABLE IF NOT EXISTS playback_history_latest (
              track_id TEXT PRIMARY KEY NOT NULL,
              latest_history_id TEXT NOT NULL,
              recorded_at_text TEXT NOT NULL,
              updated_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_playback_history_latest_recorded_at
              ON playback_history_latest(recorded_at_text DESC, latest_history_id DESC, track_id);
            ",
        )
        .map_err(|error| format!("Failed to initialize desktop state schema: {error}"))?;

    ensure_playback_history_latest_schema(connection)?;
    ensure_track_artwork_schema(connection)?;
    ensure_track_projection_schema(connection)?;
    ensure_track_query_projection_schema(connection)?;
    ensure_external_source_index_schema(connection)?;
    ensure_local_source_index_schema(connection)
}

fn ensure_external_source_index_schema(connection: &Connection) -> Result<(), String> {
    let current_version =
        load_json_from_connection(connection, EXTERNAL_SOURCE_INDEX_SCHEMA_STATE_KEY)?
            .and_then(|value| value.as_str().map(String::from));

    if current_version.as_deref() == Some(EXTERNAL_SOURCE_INDEX_SCHEMA_VERSION) {
        return Ok(());
    }

    let payload_rows = {
        let mut statement = connection
            .prepare("SELECT id, payload_json FROM tracks")
            .map_err(|error| {
                format!("Failed to prepare external source index migration: {error}")
            })?;
        let rows = statement
            .query_map([], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
            })
            .map_err(|error| format!("Failed to query external source index migration: {error}"))?;
        let mut payload_rows = Vec::new();

        for row in rows {
            payload_rows.push(row.map_err(|error| {
                format!("Failed to read external source index migration row: {error}")
            })?);
        }

        payload_rows
    };

    connection
        .execute_batch("BEGIN IMMEDIATE TRANSACTION")
        .map_err(|error| format!("Failed to begin external source index migration: {error}"))?;

    let migration_result = (|| {
        let mut statement = connection
            .prepare(
                "UPDATE tracks
                 SET payload_json = ?2,
                     updated_at = ?3
                 WHERE id = ?1",
            )
            .map_err(|error| format!("Failed to prepare external source index update: {error}"))?;
        let updated_at = current_unix_timestamp();

        for (track_id, payload_json) in payload_rows {
            let mut record = deserialize_value(&payload_json, "external source index migration")?;

            if !migrate_external_cache_source_to_index(&mut record) {
                continue;
            }

            let migrated_payload = serialize_value(&record, "external source index migration")?;
            statement
                .execute(params![track_id.as_str(), migrated_payload, updated_at])
                .map_err(|error| {
                    format!("Failed to migrate external source index '{track_id}': {error}")
                })?;
        }

        save_json_to_connection(
            connection,
            EXTERNAL_SOURCE_INDEX_SCHEMA_STATE_KEY,
            &Value::String(String::from(EXTERNAL_SOURCE_INDEX_SCHEMA_VERSION)),
        )
    })();

    if let Err(error) = migration_result {
        let _ = connection.execute_batch("ROLLBACK");
        return Err(error);
    }

    connection
        .execute_batch("COMMIT")
        .map_err(|error| format!("Failed to commit external source index migration: {error}"))?;

    Ok(())
}

fn migrate_external_cache_source_to_index(record: &mut Value) -> bool {
    let Some(source) = record.get_mut("source").and_then(Value::as_object_mut) else {
        return false;
    };

    if source_text(source, "kind") != "external-cache" {
        return false;
    }

    let provider = source_text(source, "provider");
    let remote_id = source_text(source, "remoteId");
    let origin_path = source_text(source, "originPath");
    let url = source_text(source, "url");
    let path = source_text(source, "path");
    let indexed_path = [
        origin_path.as_str(),
        url.as_str(),
        remote_id.as_str(),
        path.as_str(),
    ]
    .into_iter()
    .map(str::trim)
    .find(|value| !value.is_empty() && !looks_like_external_cache_path(value))
    .unwrap_or_default()
    .to_string();

    if indexed_path.is_empty() && remote_id.is_empty() {
        return false;
    }

    let indexed_kind = match provider.as_str() {
        "webdav" => "webdav",
        "subsonic" | "navidrome" => "subsonic",
        _ => "external-index",
    };

    source.insert(
        String::from("kind"),
        Value::String(String::from(indexed_kind)),
    );
    source.insert(String::from("path"), Value::String(indexed_path));
    source.insert(String::from("url"), Value::String(String::new()));
    source.insert(String::from("persistUrl"), Value::Bool(false));
    source.remove("transient");
    source.remove("deleteOnRelease");

    true
}

fn ensure_local_source_index_schema(connection: &Connection) -> Result<(), String> {
    let current_version =
        load_json_from_connection(connection, LOCAL_SOURCE_INDEX_SCHEMA_STATE_KEY)?
            .and_then(|value| value.as_str().map(String::from));

    if current_version.as_deref() == Some(LOCAL_SOURCE_INDEX_SCHEMA_VERSION) {
        return Ok(());
    }

    let payload_rows = {
        let mut statement = connection
            .prepare("SELECT id, payload_json FROM tracks")
            .map_err(|error| format!("Failed to prepare local source index migration: {error}"))?;
        let rows = statement
            .query_map([], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
            })
            .map_err(|error| format!("Failed to query local source index migration: {error}"))?;
        let mut payload_rows = Vec::new();

        for row in rows {
            payload_rows.push(row.map_err(|error| {
                format!("Failed to read local source index migration row: {error}")
            })?);
        }

        payload_rows
    };

    connection
        .execute_batch("BEGIN IMMEDIATE TRANSACTION")
        .map_err(|error| format!("Failed to begin local source index migration: {error}"))?;

    let migration_result = (|| {
        let mut statement = connection
            .prepare(
                "UPDATE tracks
                 SET payload_json = ?2,
                     updated_at = ?3
                 WHERE id = ?1",
            )
            .map_err(|error| format!("Failed to prepare local source index update: {error}"))?;
        let updated_at = current_unix_timestamp();

        for (track_id, payload_json) in payload_rows {
            let mut record = deserialize_value(&payload_json, "local source index migration")?;

            if !migrate_local_native_source_to_index(&mut record) {
                continue;
            }

            let migrated_payload = serialize_value(&record, "local source index migration")?;
            statement
                .execute(params![track_id.as_str(), migrated_payload, updated_at])
                .map_err(|error| {
                    format!("Failed to migrate local source index '{track_id}': {error}")
                })?;
        }

        save_json_to_connection(
            connection,
            LOCAL_SOURCE_INDEX_SCHEMA_STATE_KEY,
            &Value::String(String::from(LOCAL_SOURCE_INDEX_SCHEMA_VERSION)),
        )
    })();

    if let Err(error) = migration_result {
        let _ = connection.execute_batch("ROLLBACK");
        return Err(error);
    }

    connection
        .execute_batch("COMMIT")
        .map_err(|error| format!("Failed to commit local source index migration: {error}"))?;

    Ok(())
}

fn migrate_local_native_source_to_index(record: &mut Value) -> bool {
    let Some(source) = record.get_mut("source").and_then(Value::as_object_mut) else {
        return false;
    };

    if source_text(source, "kind") != "native-file" {
        return false;
    }

    let path = source_text(source, "path");
    let origin_path = source_text(source, "originPath");

    if origin_path.is_empty()
        || normalize_path_text(&origin_path) == normalize_path_text(&path)
        || !PathBuf::from(&origin_path).is_file()
    {
        if !path.is_empty() && PathBuf::from(&path).is_file() {
            source.insert(String::from("indexed"), Value::Bool(true));
            return true;
        }

        return false;
    }

    source.insert(String::from("path"), Value::String(origin_path.clone()));
    source.insert(String::from("originPath"), Value::String(origin_path));
    source.insert(String::from("indexed"), Value::Bool(true));

    true
}

fn source_text(source: &Map<String, Value>, field: &str) -> String {
    source
        .get(field)
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .unwrap_or_default()
        .to_string()
}

fn normalize_path_text(value: &str) -> String {
    value.trim().replace('/', "\\").to_ascii_lowercase()
}

fn looks_like_external_cache_path(value: &str) -> bool {
    let normalized = value.replace('\\', "/").to_ascii_lowercase();

    normalized.contains("/cache/external-sources/")
        || normalized.contains("/ofplayer/cache/external-sources/")
        || normalized.contains("/ofplayer/cache/external-transient/")
}

fn ensure_track_artwork_schema(connection: &Connection) -> Result<(), String> {
    connection
        .execute_batch(
            "
            CREATE TABLE IF NOT EXISTS track_artwork (
              track_id TEXT PRIMARY KEY NOT NULL,
              artwork_text TEXT NOT NULL,
              artwork_path TEXT NOT NULL DEFAULT '',
              mime_type TEXT NOT NULL DEFAULT '',
              content_hash TEXT NOT NULL DEFAULT '',
              byte_length INTEGER NOT NULL DEFAULT 0,
              updated_at INTEGER NOT NULL
            );

            DROP TRIGGER IF EXISTS trg_tracks_artwork_delete;
            CREATE TRIGGER trg_tracks_artwork_delete
            AFTER DELETE ON tracks
            BEGIN
              DELETE FROM track_artwork WHERE track_id = OLD.id;
            END;
            ",
        )
        .map_err(|error| format!("Failed to initialize desktop track artwork schema: {error}"))?;
    ensure_track_artwork_columns(connection)?;

    let current_version = load_json_from_connection(connection, TRACK_ARTWORK_SCHEMA_STATE_KEY)?
        .and_then(|value| value.as_str().map(String::from));

    if current_version.as_deref() == Some(TRACK_ARTWORK_SCHEMA_VERSION) {
        return Ok(());
    }

    split_track_artwork_from_payloads(connection)?;
    move_track_artwork_text_to_assets(connection)?;
    save_json_to_connection(
        connection,
        TRACK_ARTWORK_SCHEMA_STATE_KEY,
        &Value::String(String::from(TRACK_ARTWORK_SCHEMA_VERSION)),
    )
}

fn ensure_track_artwork_columns(connection: &Connection) -> Result<(), String> {
    let existing_columns = load_table_column_names(connection, "track_artwork")?;

    for (column_name, column_definition) in [
        ("artwork_path", "TEXT NOT NULL DEFAULT ''"),
        ("mime_type", "TEXT NOT NULL DEFAULT ''"),
        ("content_hash", "TEXT NOT NULL DEFAULT ''"),
    ] {
        if existing_columns.contains(column_name) {
            continue;
        }

        let statement =
            format!("ALTER TABLE track_artwork ADD COLUMN {column_name} {column_definition}");
        connection
            .execute(&statement, [])
            .map_err(|error| format!("Failed to add track_artwork.{column_name}: {error}"))?;
    }

    connection
        .execute(
            "CREATE INDEX IF NOT EXISTS idx_track_artwork_content_hash
              ON track_artwork(content_hash)",
            [],
        )
        .map_err(|error| {
            format!("Failed to create desktop track artwork content hash index: {error}")
        })?;

    Ok(())
}

fn split_track_artwork_from_payloads(connection: &Connection) -> Result<(), String> {
    let payload_rows = {
        let mut statement = connection
            .prepare("SELECT id, payload_json FROM tracks")
            .map_err(|error| {
                format!("Failed to prepare desktop track artwork migration: {error}")
            })?;
        let rows = statement
            .query_map([], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
            })
            .map_err(|error| format!("Failed to query desktop track artwork migration: {error}"))?;
        let mut payload_rows = Vec::new();

        for row in rows {
            payload_rows.push(row.map_err(|error| {
                format!("Failed to read desktop track artwork migration row: {error}")
            })?);
        }

        payload_rows
    };

    if payload_rows.is_empty() {
        return Ok(());
    }

    connection
        .execute_batch("BEGIN IMMEDIATE TRANSACTION")
        .map_err(|error| format!("Failed to begin desktop track artwork migration: {error}"))?;

    let migration_result = (|| {
        let mut update_track = connection
            .prepare(
                "UPDATE tracks
                 SET payload_json = ?2,
                     updated_at = ?3
                 WHERE id = ?1",
            )
            .map_err(|error| {
                format!("Failed to prepare desktop track artwork payload update: {error}")
            })?;
        let mut upsert_artwork = connection
            .prepare(
                "INSERT INTO track_artwork
                   (track_id, artwork_text, artwork_path, mime_type, content_hash, byte_length, updated_at)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
                 ON CONFLICT(track_id) DO UPDATE SET
                   artwork_text = excluded.artwork_text,
                   artwork_path = excluded.artwork_path,
                   mime_type = excluded.mime_type,
                   content_hash = excluded.content_hash,
                   byte_length = excluded.byte_length,
                   updated_at = excluded.updated_at",
            )
            .map_err(|error| format!("Failed to prepare desktop track artwork upsert: {error}"))?;

        for (track_id, payload_json) in payload_rows {
            let mut record = deserialize_value(&payload_json, "desktop track artwork migration")?;
            let artwork = optional_text_field(&record, "artwork")
                .map(|value| value.trim().to_string())
                .unwrap_or_default();

            if artwork.is_empty() {
                continue;
            }

            if let Some(record_object) = record.as_object_mut() {
                record_object.insert(String::from("artwork"), Value::String(String::new()));
            }

            let updated_at = current_unix_timestamp();
            let Some(stored_artwork) = store_track_artwork(&artwork)? else {
                continue;
            };
            let stripped_payload = serialize_value(&record, "desktop track artwork migration")?;

            upsert_artwork
                .execute(params![
                    track_id.as_str(),
                    stored_artwork.artwork_text,
                    stored_artwork.artwork_path,
                    stored_artwork.mime_type,
                    stored_artwork.content_hash,
                    stored_artwork.byte_length,
                    updated_at,
                ])
                .map_err(|error| {
                    format!("Failed to migrate desktop track artwork '{track_id}': {error}")
                })?;
            update_track
                .execute(params![track_id.as_str(), stripped_payload, updated_at])
                .map_err(|error| {
                    format!("Failed to strip desktop track payload artwork '{track_id}': {error}")
                })?;
        }

        Ok(())
    })();

    if let Err(error) = migration_result {
        let _ = connection.execute_batch("ROLLBACK");
        return Err(error);
    }

    connection
        .execute_batch("COMMIT")
        .map_err(|error| format!("Failed to commit desktop track artwork migration: {error}"))?;

    Ok(())
}

fn move_track_artwork_text_to_assets(connection: &Connection) -> Result<(), String> {
    let artwork_rows = {
        let mut statement = connection
            .prepare(
                "SELECT track_id, artwork_text
                 FROM track_artwork
                 WHERE trim(artwork_text) <> ''
                   AND (artwork_path IS NULL OR trim(artwork_path) = '')",
            )
            .map_err(|error| {
                format!("Failed to prepare desktop track artwork asset migration: {error}")
            })?;
        let rows = statement
            .query_map([], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
            })
            .map_err(|error| {
                format!("Failed to query desktop track artwork asset migration: {error}")
            })?;
        let mut artwork_rows = Vec::new();

        for row in rows {
            artwork_rows.push(row.map_err(|error| {
                format!("Failed to read desktop track artwork asset migration row: {error}")
            })?);
        }

        artwork_rows
    };

    if artwork_rows.is_empty() {
        return Ok(());
    }

    connection
        .execute_batch("BEGIN IMMEDIATE TRANSACTION")
        .map_err(|error| {
            format!("Failed to begin desktop track artwork asset migration: {error}")
        })?;

    let mut migrated_count = 0usize;
    let migration_result = (|| {
        let mut update_artwork = connection
            .prepare(
                "UPDATE track_artwork
                 SET artwork_text = ?2,
                     artwork_path = ?3,
                     mime_type = ?4,
                     content_hash = ?5,
                     byte_length = ?6,
                     updated_at = ?7
                 WHERE track_id = ?1",
            )
            .map_err(|error| {
                format!("Failed to prepare desktop track artwork asset update: {error}")
            })?;

        for (track_id, artwork) in artwork_rows {
            let Some(stored_artwork) = store_track_artwork(&artwork)? else {
                continue;
            };

            if stored_artwork.artwork_path.is_empty() {
                continue;
            }

            update_artwork
                .execute(params![
                    track_id.as_str(),
                    stored_artwork.artwork_text,
                    stored_artwork.artwork_path,
                    stored_artwork.mime_type,
                    stored_artwork.content_hash,
                    stored_artwork.byte_length,
                    current_unix_timestamp(),
                ])
                .map_err(|error| {
                    format!("Failed to migrate desktop track artwork asset '{track_id}': {error}")
                })?;
            migrated_count += 1;
        }

        Ok(())
    })();

    if let Err(error) = migration_result {
        let _ = connection.execute_batch("ROLLBACK");
        return Err(error);
    }

    connection.execute_batch("COMMIT").map_err(|error| {
        format!("Failed to commit desktop track artwork asset migration: {error}")
    })?;

    if migrated_count > 0 {
        compact_database_after_artwork_asset_migration(connection)?;
    }

    Ok(())
}

fn compact_database_after_artwork_asset_migration(connection: &Connection) -> Result<(), String> {
    connection
        .execute_batch("PRAGMA wal_checkpoint(TRUNCATE); VACUUM; PRAGMA wal_checkpoint(TRUNCATE);")
        .map_err(|error| {
            format!("Failed to compact desktop database after artwork asset migration: {error}")
        })
}

fn ensure_playback_history_latest_schema(connection: &Connection) -> Result<(), String> {
    let current_version =
        load_json_from_connection(connection, PLAYBACK_HISTORY_LATEST_SCHEMA_STATE_KEY)?
            .and_then(|value| value.as_str().map(String::from));

    if current_version.as_deref() == Some(PLAYBACK_HISTORY_LATEST_SCHEMA_VERSION) {
        return Ok(());
    }

    connection
        .execute("DELETE FROM playback_history_latest", [])
        .map_err(|error| {
            format!("Failed to clear desktop playback history latest projection: {error}")
        })?;
    connection
        .execute(
            "INSERT OR REPLACE INTO playback_history_latest
               (track_id, latest_history_id, recorded_at_text, updated_at)
             SELECT history.track_id,
                    history.id,
                    history.recorded_at_text,
                    history.updated_at
             FROM playback_history history
             INNER JOIN (
               SELECT track_id,
                      MAX(recorded_at_text || char(31) || id) AS latest_key
               FROM playback_history
               WHERE track_id IS NOT NULL
                 AND trim(track_id) <> ''
                 AND COALESCE(json_extract(payload_json, '$.type'), 'played') = 'played'
               GROUP BY track_id
             ) latest
               ON latest.track_id = history.track_id
              AND latest.latest_key = history.recorded_at_text || char(31) || history.id",
            [],
        )
        .map_err(|error| {
            format!("Failed to rebuild desktop playback history latest projection: {error}")
        })?;

    save_json_to_connection(
        connection,
        PLAYBACK_HISTORY_LATEST_SCHEMA_STATE_KEY,
        &Value::from(PLAYBACK_HISTORY_LATEST_SCHEMA_VERSION),
    )
}

pub(crate) fn build_track_projection(record: &Value) -> TrackProjection {
    TrackProjection {
        artist: optional_text_field(record, "artist").unwrap_or_default(),
        album_artist: optional_text_field(record, "albumArtist").unwrap_or_default(),
        album: optional_text_field(record, "album").unwrap_or_default(),
        genre: optional_text_field(record, "genre").unwrap_or_default(),
        composer: optional_text_field(record, "composer").unwrap_or_default(),
        lyricist: optional_text_field(record, "lyricist").unwrap_or_default(),
        comment: optional_text_field(record, "comment").unwrap_or_default(),
        duration: optional_number_as_f64(record, "duration").unwrap_or_default(),
        file_size: optional_number_as_u64(record, "fileSize")
            .or_else(|| optional_number_as_u64(record, "size"))
            .map(saturating_u64_to_i64)
            .unwrap_or_default(),
        bitrate: optional_number_as_u64(record, "bitrate")
            .map(saturating_u64_to_i64)
            .unwrap_or_default(),
        sample_rate: optional_number_as_u64(record, "sampleRate")
            .map(saturating_u64_to_i64)
            .unwrap_or_default(),
        bit_depth: optional_number_as_u64(record, "bitDepth")
            .map(saturating_u64_to_i64)
            .unwrap_or_default(),
    }
}

fn ensure_track_projection_schema(connection: &Connection) -> Result<(), String> {
    let existing_columns = load_table_column_names(connection, "tracks")?;
    let mut added_projection_column = false;

    for (column_name, column_definition) in [
        ("artist", "TEXT NOT NULL DEFAULT ''"),
        ("album_artist", "TEXT NOT NULL DEFAULT ''"),
        ("album", "TEXT NOT NULL DEFAULT ''"),
        ("genre", "TEXT NOT NULL DEFAULT ''"),
        ("composer", "TEXT NOT NULL DEFAULT ''"),
        ("lyricist", "TEXT NOT NULL DEFAULT ''"),
        ("comment", "TEXT NOT NULL DEFAULT ''"),
        ("duration", "REAL NOT NULL DEFAULT 0"),
        ("file_size", "INTEGER NOT NULL DEFAULT 0"),
        ("bitrate", "INTEGER NOT NULL DEFAULT 0"),
        ("sample_rate", "INTEGER NOT NULL DEFAULT 0"),
        ("bit_depth", "INTEGER NOT NULL DEFAULT 0"),
    ] {
        if existing_columns.contains(column_name) {
            continue;
        }

        let statement = format!("ALTER TABLE tracks ADD COLUMN {column_name} {column_definition}");
        connection
            .execute(&statement, [])
            .map_err(|error| format!("Failed to add tracks.{column_name}: {error}"))?;
        added_projection_column = true;
    }

    let projection_schema_version =
        load_json_from_connection(connection, TRACK_PROJECTION_SCHEMA_STATE_KEY)?
            .and_then(|value| value.as_str().map(String::from));

    if added_projection_column
        || projection_schema_version.as_deref() != Some(TRACK_PROJECTION_SCHEMA_VERSION)
    {
        backfill_track_projection_columns(connection)?;
        save_json_to_connection(
            connection,
            TRACK_PROJECTION_SCHEMA_STATE_KEY,
            &Value::String(String::from(TRACK_PROJECTION_SCHEMA_VERSION)),
        )?;
    }

    Ok(())
}

fn ensure_track_query_projection_schema(connection: &Connection) -> Result<(), String> {
    connection
        .execute_batch(
            "
            CREATE TABLE IF NOT EXISTS track_query_projection (
              id TEXT PRIMARY KEY NOT NULL,
              library_id TEXT NOT NULL,
              display_title TEXT NOT NULL,
              title TEXT NOT NULL,
              artist TEXT NOT NULL,
              album_artist TEXT NOT NULL,
              album TEXT NOT NULL,
              genre TEXT NOT NULL,
              composer TEXT NOT NULL,
              lyricist TEXT NOT NULL,
              comment TEXT NOT NULL,
              file_name TEXT NOT NULL,
              format TEXT NOT NULL,
              duration REAL NOT NULL,
              file_size INTEGER NOT NULL,
              bitrate INTEGER NOT NULL,
              sample_rate INTEGER NOT NULL,
              bit_depth INTEGER NOT NULL,
              is_favorite INTEGER NOT NULL
            ) WITHOUT ROWID;
            ",
        )
        .map_err(|error| format!("Failed to initialize desktop track query projection: {error}"))?;
    ensure_track_query_projection_triggers(connection)?;

    let projection_schema_version =
        load_json_from_connection(connection, TRACK_QUERY_PROJECTION_SCHEMA_STATE_KEY)?
            .and_then(|value| value.as_str().map(String::from));
    let track_count = count_rows(connection, "tracks")?;
    let projection_count = count_rows(connection, "track_query_projection")?;

    if projection_schema_version.as_deref() != Some(TRACK_QUERY_PROJECTION_SCHEMA_VERSION)
        || projection_count != track_count
    {
        rebuild_track_query_projection(connection)?;
        save_json_to_connection(
            connection,
            TRACK_QUERY_PROJECTION_SCHEMA_STATE_KEY,
            &Value::String(String::from(TRACK_QUERY_PROJECTION_SCHEMA_VERSION)),
        )?;
    }

    connection
        .execute("DROP INDEX IF EXISTS idx_tracks_sortable_projection", [])
        .map_err(|error| {
            format!("Failed to drop legacy desktop track projection index: {error}")
        })?;

    Ok(())
}

fn ensure_track_query_projection_triggers(connection: &Connection) -> Result<(), String> {
    connection
        .execute_batch(
            "
            DROP TRIGGER IF EXISTS trg_tracks_query_projection_insert;
            DROP TRIGGER IF EXISTS trg_tracks_query_projection_update;
            DROP TRIGGER IF EXISTS trg_tracks_query_projection_delete;

            CREATE TRIGGER trg_tracks_query_projection_insert
            AFTER INSERT ON tracks
            BEGIN
              DELETE FROM track_query_projection WHERE id = NEW.id;
              INSERT INTO track_query_projection
                (id, library_id, display_title, title, artist, album_artist, album, genre,
                 composer, lyricist, comment, file_name, format, duration, file_size, bitrate,
                 sample_rate, bit_depth, is_favorite)
              VALUES
                (NEW.id, NEW.library_id, NEW.display_title, NEW.title, NEW.artist,
                 NEW.album_artist, NEW.album, NEW.genre, NEW.composer, NEW.lyricist,
                 NEW.comment, NEW.file_name, NEW.format, NEW.duration, NEW.file_size,
                 NEW.bitrate, NEW.sample_rate, NEW.bit_depth, NEW.is_favorite);
            END;

            CREATE TRIGGER trg_tracks_query_projection_update
            AFTER UPDATE ON tracks
            BEGIN
              UPDATE track_query_projection
              SET library_id = NEW.library_id,
                  display_title = NEW.display_title,
                  title = NEW.title,
                  artist = NEW.artist,
                  album_artist = NEW.album_artist,
                  album = NEW.album,
                  genre = NEW.genre,
                  composer = NEW.composer,
                  lyricist = NEW.lyricist,
                  comment = NEW.comment,
                  file_name = NEW.file_name,
                  format = NEW.format,
                  duration = NEW.duration,
                  file_size = NEW.file_size,
                  bitrate = NEW.bitrate,
                  sample_rate = NEW.sample_rate,
                  bit_depth = NEW.bit_depth,
                  is_favorite = NEW.is_favorite
              WHERE id = NEW.id;

              INSERT INTO track_query_projection
                (id, library_id, display_title, title, artist, album_artist, album, genre,
                 composer, lyricist, comment, file_name, format, duration, file_size, bitrate,
                 sample_rate, bit_depth, is_favorite)
              SELECT
                NEW.id, NEW.library_id, NEW.display_title, NEW.title, NEW.artist,
                 NEW.album_artist, NEW.album, NEW.genre, NEW.composer, NEW.lyricist,
                 NEW.comment, NEW.file_name, NEW.format, NEW.duration, NEW.file_size,
                 NEW.bitrate, NEW.sample_rate, NEW.bit_depth, NEW.is_favorite
              WHERE NOT EXISTS (
                SELECT 1 FROM track_query_projection WHERE id = NEW.id
              );
            END;

            CREATE TRIGGER trg_tracks_query_projection_delete
            AFTER DELETE ON tracks
            BEGIN
              DELETE FROM track_query_projection WHERE id = OLD.id;
            END;
            ",
        )
        .map_err(|error| {
            format!("Failed to refresh desktop track query projection triggers: {error}")
        })
}

fn count_rows(connection: &Connection, table_name: &str) -> Result<i64, String> {
    connection
        .query_row(&format!("SELECT COUNT(*) FROM {table_name}"), [], |row| {
            row.get::<_, i64>(0)
        })
        .map_err(|error| format!("Failed to count {table_name} rows: {error}"))
}

fn index_exists(connection: &Connection, index_name: &str) -> Result<bool, String> {
    let exists = connection
        .query_row(
            "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = ?1",
            [index_name],
            |_row| Ok(true),
        )
        .optional()
        .map_err(|error| format!("Failed to inspect index {index_name}: {error}"))?
        .unwrap_or(false);

    Ok(exists)
}

fn rebuild_track_query_projection(connection: &Connection) -> Result<(), String> {
    let source_table = if index_exists(connection, "idx_tracks_sortable_projection")? {
        "tracks INDEXED BY idx_tracks_sortable_projection"
    } else {
        "tracks"
    };

    connection
        .execute_batch("BEGIN IMMEDIATE TRANSACTION")
        .map_err(|error| {
            format!("Failed to begin desktop track query projection rebuild: {error}")
        })?;

    let rebuild_sql = format!(
        "
            DELETE FROM track_query_projection;
            INSERT INTO track_query_projection
              (id, library_id, display_title, title, artist, album_artist, album, genre,
               composer, lyricist, comment, file_name, format, duration, file_size, bitrate,
               sample_rate, bit_depth, is_favorite)
            SELECT id, library_id, display_title, title, artist, album_artist, album, genre,
                   composer, lyricist, comment, file_name, format, duration, file_size, bitrate,
                   sample_rate, bit_depth, is_favorite
            FROM {source_table};
            "
    );
    let rebuild_result = connection
        .execute_batch(&rebuild_sql)
        .map_err(|error| format!("Failed to rebuild desktop track query projection: {error}"));

    if let Err(error) = rebuild_result {
        let _ = connection.execute_batch("ROLLBACK");
        return Err(error);
    }

    connection.execute_batch("COMMIT").map_err(|error| {
        format!("Failed to commit desktop track query projection rebuild: {error}")
    })?;

    Ok(())
}

fn load_table_column_names(
    connection: &Connection,
    table_name: &str,
) -> Result<HashSet<String>, String> {
    let mut statement = connection
        .prepare(&format!("PRAGMA table_info({table_name})"))
        .map_err(|error| format!("Failed to inspect {table_name} schema: {error}"))?;
    let rows = statement
        .query_map([], |row| row.get::<_, String>(1))
        .map_err(|error| format!("Failed to query {table_name} schema: {error}"))?;
    let mut column_names = HashSet::new();

    for row in rows {
        column_names.insert(
            row.map_err(|error| format!("Failed to read {table_name} schema row: {error}"))?,
        );
    }

    Ok(column_names)
}

fn backfill_track_projection_columns(connection: &Connection) -> Result<(), String> {
    if backfill_track_projection_columns_with_json_extract(connection).is_ok() {
        return Ok(());
    }

    let payload_rows = {
        let mut statement = connection
            .prepare("SELECT id, payload_json FROM tracks")
            .map_err(|error| {
                format!("Failed to prepare desktop track projection backfill: {error}")
            })?;
        let rows = statement
            .query_map([], |row| {
                Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
            })
            .map_err(|error| {
                format!("Failed to query desktop track projection backfill: {error}")
            })?;
        let mut payload_rows = Vec::new();

        for row in rows {
            payload_rows.push(row.map_err(|error| {
                format!("Failed to read desktop track projection backfill row: {error}")
            })?);
        }

        payload_rows
    };

    if payload_rows.is_empty() {
        return Ok(());
    }

    connection
        .execute_batch("BEGIN IMMEDIATE TRANSACTION")
        .map_err(|error| format!("Failed to begin desktop track projection backfill: {error}"))?;

    let backfill_result = (|| {
        let mut statement = connection
            .prepare(
                "UPDATE tracks
                 SET artist = ?2,
                     album_artist = ?3,
                     album = ?4,
                     genre = ?5,
                     composer = ?6,
                     lyricist = ?7,
                     comment = ?8,
                     duration = ?9,
                     file_size = ?10,
                     bitrate = ?11,
                     sample_rate = ?12,
                     bit_depth = ?13
                 WHERE id = ?1",
            )
            .map_err(|error| {
                format!("Failed to prepare desktop track projection update: {error}")
            })?;

        for (track_id, payload_json) in payload_rows {
            let record = deserialize_value(&payload_json, "desktop track projection backfill")?;
            let projection = build_track_projection(&record);

            statement
                .execute(params![
                    track_id,
                    projection.artist,
                    projection.album_artist,
                    projection.album,
                    projection.genre,
                    projection.composer,
                    projection.lyricist,
                    projection.comment,
                    projection.duration,
                    projection.file_size,
                    projection.bitrate,
                    projection.sample_rate,
                    projection.bit_depth,
                ])
                .map_err(|error| {
                    format!("Failed to update desktop track projection '{track_id}': {error}")
                })?;
        }

        Ok(())
    })();

    if let Err(error) = backfill_result {
        let _ = connection.execute_batch("ROLLBACK");
        return Err(error);
    }

    connection
        .execute_batch("COMMIT")
        .map_err(|error| format!("Failed to commit desktop track projection backfill: {error}"))?;

    Ok(())
}

fn backfill_track_projection_columns_with_json_extract(
    connection: &Connection,
) -> Result<(), String> {
    connection
        .execute(
            "UPDATE tracks
             SET artist = COALESCE(json_extract(payload_json, '$.artist'), ''),
                 album_artist = COALESCE(json_extract(payload_json, '$.albumArtist'), ''),
                 album = COALESCE(json_extract(payload_json, '$.album'), ''),
                 genre = COALESCE(json_extract(payload_json, '$.genre'), ''),
                 composer = COALESCE(json_extract(payload_json, '$.composer'), ''),
                 lyricist = COALESCE(json_extract(payload_json, '$.lyricist'), ''),
                 comment = COALESCE(json_extract(payload_json, '$.comment'), ''),
                 duration = COALESCE(json_extract(payload_json, '$.duration'), 0),
                 file_size = COALESCE(
                   json_extract(payload_json, '$.fileSize'),
                   json_extract(payload_json, '$.size'),
                   0
                 ),
                 bitrate = COALESCE(json_extract(payload_json, '$.bitrate'), 0),
                 sample_rate = COALESCE(json_extract(payload_json, '$.sampleRate'), 0),
                 bit_depth = COALESCE(json_extract(payload_json, '$.bitDepth'), 0)",
            [],
        )
        .map_err(|error| format!("Failed to backfill desktop track projections: {error}"))?;

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use rusqlite::{params, Connection, OptionalExtension};
    use serde_json::json;
    use std::{env, fs};

    fn setup_connection() -> Connection {
        let connection = Connection::open_in_memory().expect("in-memory db should open");
        initialize_schema(&connection).expect("schema should initialize");
        connection
    }

    fn insert_track(connection: &Connection, id: &str) {
        connection
            .execute(
                "INSERT INTO tracks
                   (id, library_id, library_order, imported_at_text, is_favorite,
                    title, display_title, file_name, format, payload_json, updated_at)
                 VALUES (?1, 'library-a', 0, '2026-05-12T00:00:00Z', 0,
                         'Original title', 'Original title', 'track.mp3', 'MP3', '{}', 1)",
                [id],
            )
            .expect("track should insert");
    }

    #[test]
    fn track_artwork_schema_upgrades_v1_table_before_creating_index() {
        let connection = Connection::open_in_memory().expect("in-memory db should open");
        connection
            .execute_batch(
                "
                CREATE TABLE track_artwork (
                  track_id TEXT PRIMARY KEY NOT NULL,
                  artwork_text TEXT NOT NULL,
                  byte_length INTEGER NOT NULL DEFAULT 0,
                  updated_at INTEGER NOT NULL
                );
                ",
            )
            .expect("legacy artwork table should initialize");

        initialize_schema(&connection).expect("schema should upgrade legacy artwork table");

        let columns =
            load_table_column_names(&connection, "track_artwork").expect("columns should load");
        assert!(columns.contains("artwork_path"));
        assert!(columns.contains("mime_type"));
        assert!(columns.contains("content_hash"));
        assert!(index_exists(&connection, "idx_track_artwork_content_hash")
            .expect("index lookup should run"));
    }

    #[test]
    fn track_artwork_migration_moves_artwork_out_of_payload_json() {
        let connection = setup_connection();
        let payload = json!({
            "id": "track-a",
            "libraryId": "library-a",
            "libraryOrder": 0,
            "importedAt": "2026-05-12T00:00:00Z",
            "isFavorite": false,
            "title": "Artwork track",
            "displayTitle": "Artwork track",
            "fileName": "track.flac",
            "format": "FLAC",
            "artwork": "https://example.test/cover.jpg",
        });

        connection
            .execute(
                "INSERT INTO tracks
                   (id, library_id, library_order, imported_at_text, is_favorite,
                    title, display_title, file_name, format, payload_json, updated_at)
                 VALUES ('track-a', 'library-a', 0, '2026-05-12T00:00:00Z', 0,
                         'Artwork track', 'Artwork track', 'track.flac', 'FLAC', ?1, 1)",
                [payload.to_string()],
            )
            .expect("legacy artwork track should insert");
        connection
            .execute(
                "DELETE FROM app_state WHERE key = ?1",
                [TRACK_ARTWORK_SCHEMA_STATE_KEY],
            )
            .expect("schema state should reset");

        initialize_schema(&connection).expect("schema should migrate artwork");

        let (stored_text, stored_path, stored_length): (String, String, i64) = connection
            .query_row(
                "SELECT artwork_text, artwork_path, byte_length FROM track_artwork WHERE track_id = 'track-a'",
                [],
                |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)),
            )
            .expect("artwork should move to asset side table");
        assert_eq!(stored_text, "https://example.test/cover.jpg");
        assert_eq!(stored_path, "");
        assert_eq!(stored_length, 30);

        let payload_json: String = connection
            .query_row(
                "SELECT payload_json FROM tracks WHERE id = 'track-a'",
                [],
                |row| row.get(0),
            )
            .expect("track payload should still exist");
        let migrated_payload =
            deserialize_value(&payload_json, "migrated track").expect("payload should decode");
        assert_eq!(
            optional_text_field(&migrated_payload, "artwork").as_deref(),
            Some("")
        );
    }

    #[test]
    fn external_source_index_migration_rewrites_legacy_cache_paths() {
        let connection = setup_connection();
        let track = json!({
            "id": "remote-track",
            "libraryId": "library",
            "libraryOrder": 0,
            "importedAt": "2026-05-16T00:00:00Z",
            "isFavorite": false,
            "title": "Remote Track",
            "displayTitle": "Remote Track",
            "fileName": "remote.flac",
            "format": "FLAC",
            "source": {
                "kind": "external-cache",
                "provider": "webdav",
                "connectionId": "connection",
                "remoteId": "https://dav.example.test/music/remote.flac",
                "path": "C:\\Users\\demo\\AppData\\Local\\OFPlayer\\cache\\external-sources\\webdav\\cached.flac",
                "originPath": "https://dav.example.test/music/remote.flac",
                "persistUrl": false
            }
        });

        connection
            .execute(
                "INSERT INTO tracks
                   (id, library_id, library_order, imported_at_text, is_favorite,
                    title, display_title, file_name, format, payload_json, updated_at)
                 VALUES ('remote-track', 'library', 0, '2026-05-16T00:00:00Z', 0,
                         'Remote Track', 'Remote Track', 'remote.flac', 'FLAC', ?1, 1)",
                [track.to_string()],
            )
            .expect("legacy remote track should insert");
        connection
            .execute(
                "DELETE FROM app_state WHERE key = ?1",
                [EXTERNAL_SOURCE_INDEX_SCHEMA_STATE_KEY],
            )
            .expect("schema state should reset");

        initialize_schema(&connection).expect("schema should migrate external source indexes");

        let payload_json: String = connection
            .query_row(
                "SELECT payload_json FROM tracks WHERE id = 'remote-track'",
                [],
                |row| row.get(0),
            )
            .expect("track payload should still exist");
        let migrated_payload = deserialize_value(&payload_json, "migrated remote track")
            .expect("payload should decode");
        let source = migrated_payload
            .get("source")
            .and_then(Value::as_object)
            .expect("source should exist");

        assert_eq!(source.get("kind").and_then(Value::as_str), Some("webdav"));
        assert_eq!(
            source.get("path").and_then(Value::as_str),
            Some("https://dav.example.test/music/remote.flac")
        );
        assert_eq!(source.get("url").and_then(Value::as_str), Some(""));
    }

    #[test]
    fn local_source_index_migration_prefers_existing_origin_path() {
        let connection = setup_connection();
        let fixture_dir =
            env::temp_dir().join(format!("ofplayer-local-index-{}", current_unix_timestamp()));
        fs::create_dir_all(&fixture_dir).expect("fixture dir should be created");
        let origin_path = fixture_dir.join("origin.flac");
        fs::write(&origin_path, b"audio").expect("origin fixture should be writable");
        let origin_path_text = origin_path.to_string_lossy().to_string();
        let track = json!({
            "id": "local-track",
            "libraryId": "library",
            "libraryOrder": 0,
            "importedAt": "2026-05-17T00:00:00Z",
            "isFavorite": false,
            "title": "Local Track",
            "displayTitle": "Local Track",
            "fileName": "origin.flac",
            "format": "FLAC",
            "source": {
                "kind": "native-file",
                "path": "C:\\Users\\demo\\Music\\OFPlayer Library\\libraries\\library\\origin.flac",
                "originPath": origin_path_text
            }
        });

        connection
            .execute(
                "INSERT INTO tracks
                   (id, library_id, library_order, imported_at_text, is_favorite,
                    title, display_title, file_name, format, payload_json, updated_at)
                 VALUES ('local-track', 'library', 0, '2026-05-17T00:00:00Z', 0,
                         'Local Track', 'Local Track', 'origin.flac', 'FLAC', ?1, 1)",
                [track.to_string()],
            )
            .expect("legacy local track should insert");
        connection
            .execute(
                "DELETE FROM app_state WHERE key = ?1",
                [LOCAL_SOURCE_INDEX_SCHEMA_STATE_KEY],
            )
            .expect("schema state should reset");

        initialize_schema(&connection).expect("schema should migrate local source indexes");

        let payload_json: String = connection
            .query_row(
                "SELECT payload_json FROM tracks WHERE id = 'local-track'",
                [],
                |row| row.get(0),
            )
            .expect("track payload should still exist");
        let migrated_payload = deserialize_value(&payload_json, "migrated local track")
            .expect("payload should decode");
        let source = migrated_payload
            .get("source")
            .and_then(Value::as_object)
            .expect("source should exist");

        assert_eq!(
            source.get("kind").and_then(Value::as_str),
            Some("native-file")
        );
        assert_eq!(
            source.get("path").and_then(Value::as_str),
            Some(origin_path_text.as_str())
        );
        assert_eq!(source.get("indexed").and_then(Value::as_bool), Some(true));

        let _ = fs::remove_dir_all(fixture_dir);
    }

    #[test]
    fn track_query_projection_tracks_insert_update_and_delete() {
        let connection = setup_connection();
        insert_track(&connection, "track-a");

        let inserted_title: String = connection
            .query_row(
                "SELECT title FROM track_query_projection WHERE id = 'track-a'",
                [],
                |row| row.get(0),
            )
            .expect("projection should insert");
        assert_eq!(inserted_title, "Original title");

        connection
            .execute(
                "UPDATE tracks SET title = ?2, display_title = ?2, is_favorite = 1 WHERE id = ?1",
                params!["track-a", "Updated title"],
            )
            .expect("track should update");
        let (updated_title, is_favorite): (String, i64) = connection
            .query_row(
                "SELECT title, is_favorite FROM track_query_projection WHERE id = 'track-a'",
                [],
                |row| Ok((row.get(0)?, row.get(1)?)),
            )
            .expect("projection should update");
        assert_eq!(updated_title, "Updated title");
        assert_eq!(is_favorite, 1);

        connection
            .execute("DELETE FROM tracks WHERE id = 'track-a'", [])
            .expect("track should delete");
        let projection_exists = connection
            .query_row(
                "SELECT 1 FROM track_query_projection WHERE id = 'track-a'",
                [],
                |_row| Ok(true),
            )
            .optional()
            .expect("projection lookup should run")
            .unwrap_or(false);
        assert!(!projection_exists);
    }

    #[test]
    fn track_query_projection_handles_track_upsert_conflict_update() {
        let connection = setup_connection();
        insert_track(&connection, "track-a");

        connection
            .execute(
                "INSERT INTO tracks
                   (id, library_id, library_order, imported_at_text, is_favorite,
                    title, display_title, artist, album_artist, album, genre, composer,
                    lyricist, comment, file_name, format, duration, file_size, bitrate,
                    sample_rate, bit_depth, payload_json, updated_at)
                 VALUES (?1, 'library-a', 1, '2026-05-12T00:00:00Z', 1,
                         ?2, ?2, '', '', '', '', '', '', '', 'track.mp3', 'MP3',
                         0, 0, 0, 0, 0, '{}', 2)
                 ON CONFLICT(id) DO UPDATE SET
                   library_id = excluded.library_id,
                   library_order = excluded.library_order,
                   imported_at_text = excluded.imported_at_text,
                   is_favorite = excluded.is_favorite,
                   title = excluded.title,
                   display_title = excluded.display_title,
                   artist = excluded.artist,
                   album_artist = excluded.album_artist,
                   album = excluded.album,
                   genre = excluded.genre,
                   composer = excluded.composer,
                   lyricist = excluded.lyricist,
                   comment = excluded.comment,
                   file_name = excluded.file_name,
                   format = excluded.format,
                   duration = excluded.duration,
                   file_size = excluded.file_size,
                   bitrate = excluded.bitrate,
                   sample_rate = excluded.sample_rate,
                   bit_depth = excluded.bit_depth,
                   payload_json = excluded.payload_json,
                   updated_at = excluded.updated_at",
                params!["track-a", "Upserted title"],
            )
            .expect("track upsert conflict update should refresh the projection");

        let (updated_title, is_favorite): (String, i64) = connection
            .query_row(
                "SELECT title, is_favorite FROM track_query_projection WHERE id = 'track-a'",
                [],
                |row| Ok((row.get(0)?, row.get(1)?)),
            )
            .expect("projection should update after upsert");
        assert_eq!(updated_title, "Upserted title");
        assert_eq!(is_favorite, 1);
    }

    #[test]
    fn track_query_projection_refreshes_legacy_update_trigger() {
        let connection = setup_connection();
        connection
            .execute_batch(
                "
                DROP TRIGGER IF EXISTS trg_tracks_query_projection_update;
                CREATE TRIGGER trg_tracks_query_projection_update
                AFTER UPDATE ON tracks
                BEGIN
                  INSERT INTO track_query_projection
                    (id, library_id, display_title, title, artist, album_artist, album, genre,
                     composer, lyricist, comment, file_name, format, duration, file_size, bitrate,
                     sample_rate, bit_depth, is_favorite)
                  VALUES
                    (NEW.id, NEW.library_id, NEW.display_title, NEW.title, NEW.artist,
                     NEW.album_artist, NEW.album, NEW.genre, NEW.composer, NEW.lyricist,
                     NEW.comment, NEW.file_name, NEW.format, NEW.duration, NEW.file_size,
                     NEW.bitrate, NEW.sample_rate, NEW.bit_depth, NEW.is_favorite);
                END;
                ",
            )
            .expect("legacy update trigger should install");
        insert_track(&connection, "track-a");

        let legacy_update_error = connection
            .execute(
                "UPDATE tracks SET title = ?2, display_title = ?2 WHERE id = ?1",
                params!["track-a", "Legacy trigger update"],
            )
            .expect_err("legacy update trigger should violate projection id uniqueness");
        assert!(legacy_update_error
            .to_string()
            .contains("UNIQUE constraint"));

        initialize_schema(&connection).expect("schema should refresh query projection triggers");
        connection
            .execute(
                "UPDATE tracks SET title = ?2, display_title = ?2 WHERE id = ?1",
                params!["track-a", "Refreshed trigger update"],
            )
            .expect("refreshed update trigger should replace the projection");

        let updated_title: String = connection
            .query_row(
                "SELECT title FROM track_query_projection WHERE id = 'track-a'",
                [],
                |row| row.get(0),
            )
            .expect("projection should update after trigger refresh");
        assert_eq!(updated_title, "Refreshed trigger update");
    }

    #[test]
    fn sortable_projection_index_is_not_created() {
        let connection = setup_connection();
        let index_exists = connection
            .query_row(
                "SELECT 1
                 FROM pragma_index_list('tracks')
                 WHERE name = 'idx_tracks_sortable_projection'",
                [],
                |_row| Ok(true),
            )
            .optional()
            .expect("index lookup should run")
            .unwrap_or(false);

        assert!(!index_exists);
    }
}
