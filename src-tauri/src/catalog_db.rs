use crate::{
    artwork_store::{resolve_stored_track_artwork, store_track_artwork},
    db_helpers::*,
    desktop_types::CatalogSnapshot,
    schema::build_track_projection,
};
use rusqlite::{params, params_from_iter, Connection, OptionalExtension, Transaction};
use serde_json::{json, Value};
use std::collections::{HashMap, HashSet};
use uuid::Uuid;

const DEFAULT_LIBRARY_ID: &str = "library-default";
const DEFAULT_ALL_TRACKS_PLAYLIST_ID: &str = "playlist-default-all-tracks";
const SYSTEM_PLAYLIST_ALL_TRACKS_KEY: &str = "all-tracks";
const EXTERNAL_PROVIDER_WEBDAV: &str = "webdav";

pub(crate) fn load_catalog_snapshot_from_connection(
    connection: &Connection,
) -> Result<CatalogSnapshot, String> {
    Ok(CatalogSnapshot {
        libraries: load_payloads(
            connection,
            "SELECT payload_json FROM libraries ORDER BY order_index ASC, created_at_text ASC, id ASC",
            [],
            "desktop libraries",
        )?,
        playlists: load_payloads(
            connection,
            "SELECT payload_json FROM playlists ORDER BY library_id ASC, order_index ASC, created_at_text ASC, id ASC",
            [],
            "desktop playlists",
        )?,
        tracks: load_payloads(
            connection,
            "SELECT payload_json FROM tracks ORDER BY library_id ASC, library_order ASC, imported_at_text ASC, id ASC",
            [],
            "desktop tracks",
        )?,
        playlist_track_relations: load_payloads(
            connection,
            "SELECT payload_json FROM playlist_track_relations ORDER BY playlist_id ASC, order_index ASC, added_at_text ASC, id ASC",
            [],
            "desktop playlist-track relations",
        )?,
    })
}

pub(crate) fn load_catalog_shell_snapshot_from_connection(
    connection: &Connection,
    include_playlist_track_relations: bool,
) -> Result<CatalogSnapshot, String> {
    Ok(CatalogSnapshot {
        libraries: load_payloads(
            connection,
            "SELECT payload_json FROM libraries ORDER BY order_index ASC, created_at_text ASC, id ASC",
            [],
            "desktop libraries",
        )?,
        playlists: load_payloads(
            connection,
            "SELECT payload_json FROM playlists ORDER BY library_id ASC, order_index ASC, created_at_text ASC, id ASC",
            [],
            "desktop playlists",
        )?,
        tracks: Vec::new(),
        playlist_track_relations: if include_playlist_track_relations {
            load_payloads(
                connection,
                "SELECT payload_json FROM playlist_track_relations ORDER BY playlist_id ASC, order_index ASC, added_at_text ASC, id ASC",
                [],
                "desktop playlist-track relations",
            )?
        } else {
            Vec::new()
        },
    })
}

pub(crate) fn ensure_catalog_shell_consistency(connection: &mut Connection) -> Result<(), String> {
    let libraries = load_payloads(
        connection,
        "SELECT payload_json FROM libraries ORDER BY order_index ASC, created_at_text ASC, id ASC",
        [],
        "desktop libraries",
    )?;
    let playlists = load_payloads(
        connection,
        "SELECT payload_json FROM playlists ORDER BY library_id ASC, order_index ASC, created_at_text ASC, id ASC",
        [],
        "desktop playlists",
    )?;
    let mut libraries_to_upsert = Vec::new();
    let mut playlists_to_upsert = Vec::new();

    if libraries.is_empty() {
        libraries_to_upsert.push(create_seed_default_library_value());
        playlists_to_upsert.push(create_default_playlist_value(
            DEFAULT_LIBRARY_ID,
            0,
            Some(DEFAULT_ALL_TRACKS_PLAYLIST_ID),
        ));
    } else {
        for library in &libraries {
            let library_id = required_text_field(library, "id", "desktop library")?;
            let has_default_playlist = playlists.iter().any(|playlist| {
                optional_text_field(playlist, "libraryId").as_deref() == Some(library_id.as_str())
                    && optional_text_field(playlist, "systemKey").as_deref()
                        == Some(SYSTEM_PLAYLIST_ALL_TRACKS_KEY)
            });

            if has_default_playlist {
                continue;
            }

            let order = playlists
                .iter()
                .filter(|playlist| {
                    optional_text_field(playlist, "libraryId").as_deref()
                        == Some(library_id.as_str())
                })
                .count();
            let playlist_id = if library_id == DEFAULT_LIBRARY_ID {
                Some(DEFAULT_ALL_TRACKS_PLAYLIST_ID)
            } else {
                None
            };

            playlists_to_upsert.push(create_default_playlist_value(
                &library_id,
                order,
                playlist_id,
            ));
        }
    }

    if libraries_to_upsert.is_empty() && playlists_to_upsert.is_empty() {
        return Ok(());
    }

    let transaction = connection.transaction().map_err(|error| {
        format!("Failed to open catalog shell consistency transaction: {error}")
    })?;

    if !libraries_to_upsert.is_empty() {
        upsert_libraries(&transaction, &libraries_to_upsert)?;
    }

    if !playlists_to_upsert.is_empty() {
        upsert_playlists(&transaction, &playlists_to_upsert)?;
    }

    transaction
        .commit()
        .map_err(|error| format!("Failed to commit catalog shell consistency repairs: {error}"))?;

    Ok(())
}

