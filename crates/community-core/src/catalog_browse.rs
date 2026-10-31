//! Lightweight browse metadata kept in sync with catalog writes, without loading artwork payloads.
use rusqlite::{Connection, OptionalExtension};

/// Older metadata editors patch the display artist only. Reconcile unchanged old credits
/// in that case, while preserving a caller's explicit structured-credit update.
pub fn reconcile_artist_edit<'a>(
    connection: &Connection,
    record: &'a serde_json::Value,
) -> Result<std::borrow::Cow<'a, serde_json::Value>, String> {
    use serde_json::{json, Value};
    use std::borrow::Cow;
    let Some(credits) = record.get("artists").and_then(Value::as_array) else {
        return Ok(Cow::Borrowed(record));
    };
    let display = record.get("artist").and_then(Value::as_str).unwrap_or("");
    if credits
        .iter()
        .filter_map(Value::as_str)
        .collect::<Vec<_>>()
        .join(", ")
        == display
    {
        return Ok(Cow::Borrowed(record));
    }
    let previous: Option<(String, Option<String>)> = connection
        .query_row(
            "SELECT artist,json_extract(payload_json,'$.artists') FROM tracks WHERE id=?1",
            [record.get("id").and_then(Value::as_str).unwrap_or("")],
            |row| Ok((row.get(0)?, row.get(1)?)),
        )
        .optional()
        .map_err(|error| format!("Failed to reconcile artist edit: {error}"))?;
    if let Some((artist, Some(previous_credits))) = previous {
        if artist != display
            && serde_json::from_str::<Value>(&previous_credits)
                .ok()
                .as_ref()
                == record.get("artists")
        {
            let mut updated = record.clone();
            updated["artists"] = json!(artist_names([display]));
            return Ok(Cow::Owned(updated));
        }
    }
    Ok(Cow::Borrowed(record))
}

pub fn initialize(connection: &Connection) -> Result<(), String> {
    let exists: bool = connection.query_row(
        "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='track_browse_metadata')",
        [], |row| row.get(0),
    ).map_err(|e| format!("Failed to inspect browse metadata: {e}"))?;
    if exists {
        return Ok(());
    }
    // A savepoint also works when schema initialization is called inside a migration transaction.
    connection.execute_batch("SAVEPOINT browse_metadata;
        CREATE TABLE track_browse_metadata (
            track_id TEXT PRIMARY KEY NOT NULL,
            artists_json TEXT NOT NULL,
            added_at_ms INTEGER,
            modified_at_ms INTEGER
        );
        CREATE TRIGGER insert_track_browse_metadata AFTER INSERT ON tracks WHEN json_valid(NEW.payload_json) BEGIN
            INSERT INTO track_browse_metadata VALUES (
                NEW.id,
                CASE WHEN json_type(NEW.payload_json,'$.artists')='array'
                    AND json_array_length(NEW.payload_json,'$.artists')>0
                    THEN json_extract(NEW.payload_json,'$.artists')
                    ELSE json_array(COALESCE(NULLIF(trim(NEW.artist),''),NULLIF(trim(NEW.album_artist),''),'未知艺术家')) END,
                CAST((julianday(NEW.imported_at_text)-2440587.5)*86400000 AS INTEGER),
                json_extract(NEW.payload_json,'$.modifiedAtMs'))
            ON CONFLICT(track_id) DO UPDATE SET artists_json=excluded.artists_json,
                added_at_ms=excluded.added_at_ms,modified_at_ms=excluded.modified_at_ms;
        END;
        CREATE TRIGGER update_track_browse_metadata AFTER UPDATE ON tracks WHEN json_valid(NEW.payload_json) BEGIN
            INSERT INTO track_browse_metadata VALUES (
                NEW.id,
                CASE WHEN json_type(NEW.payload_json,'$.artists')='array'
                    AND json_array_length(NEW.payload_json,'$.artists')>0
                    THEN json_extract(NEW.payload_json,'$.artists')
                    ELSE json_array(COALESCE(NULLIF(trim(NEW.artist),''),NULLIF(trim(NEW.album_artist),''),'未知艺术家')) END,
                CAST((julianday(NEW.imported_at_text)-2440587.5)*86400000 AS INTEGER),
                json_extract(NEW.payload_json,'$.modifiedAtMs'))
            ON CONFLICT(track_id) DO UPDATE SET artists_json=excluded.artists_json,
                added_at_ms=excluded.added_at_ms,modified_at_ms=excluded.modified_at_ms;
        END;
        CREATE TRIGGER delete_track_browse_metadata AFTER DELETE ON tracks BEGIN
            DELETE FROM track_browse_metadata WHERE track_id=OLD.id;
        END;
        INSERT INTO track_browse_metadata
            SELECT id,
                CASE WHEN json_type(payload_json,'$.artists')='array' AND json_array_length(payload_json,'$.artists')>0
                    THEN json_extract(payload_json,'$.artists')
                    ELSE json_array(COALESCE(NULLIF(trim(artist),''),NULLIF(trim(album_artist),''),'未知艺术家')) END,
                CAST((julianday(imported_at_text)-2440587.5)*86400000 AS INTEGER),
                json_extract(payload_json,'$.modifiedAtMs') FROM tracks WHERE json_valid(payload_json);
        RELEASE browse_metadata;")
        .map_err(|error| {
            let _ = connection.execute_batch("ROLLBACK TO browse_metadata; RELEASE browse_metadata;");
            format!("Failed to initialize browse metadata: {error}")
        })
}

/// Repeated tags, NUL (ID3v2.4) and semicolon lists are explicit credits.
/// Commas, slashes and ampersands can be part of a band's name and remain intact.
pub fn artist_names<'a>(values: impl IntoIterator<Item = &'a str>) -> Vec<String> {
    let mut names = Vec::new();
    for name in values
        .into_iter()
        .flat_map(|value| value.split(['\0', ';']))
        .map(str::trim)
        .filter(|v| !v.is_empty())
    {
        if !names
            .iter()
            .any(|existing: &String| existing.eq_ignore_ascii_case(name))
        {
            names.push(name.to_string());
        }
    }
    names
}

pub fn modified_at_ms(path: &std::path::Path) -> Option<i64> {
    std::fs::metadata(path)
        .ok()?
        .modified()
        .ok()?
        .duration_since(std::time::UNIX_EPOCH)
        .ok()?
        .as_millis()
        .try_into()
        .ok()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn artist_credits_keep_band_names_and_deduplicate() {
        assert_eq!(
            artist_names(["AC/DC", "Earth, Wind & Fire", "Alice; Bob\0alice"]),
            ["AC/DC", "Earth, Wind & Fire", "Alice", "Bob"]
        );
    }
}