pub(crate) fn ensure_catalog_consistency(
    connection: &mut Connection,
) -> Result<CatalogSnapshot, String> {
    let snapshot = load_catalog_snapshot_from_connection(connection)?;
    let mut libraries_to_upsert = Vec::new();
    let mut playlists_to_upsert = Vec::new();
    let mut tracks_to_upsert = Vec::new();

    if snapshot.libraries.is_empty() {
        libraries_to_upsert.push(create_seed_default_library_value());
        playlists_to_upsert.push(create_default_playlist_value(
            DEFAULT_LIBRARY_ID,
            0,
            Some(DEFAULT_ALL_TRACKS_PLAYLIST_ID),
        ));

        for (index, track) in snapshot.tracks.iter().enumerate() {
            tracks_to_upsert.push(repair_track_for_library(track, DEFAULT_LIBRARY_ID, index)?);
        }
    } else {
        let default_library_id =
            required_text_field(&snapshot.libraries[0], "id", "desktop library")?;

        for library in &snapshot.libraries {
            let library_id = required_text_field(library, "id", "desktop library")?;
            let has_default_playlist = snapshot.playlists.iter().any(|playlist| {
                optional_text_field(playlist, "libraryId").as_deref() == Some(library_id.as_str())
                    && optional_text_field(playlist, "systemKey").as_deref()
                        == Some(SYSTEM_PLAYLIST_ALL_TRACKS_KEY)
            });

            if !has_default_playlist {
                let order = snapshot
                    .playlists
                    .iter()
                    .filter(|playlist| {
                        optional_text_field(playlist, "libraryId").as_deref()
                            == Some(library_id.as_str())
                    })
                    .count();
                let playlist_id = if library_id == DEFAULT_LIBRARY_ID {
                    Some(DEFAULT_ALL_TRACKS_PLAYLIST_ID)
                } else {
                    None
                };

                playlists_to_upsert.push(create_default_playlist_value(
                    &library_id,
                    order,
                    playlist_id,
                ));
            }
        }

        for (index, track) in snapshot
            .tracks
            .iter()
            .filter(|track| {
                normalized_optional_text(track.get("libraryId").and_then(Value::as_str)).is_none()
            })
            .enumerate()
        {
            tracks_to_upsert.push(repair_track_for_library(track, &default_library_id, index)?);
        }
    }

    if libraries_to_upsert.is_empty()
        && playlists_to_upsert.is_empty()
        && tracks_to_upsert.is_empty()
    {
        return Ok(snapshot);
    }

    let transaction = connection
        .transaction()
        .map_err(|error| format!("Failed to open catalog consistency transaction: {error}"))?;

    if !libraries_to_upsert.is_empty() {
        upsert_libraries(&transaction, &libraries_to_upsert)?;
    }

    if !playlists_to_upsert.is_empty() {
        upsert_playlists(&transaction, &playlists_to_upsert)?;
    }

    if !tracks_to_upsert.is_empty() {
        upsert_tracks(&transaction, &tracks_to_upsert)?;
    }

    transaction
        .commit()
        .map_err(|error| format!("Failed to commit catalog consistency repairs: {error}"))?;

    load_catalog_snapshot_from_connection(connection)
}

pub(crate) fn upsert_libraries(
    transaction: &Transaction<'_>,
    records: &[Value],
) -> Result<(), String> {
    if records.is_empty() {
        return Ok(());
    }

    let mut statement = transaction
        .prepare(
            "INSERT INTO libraries (id, order_index, created_at_text, payload_json, updated_at)
             VALUES (?1, ?2, ?3, ?4, ?5)
             ON CONFLICT(id) DO UPDATE SET
               order_index = excluded.order_index,
               created_at_text = excluded.created_at_text,
               payload_json = excluded.payload_json,
               updated_at = excluded.updated_at",
        )
        .map_err(|error| format!("Failed to prepare desktop library upsert: {error}"))?;

    for record in records {
        let id = required_text_field(record, "id", "desktop library")?;
        let order_index = required_integer_field(record, "order", "desktop library")?;
        let created_at = required_text_field(record, "createdAt", "desktop library")?;
        let payload_json = serialize_value(record, "desktop library")?;
        let updated_at = current_unix_timestamp();

        statement
            .execute(params![
                id,
                order_index,
                created_at,
                payload_json,
                updated_at
            ])
            .map_err(|error| format!("Failed to upsert desktop library '{id}': {error}"))?;
    }

    Ok(())
}

pub(crate) fn upsert_playlists(
    transaction: &Transaction<'_>,
    records: &[Value],
) -> Result<(), String> {
    if records.is_empty() {
        return Ok(());
    }

    let mut statement = transaction
        .prepare(
            "INSERT INTO playlists
               (id, library_id, order_index, kind, system_key, created_at_text, payload_json, updated_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)
             ON CONFLICT(id) DO UPDATE SET
               library_id = excluded.library_id,
               order_index = excluded.order_index,
               kind = excluded.kind,
               system_key = excluded.system_key,
               created_at_text = excluded.created_at_text,
               payload_json = excluded.payload_json,
               updated_at = excluded.updated_at",
        )
        .map_err(|error| format!("Failed to prepare desktop playlist upsert: {error}"))?;

    for record in records {
        let id = required_text_field(record, "id", "desktop playlist")?;
        let library_id = required_text_field(record, "libraryId", "desktop playlist")?;
        let order_index = required_integer_field(record, "order", "desktop playlist")?;
        let kind = required_text_field(record, "kind", "desktop playlist")?;
        let system_key = optional_text_field(record, "systemKey");
        let created_at = required_text_field(record, "createdAt", "desktop playlist")?;
        let payload_json = serialize_value(record, "desktop playlist")?;
        let updated_at = current_unix_timestamp();

        statement
            .execute(params![
                id,
                library_id,
                order_index,
                kind,
                system_key,
                created_at,
                payload_json,
                updated_at
            ])
            .map_err(|error| format!("Failed to upsert desktop playlist '{id}': {error}"))?;
    }

    Ok(())
}

pub(crate) fn upsert_playlist_track_relations(
    transaction: &Transaction<'_>,
    records: &[Value],
) -> Result<(), String> {
    if records.is_empty() {
        return Ok(());
    }

    let mut statement = transaction
        .prepare(
            "INSERT INTO playlist_track_relations
               (id, playlist_id, track_id, order_index, added_at_text, payload_json, updated_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)
             ON CONFLICT(id) DO UPDATE SET
               playlist_id = excluded.playlist_id,
               track_id = excluded.track_id,
               order_index = excluded.order_index,
               added_at_text = excluded.added_at_text,
               payload_json = excluded.payload_json,
               updated_at = excluded.updated_at",
        )
        .map_err(|error| {
            format!("Failed to prepare desktop playlist-track relation upsert: {error}")
        })?;

    for record in records {
        let id = required_text_field(record, "id", "desktop playlist-track relation")?;
        let playlist_id =
            required_text_field(record, "playlistId", "desktop playlist-track relation")?;
        let track_id = required_text_field(record, "trackId", "desktop playlist-track relation")?;
        let order_index =
            required_integer_field(record, "order", "desktop playlist-track relation")?;
        let added_at = required_text_field(record, "addedAt", "desktop playlist-track relation")?;
        let payload_json = serialize_value(record, "desktop playlist-track relation")?;
        let updated_at = current_unix_timestamp();

        statement
            .execute(params![
                id,
                playlist_id,
                track_id,
                order_index,
                added_at,
                payload_json,
                updated_at
            ])
            .map_err(|error| {
                format!("Failed to upsert desktop playlist-track relation '{id}': {error}")
            })?;
    }

    Ok(())
}

pub(crate) fn upsert_tracks(
    transaction: &Transaction<'_>,
    records: &[Value],
) -> Result<(), String> {
    if records.is_empty() {
        return Ok(());
    }

    let mut statement = transaction
        .prepare(
            "INSERT INTO tracks
               (id, library_id, library_order, imported_at_text, is_favorite, title, display_title, artist, album_artist, album, genre, composer, lyricist, comment, file_name, format, duration, file_size, bitrate, sample_rate, bit_depth, payload_json, updated_at)
             VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11, ?12, ?13, ?14, ?15, ?16, ?17, ?18, ?19, ?20, ?21, ?22, ?23)
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
        )
        .map_err(|error| format!("Failed to prepare desktop track upsert: {error}"))?;
    let mut artwork_upsert_statement = transaction
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
    let mut artwork_delete_statement = transaction
        .prepare("DELETE FROM track_artwork WHERE track_id = ?1")
        .map_err(|error| format!("Failed to prepare desktop track artwork delete: {error}"))?;

    for record in records {
        let id = required_text_field(record, "id", "desktop track")?;
        let library_id = required_text_field(record, "libraryId", "desktop track")?;
        let library_order = required_integer_field(record, "libraryOrder", "desktop track")?;
        let imported_at = required_text_field(record, "importedAt", "desktop track")?;
        let is_favorite = required_boolean_field(record, "isFavorite", "desktop track")?;
        let title = required_text_field(record, "title", "desktop track")?;
        let display_title = required_text_field(record, "displayTitle", "desktop track")?;
        let projection = build_track_projection(record);
        let file_name = required_text_field(record, "fileName", "desktop track")?;
        let format = required_text_field(record, "format", "desktop track")?;
        let artwork = track_artwork_text(record).unwrap_or_default();
        let payload_json = serialize_value(&strip_track_artwork(record.clone()), "desktop track")?;
        let updated_at = current_unix_timestamp();

        if let Some(stored_artwork) = store_track_artwork(&artwork)? {
            artwork_upsert_statement
                .execute(params![
                    id.as_str(),
                    stored_artwork.artwork_text,
                    stored_artwork.artwork_path,
                    stored_artwork.mime_type,
                    stored_artwork.content_hash,
                    stored_artwork.byte_length,
                    updated_at
                ])
                .map_err(|error| {
                    format!("Failed to upsert desktop track artwork '{id}': {error}")
                })?;
        } else if should_delete_empty_track_artwork(record) {
            artwork_delete_statement
                .execute(params![id.as_str()])
                .map_err(|error| {
                    format!("Failed to delete desktop track artwork '{id}': {error}")
                })?;
        }

        statement
            .execute(params![
                id,
                library_id,
                library_order,
                imported_at,
                if is_favorite { 1 } else { 0 },
                title,
                display_title,
                projection.artist,
                projection.album_artist,
                projection.album,
                projection.genre,
                projection.composer,
                projection.lyricist,
                projection.comment,
                file_name,
                format,
                projection.duration,
                projection.file_size,
                projection.bitrate,
                projection.sample_rate,
                projection.bit_depth,
                payload_json,
                updated_at
            ])
            .map_err(|error| format!("Failed to upsert desktop track '{id}': {error}"))?;
    }

    Ok(())
}

pub(crate) fn strip_track_artwork(mut track: Value) -> Value {
    if let Some(track_object) = track.as_object_mut() {
        track_object.insert(String::from("artwork"), Value::String(String::new()));
    }

    track
}

pub(crate) fn load_track_artwork(
    connection: &Connection,
    track_id: &str,
) -> Result<Option<String>, String> {
    connection
        .query_row(
            "SELECT artwork_text, COALESCE(artwork_path, '') FROM track_artwork WHERE track_id = ?1",
            [track_id],
            |row| {
                Ok(resolve_stored_track_artwork(
                    row.get::<_, String>(0)?,
                    row.get::<_, String>(1)?,
                ))
            },
        )
        .optional()
        .map_err(|error| format!("Failed to read desktop track artwork '{track_id}': {error}"))
}

pub(crate) fn attach_track_artwork(
    connection: &Connection,
    mut track: Value,
) -> Result<Value, String> {
    let track_id = required_text_field(&track, "id", "desktop track")?;
    let artwork = load_track_artwork(connection, &track_id)?.unwrap_or_default();

    if let Some(track_object) = track.as_object_mut() {
        track_object.insert(String::from("artwork"), Value::String(artwork));
    }

    Ok(track)
}

pub(crate) fn delete_track_artwork(
    transaction: &Transaction<'_>,
    track_id: &str,
) -> Result<(), String> {
    transaction
        .execute("DELETE FROM track_artwork WHERE track_id = ?1", [track_id])
        .map_err(|error| format!("Failed to delete desktop track artwork '{track_id}': {error}"))?;

    Ok(())
}

fn track_artwork_text(record: &Value) -> Option<String> {
    optional_text_field(record, "artwork")
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty())
}

fn should_delete_empty_track_artwork(record: &Value) -> bool {
    record.get("artwork").is_some()
        && track_artwork_text(record).is_none()
        && track_source_provider(record)
            .as_deref()
            .is_some_and(|provider| provider == EXTERNAL_PROVIDER_WEBDAV)
}

fn track_source_provider(record: &Value) -> Option<String> {
    record
        .get("source")
        .and_then(Value::as_object)
        .and_then(|source| source.get("provider"))
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(|value| value.to_ascii_lowercase())
}

pub(crate) fn ensure_library_exists(
    transaction: &Transaction<'_>,
    library_id: &str,
) -> Result<(), String> {
    let exists = transaction
        .query_row(
            "SELECT 1 FROM libraries WHERE id = ?1 LIMIT 1",
            [library_id],
            |_row| Ok(true),
        )
        .optional()
        .map_err(|error| format!("Failed to verify desktop library '{library_id}': {error}"))?
        .unwrap_or(false);

    if exists {
        Ok(())
    } else {
        Err(String::from("Library not found."))
    }
}

pub(crate) fn load_required_library_value(
    transaction: &Transaction<'_>,
    library_id: &str,
) -> Result<Value, String> {
    let sql = "SELECT payload_json FROM libraries WHERE id = ?1";
    let payload = transaction
        .query_row(sql, [library_id], |row| row.get::<_, String>(0))
        .optional()
        .map_err(|error| format!("Failed to read desktop library '{library_id}': {error}"))?
        .ok_or_else(|| String::from("Library not found."))?;

    deserialize_value(&payload, "desktop library")
}

pub(crate) fn load_required_playlist_value(
    transaction: &Transaction<'_>,
    playlist_id: &str,
) -> Result<Value, String> {
    load_required_payload_by_id(transaction, "playlists", playlist_id, "desktop playlist")
}

pub(crate) fn load_required_track_value(
    transaction: &Transaction<'_>,
    track_id: &str,
) -> Result<Value, String> {
    load_required_payload_by_id(transaction, "tracks", track_id, "desktop track")
}

pub(crate) fn load_required_payload_by_id(
    transaction: &Transaction<'_>,
    table_name: &str,
    id: &str,
    entity_label: &str,
) -> Result<Value, String> {
    let sql = format!("SELECT payload_json FROM {table_name} WHERE id = ?1");
    let payload = transaction
        .query_row(&sql, [id], |row| row.get::<_, String>(0))
        .optional()
        .map_err(|error| format!("Failed to read {entity_label} '{id}': {error}"))?
        .ok_or_else(|| {
            if entity_label == "desktop track" {
                String::from("Track not found.")
            } else {
                String::from("Playlist not found.")
            }
        })?;

    deserialize_value(&payload, entity_label)
}

pub(crate) fn load_library_playlist_values(
    transaction: &Transaction<'_>,
    library_id: &str,
) -> Result<Vec<Value>, String> {
    load_payloads(
        transaction,
        "SELECT payload_json
         FROM playlists
         WHERE library_id = ?1
         ORDER BY order_index ASC, created_at_text ASC, id ASC",
        [library_id],
        "desktop library playlists",
    )
}

pub(crate) fn load_library_values(transaction: &Transaction<'_>) -> Result<Vec<Value>, String> {
    load_payloads(
        transaction,
        "SELECT payload_json
         FROM libraries
         ORDER BY order_index ASC, created_at_text ASC, id ASC",
        [],
        "desktop libraries",
    )
}

pub(crate) fn load_library_track_values(
    transaction: &Transaction<'_>,
    library_id: &str,
) -> Result<Vec<Value>, String> {
    load_payloads(
        transaction,
        "SELECT payload_json
         FROM tracks
         WHERE library_id = ?1
         ORDER BY library_order ASC, imported_at_text ASC, id ASC",
        [library_id],
        "desktop library tracks",
    )
}

pub(crate) fn load_playlist_relation_values(
    transaction: &Transaction<'_>,
    playlist_id: &str,
) -> Result<Vec<Value>, String> {
    load_payloads(
        transaction,
        "SELECT payload_json
         FROM playlist_track_relations
         WHERE playlist_id = ?1
         ORDER BY order_index ASC, added_at_text ASC, id ASC",
        [playlist_id],
        "desktop playlist relations",
    )
}

pub(crate) fn count_library_playlists(
    transaction: &Transaction<'_>,
    library_id: &str,
) -> Result<usize, String> {
    let count = transaction
        .query_row(
            "SELECT COUNT(id) FROM playlists WHERE library_id = ?1",
            [library_id],
            |row| row.get::<_, i64>(0),
        )
        .map_err(|error| {
            format!("Failed to count desktop playlists for library '{library_id}': {error}")
        })?;

    Ok(usize::try_from(count.max(0)).unwrap_or_default())
}

pub(crate) fn count_libraries(transaction: &Transaction<'_>) -> Result<usize, String> {
    let count = transaction
        .query_row("SELECT COUNT(id) FROM libraries", [], |row| {
            row.get::<_, i64>(0)
        })
        .map_err(|error| format!("Failed to count desktop libraries: {error}"))?;

    Ok(usize::try_from(count.max(0)).unwrap_or_default())
}

pub(crate) fn create_library_value(name: &str, order: usize) -> Value {
    let now = current_iso_timestamp();
    json!({
        "id": format!("library-{}", Uuid::new_v4()),
        "name": name,
        "order": order,
        "isDefault": false,
        "createdAt": now,
        "updatedAt": now,
    })
}

pub(crate) fn create_seed_default_library_value() -> Value {
    let now = current_iso_timestamp();
    json!({
        "id": DEFAULT_LIBRARY_ID,
        "name": "",
        "order": 0,
        "isDefault": true,
        "createdAt": now,
        "updatedAt": now,
    })
}

pub(crate) fn create_default_playlist_value(
    library_id: &str,
    order: usize,
    id_override: Option<&str>,
) -> Value {
    let now = current_iso_timestamp();
    json!({
        "id": id_override
            .map(String::from)
            .unwrap_or_else(|| format!("playlist-{}", Uuid::new_v4())),
        "libraryId": library_id,
        "name": "",
        "order": order,
        "kind": "system",
        "systemKey": SYSTEM_PLAYLIST_ALL_TRACKS_KEY,
        "createdAt": now,
        "updatedAt": now,
    })
}

pub(crate) fn repair_track_for_library(
    track: &Value,
    library_id: &str,
    fallback_library_order: usize,
) -> Result<Value, String> {
    let mut next_track = track.clone();
    let library_order = track
        .get("libraryOrder")
        .and_then(Value::as_u64)
        .map(|value| usize::try_from(value).unwrap_or(fallback_library_order))
        .unwrap_or(fallback_library_order);
    let is_favorite = track
        .get("isFavorite")
        .and_then(Value::as_bool)
        .unwrap_or(false);

    set_string_field(
        &mut next_track,
        "libraryId",
        String::from(library_id),
        "desktop track",
    )?;
    set_u64_field(
        &mut next_track,
        "libraryOrder",
        u64::try_from(library_order).unwrap_or_default(),
    )?;
    set_bool_field(&mut next_track, "isFavorite", is_favorite, "desktop track")?;

    if normalized_optional_text(next_track.get("importedAt").and_then(Value::as_str)).is_none() {
        set_string_field(
            &mut next_track,
            "importedAt",
            current_iso_timestamp(),
            "desktop track",
        )?;
    }

    Ok(next_track)
}

pub(crate) fn create_user_playlist_value(library_id: &str, name: &str, order: usize) -> Value {
    let now = current_iso_timestamp();
    json!({
        "id": format!("playlist-{}", Uuid::new_v4()),
        "libraryId": library_id,
        "name": name,
        "order": order,
        "kind": "user",
        "systemKey": Value::Null,
        "createdAt": now,
        "updatedAt": now,
    })
}

pub(crate) fn create_playlist_track_relation_value(
    playlist_id: &str,
    track_id: &str,
    order: usize,
    added_at: Option<String>,
) -> Value {
    json!({
        "id": format!("{playlist_id}:{track_id}"),
        "playlistId": playlist_id,
        "trackId": track_id,
        "order": order,
        "addedAt": added_at.unwrap_or_else(current_iso_timestamp),
    })
}

pub(crate) fn assert_user_playlist(playlist: &Value) -> Result<(), String> {
    match playlist.get("kind").and_then(Value::as_str) {
        Some("user") => Ok(()),
        Some("system") => Err(String::from("System playlists cannot be modified.")),
        _ => Err(String::from("Playlist not found.")),
    }
}

pub(crate) fn ensure_track_matches_playlist_library(
    track: &Value,
    playlist: &Value,
) -> Result<(), String> {
    let track_library_id = required_text_field(track, "libraryId", "desktop track")?;
    let playlist_library_id = required_text_field(playlist, "libraryId", "desktop playlist")?;

    if track_library_id == playlist_library_id {
        Ok(())
    } else {
        Err(String::from(
            "Track and playlist must belong to the same library.",
        ))
    }
}

pub(crate) fn normalized_optional_text(value: Option<&str>) -> Option<String> {
    value
        .map(str::trim)
        .filter(|text| !text.is_empty())
        .map(String::from)
}

pub(crate) fn normalized_non_empty_text(value: &str) -> Option<String> {
    normalized_optional_text(Some(value))
}

pub(crate) fn assert_library_can_be_deleted(library: &Value) -> Result<(), String> {
    let library_id = library_id(library)?;
    let is_default = library
        .get("isDefault")
        .and_then(Value::as_bool)
        .unwrap_or(false);

    if is_default || library_id == DEFAULT_LIBRARY_ID {
        Err(String::from("Default library cannot be deleted."))
    } else {
        Ok(())
    }
}

pub(crate) fn reorder_libraries(
    libraries: Vec<Value>,
    ordered_library_ids: &[String],
) -> Result<Vec<Value>, String> {
    let mut library_map = HashMap::new();

    for library in libraries {
        let id = library_id(&library)?;
        library_map.insert(id, library);
    }

    let mut reordered_libraries = Vec::new();
    let mut seen_library_ids = HashSet::new();

    for library_id in ordered_library_ids {
        if seen_library_ids.contains(library_id) {
            continue;
        }

        if let Some(library) = library_map.remove(library_id) {
            seen_library_ids.insert(library_id.clone());
            reordered_libraries.push(library);
        }
    }

    let mut remaining_libraries = library_map.into_values().collect::<Vec<_>>();
    remaining_libraries.sort_by(compare_ordered_entities);
    reordered_libraries.extend(remaining_libraries);

    apply_order_and_updated_at(reordered_libraries, "desktop library")
}

pub(crate) fn normalize_library_track_orders(tracks: Vec<Value>) -> Result<Vec<Value>, String> {
    tracks
        .into_iter()
        .enumerate()
        .map(|(index, track)| {
            let mut next_track = track;
            set_u64_field(&mut next_track, "libraryOrder", index as u64)?;
            Ok(next_track)
        })
        .collect()
}

pub(crate) fn reorder_library_playlists(
    playlists: Vec<Value>,
    ordered_user_playlist_ids: &[String],
) -> Result<Vec<Value>, String> {
    let mut system_playlists = Vec::new();
    let mut user_playlists = Vec::new();

    for playlist in playlists {
        if playlist.get("kind").and_then(Value::as_str) == Some("system") {
            system_playlists.push(playlist);
        } else {
            user_playlists.push(playlist);
        }
    }

    let mut user_playlist_map = HashMap::new();
    for playlist in user_playlists {
        let playlist_id = required_text_field(&playlist, "id", "desktop playlist")?;
        user_playlist_map.insert(playlist_id, playlist);
    }

    let mut reordered_user_playlists = Vec::new();
    let mut seen_playlist_ids = HashSet::new();

    for playlist_id in ordered_user_playlist_ids {
        if seen_playlist_ids.contains(playlist_id) {
            continue;
        }

        if let Some(playlist) = user_playlist_map.remove(playlist_id) {
            seen_playlist_ids.insert(playlist_id.clone());
            reordered_user_playlists.push(playlist);
        }
    }

    let mut remaining_user_playlists = user_playlist_map.into_values().collect::<Vec<_>>();
    remaining_user_playlists.sort_by(compare_ordered_entities);
    reordered_user_playlists.extend(remaining_user_playlists);

    let mut reordered_playlists = system_playlists;
    reordered_playlists.extend(reordered_user_playlists);

    apply_order_and_updated_at(reordered_playlists, "desktop playlist")
}

pub(crate) fn normalize_playlist_relation_orders(
    relations: Vec<Value>,
) -> Result<Vec<Value>, String> {
    relations
        .into_iter()
        .enumerate()
        .map(|(index, relation)| {
            let mut next_relation = relation;
            set_u64_field(&mut next_relation, "order", index as u64)?;
            Ok(next_relation)
        })
        .collect()
}

pub(crate) fn reorder_playlist_relation_values(
    relations: Vec<Value>,
    ordered_track_ids: &[String],
) -> Result<Vec<Value>, String> {
    let mut relation_map = HashMap::new();

    for relation in relations.iter() {
        let track_id = required_text_field(relation, "trackId", "desktop playlist-track relation")?;
        relation_map.insert(track_id, relation.clone());
    }

    let mut reordered_relations = Vec::new();
    let mut seen_track_ids = HashSet::new();

    for track_id in ordered_track_ids {
        if seen_track_ids.contains(track_id) {
            continue;
        }

        if let Some(relation) = relation_map.get(track_id) {
            seen_track_ids.insert(track_id.clone());
            reordered_relations.push(relation.clone());
        }
    }

    for relation in relations {
        let track_id = relation_track_id(&relation).unwrap_or_default();
        if seen_track_ids.contains(&track_id) {
            continue;
        }

        reordered_relations.push(relation);
    }

    normalize_playlist_relation_orders(reordered_relations)
}

pub(crate) fn apply_order_and_updated_at(
    records: Vec<Value>,
    entity_label: &str,
) -> Result<Vec<Value>, String> {
    let updated_at = current_iso_timestamp();

    records
        .into_iter()
        .enumerate()
        .map(|(index, record)| {
            let mut next_record = record;
            set_u64_field(&mut next_record, "order", index as u64)?;
            set_string_field(
                &mut next_record,
                "updatedAt",
                updated_at.clone(),
                entity_label,
            )?;
            Ok(next_record)
        })
        .collect()
}

pub(crate) fn compare_ordered_entities(left: &Value, right: &Value) -> std::cmp::Ordering {
    let left_order = left
        .get("order")
        .and_then(Value::as_i64)
        .unwrap_or_default();
    let right_order = right
        .get("order")
        .and_then(Value::as_i64)
        .unwrap_or_default();

    left_order
        .cmp(&right_order)
        .then_with(|| {
            String::from(
                left.get("createdAt")
                    .and_then(Value::as_str)
                    .unwrap_or_default(),
            )
            .cmp(&String::from(
                right
                    .get("createdAt")
                    .and_then(Value::as_str)
                    .unwrap_or_default(),
            ))
        })
        .then_with(|| {
            String::from(left.get("id").and_then(Value::as_str).unwrap_or_default()).cmp(
                &String::from(right.get("id").and_then(Value::as_str).unwrap_or_default()),
            )
        })
}

pub(crate) fn relation_track_id(relation: &Value) -> Option<String> {
    relation
        .get("trackId")
        .and_then(Value::as_str)
        .map(String::from)
}

pub(crate) fn relation_id(relation: &Value) -> Option<String> {
    relation.get("id").and_then(Value::as_str).map(String::from)
}

pub(crate) fn library_id(library: &Value) -> Result<String, String> {
    required_text_field(library, "id", "desktop library")
}

pub(crate) fn load_relation_ids_for_library_delete(
    transaction: &Transaction<'_>,
    playlist_ids: &[String],
    track_ids: &[String],
) -> Result<Vec<String>, String> {
    if playlist_ids.is_empty() && track_ids.is_empty() {
        return Ok(Vec::new());
    }

    let mut conditions = Vec::new();
    let mut parameters = Vec::new();

    if !playlist_ids.is_empty() {
        conditions.push(format!(
            "playlist_id IN ({})",
            vec!["?"; playlist_ids.len()].join(", ")
        ));
        parameters.extend(playlist_ids.iter().cloned());
    }

    if !track_ids.is_empty() {
        conditions.push(format!(
            "track_id IN ({})",
            vec!["?"; track_ids.len()].join(", ")
        ));
        parameters.extend(track_ids.iter().cloned());
    }

    let sql = format!(
        "SELECT id FROM playlist_track_relations WHERE {}",
        conditions.join(" OR ")
    );
    let mut statement = transaction
        .prepare(&sql)
        .map_err(|error| format!("Failed to prepare desktop library relation query: {error}"))?;
    let rows = statement
        .query_map(params_from_iter(parameters.iter()), |row| {
            row.get::<_, String>(0)
        })
        .map_err(|error| format!("Failed to query desktop library relations: {error}"))?;

    let mut relation_ids = Vec::new();

    for row in rows {
        relation_ids.push(
            row.map_err(|error| format!("Failed to read desktop library relation row: {error}"))?,
        );
    }

    Ok(relation_ids)
}

pub(crate) fn load_relation_ids_for_track_delete(
    transaction: &Transaction<'_>,
    track_id: &str,
) -> Result<Vec<String>, String> {
    let mut statement = transaction
        .prepare("SELECT id FROM playlist_track_relations WHERE track_id = ?1")
        .map_err(|error| format!("Failed to prepare desktop track relation query: {error}"))?;
    let rows = statement
        .query_map([track_id], |row| row.get::<_, String>(0))
        .map_err(|error| format!("Failed to query desktop track relations: {error}"))?;

    let mut relation_ids = Vec::new();

    for row in rows {
        relation_ids.push(
            row.map_err(|error| format!("Failed to read desktop track relation row: {error}"))?,
        );
    }

    Ok(relation_ids)
}
