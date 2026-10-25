//! Low-level SQLite and JSON utility helpers shared across desktop_state modules.
//!
//! This module contains stateless helper functions for:
//! - Reading and writing JSON values to the `app_state` key-value table
//! - Generic SQL payload queries (SELECT … payload_json)
//! - Deleting records by id lists
//! - Accessing typed fields from `serde_json::Value` objects
//! - Time utilities (Unix timestamp, ISO-8601 timestamp, `Instant` elapsed)
//! - Numeric conversion helpers used for SQLite column mapping

use rusqlite::{params, params_from_iter, Connection, OptionalExtension, Transaction};
use serde_json::Value;
use std::time::{Instant, SystemTime, UNIX_EPOCH};

// ---------------------------------------------------------------------------
// SQL payload query helpers
// ---------------------------------------------------------------------------

/// Execute `sql` and deserialize every row's first column as a JSON `Value`.
///
/// The query must return a single TEXT column that contains a valid JSON object
/// per row.  `entity_label` is used only for error message formatting.
pub(crate) fn load_payloads<P>(
    connection: &Connection,
    sql: &str,
    params: P,
    entity_label: &str,
) -> Result<Vec<Value>, String>
where
    P: rusqlite::Params,
{
    let mut statement = connection
        .prepare(sql)
        .map_err(|error| format!("Failed to prepare {entity_label} query: {error}"))?;
    let rows = statement
        .query_map(params, |row| row.get::<_, String>(0))
        .map_err(|error| format!("Failed to query {entity_label}: {error}"))?;

    let mut records = Vec::new();

    for row in rows {
        let payload = row.map_err(|error| format!("Failed to read {entity_label} row: {error}"))?;
        records.push(deserialize_value(&payload, entity_label)?);
    }

    Ok(records)
}

/// Execute `sql` and return every row's first column as a `String`.
///
/// Useful for `SELECT id FROM …` style queries.
pub(crate) fn load_track_id_query<P>(
    connection: &Connection,
    sql: &str,
    params: P,
    label: &str,
) -> Result<Vec<String>, String>
where
    P: rusqlite::Params,
{
    let mut statement = connection
        .prepare(sql)
        .map_err(|error| format!("Failed to prepare {label} query: {error}"))?;
    let rows = statement
        .query_map(params, |row| row.get::<_, String>(0))
        .map_err(|error| format!("Failed to query {label}: {error}"))?;

    let mut track_ids = Vec::new();

    for row in rows {
        track_ids.push(row.map_err(|error| format!("Failed to read {label} row: {error}"))?);
    }

    Ok(track_ids)
}

// ---------------------------------------------------------------------------
// JSON serialization helpers
// ---------------------------------------------------------------------------

pub(crate) fn deserialize_value(payload: &str, entity_label: &str) -> Result<Value, String> {
    serde_json::from_str::<Value>(payload)
        .map_err(|error| format!("Failed to decode {entity_label}: {error}"))
}

pub(crate) fn serialize_value(value: &Value, entity_label: &str) -> Result<String, String> {
    serde_json::to_string(value)
        .map_err(|error| format!("Failed to encode {entity_label}: {error}"))
}

// ---------------------------------------------------------------------------
// app_state key-value persistence
// ---------------------------------------------------------------------------

/// Load a JSON value from the `app_state` table by key.  Returns `None` when
/// the key does not exist.
pub(crate) fn load_json_from_connection(
    connection: &Connection,
    key: &str,
) -> Result<Option<Value>, String> {
    let serialized = connection
        .query_row(
            "SELECT value_json FROM app_state WHERE key = ?1",
            [key],
            |row| row.get::<_, String>(0),
        )
        .optional()
        .map_err(|error| format!("Failed to read desktop app state '{key}': {error}"))?;

    serialized
        .map(|raw| {
            serde_json::from_str::<Value>(&raw)
                .map_err(|error| format!("Failed to decode desktop app state '{key}': {error}"))
        })
        .transpose()
}

/// Upsert a JSON value into the `app_state` table.
pub(crate) fn save_json_to_connection(
    connection: &Connection,
    key: &str,
    value: &Value,
) -> Result<(), String> {
    let serialized = serde_json::to_string(value)
        .map_err(|error| format!("Failed to encode desktop app state '{key}': {error}"))?;
    let updated_at = current_unix_timestamp();

    connection
        .execute(
            "INSERT INTO app_state (key, value_json, updated_at)
             VALUES (?1, ?2, ?3)
             ON CONFLICT(key) DO UPDATE SET
               value_json = excluded.value_json,
               updated_at = excluded.updated_at",
            params![key, serialized, updated_at],
        )
        .map_err(|error| format!("Failed to save desktop app state '{key}': {error}"))?;

    Ok(())
}

// ---------------------------------------------------------------------------
// Record deletion helpers
// ---------------------------------------------------------------------------

/// Delete rows from `table_name` whose `id` column is in `ids`.
pub(crate) fn delete_records(
    connection: &Connection,
    table_name: &str,
    ids: &[String],
    entity_label: &str,
) -> Result<(), String> {
    if ids.is_empty() {
        return Ok(());
    }

    let placeholders = vec!["?"; ids.len()].join(", ");
    let statement = format!("DELETE FROM {table_name} WHERE id IN ({placeholders})");

    connection
        .execute(&statement, params_from_iter(ids.iter()))
        .map_err(|error| format!("Failed to delete {entity_label}: {error}"))?;

    Ok(())
}

/// Same as [`delete_records`] but operates inside an open transaction.
pub(crate) fn delete_records_in_transaction(
    transaction: &Transaction<'_>,
    table_name: &str,
    ids: &[String],
    entity_label: &str,
) -> Result<(), String> {
    if ids.is_empty() {
        return Ok(());
    }

    let placeholders = vec!["?"; ids.len()].join(", ");
    let statement = format!("DELETE FROM {table_name} WHERE id IN ({placeholders})");

    transaction
        .execute(&statement, params_from_iter(ids.iter()))
        .map_err(|error| format!("Failed to delete {entity_label}: {error}"))?;

    Ok(())
}

// ---------------------------------------------------------------------------
// JSON field access helpers
// ---------------------------------------------------------------------------

/// Extract a required string field from a JSON object.  Returns an error when
/// the field is absent or not a string.
pub(crate) fn required_text_field(
    record: &Value,
    field: &str,
    entity_label: &str,
) -> Result<String, String> {
    record
        .get(field)
        .and_then(Value::as_str)
        .map(|value| value.to_string())
        .ok_or_else(|| format!("Missing string field '{field}' on {entity_label}."))
}

/// Extract an optional string field from a JSON object.
pub(crate) fn optional_text_field(record: &Value, field: &str) -> Option<String> {
    record
        .get(field)
        .and_then(Value::as_str)
        .map(|value| value.to_string())
}

/// Extract an optional number field as `f64`, accepting integer JSON values.
pub(crate) fn optional_number_as_f64(record: &Value, field: &str) -> Option<f64> {
    record.get(field).and_then(|value| {
        value
            .as_f64()
            .or_else(|| value.as_i64().map(|number| number as f64))
            .or_else(|| value.as_u64().map(|number| number as f64))
    })
}

/// Extract an optional number field as `u64`, accepting signed JSON integers
/// that fit into the unsigned range.
pub(crate) fn optional_number_as_u64(record: &Value, field: &str) -> Option<u64> {
    record.get(field).and_then(|value| {
        value
            .as_u64()
            .or_else(|| value.as_i64().and_then(|number| u64::try_from(number).ok()))
    })
}

/// Extract a required integer field as `i64`.
pub(crate) fn required_integer_field(
    record: &Value,
    field: &str,
    entity_label: &str,
) -> Result<i64, String> {
    record
        .get(field)
        .and_then(|value| {
            value.as_i64().or_else(|| {
                value
                    .as_u64()
                    .and_then(|unsigned| i64::try_from(unsigned).ok())
            })
        })
        .ok_or_else(|| format!("Missing integer field '{field}' on {entity_label}."))
}

/// Extract a required boolean field.
pub(crate) fn required_boolean_field(
    record: &Value,
    field: &str,
    entity_label: &str,
) -> Result<bool, String> {
    record
        .get(field)
        .and_then(Value::as_bool)
        .ok_or_else(|| format!("Missing boolean field '{field}' on {entity_label}."))
}

/// Overwrite a string field on a JSON object value.
pub(crate) fn set_string_field(
    record: &mut Value,
    field: &str,
    value: String,
    entity_label: &str,
) -> Result<(), String> {
    match record {
        Value::Object(map) => {
            map.insert(String::from(field), Value::String(value));
            Ok(())
        }
        _ => Err(format!("Cannot write field '{field}' on {entity_label}.")),
    }
}

/// Overwrite a numeric (`u64`) field on a JSON object value.
pub(crate) fn set_u64_field(record: &mut Value, field: &str, value: u64) -> Result<(), String> {
    match record {
        Value::Object(map) => {
            map.insert(String::from(field), Value::from(value));
            Ok(())
        }
        _ => Err(format!(
            "Cannot write numeric field '{field}' on desktop record."
        )),
    }
}

/// Overwrite a boolean field on a JSON object value.
pub(crate) fn set_bool_field(
    record: &mut Value,
    field: &str,
    value: bool,
    entity_label: &str,
) -> Result<(), String> {
    match record {
        Value::Object(map) => {
            map.insert(String::from(field), Value::Bool(value));
            Ok(())
        }
        _ => Err(format!("Cannot write field '{field}' on {entity_label}.")),
    }
}

/// Recursively merge `patch` into `target` (JSON merge-patch semantics for
/// objects, replacement semantics for all other types).
pub(crate) fn merge_json_values(target: &mut Value, patch: &Value) {
    match (target, patch) {
        (Value::Object(target_map), Value::Object(patch_map)) => {
            for (key, value) in patch_map {
                if let Some(existing_value) = target_map.get_mut(key) {
                    if existing_value.is_object() && value.is_object() {
                        merge_json_values(existing_value, value);
                        continue;
                    }
                }

                target_map.insert(key.clone(), value.clone());
            }
        }
        (target_value, patch_value) => {
            *target_value = patch_value.clone();
        }
    }
}

// ---------------------------------------------------------------------------
// Time utilities
// ---------------------------------------------------------------------------

/// Convert an `Instant` to elapsed milliseconds since it was created, clamped
/// to `u64::MAX` on overflow.
pub(crate) fn elapsed_ms(start: Instant) -> u64 {
    u64::try_from(start.elapsed().as_millis()).unwrap_or(u64::MAX)
}

/// Saturating cast of `u64` → `i64`, clamping at `i64::MAX`.
pub(crate) fn saturating_u64_to_i64(value: u64) -> i64 {
    i64::try_from(value).unwrap_or(i64::MAX)
}

/// Cast `i64` → `u64`, treating negative values as zero.
pub(crate) fn nonnegative_i64_to_u64(value: i64) -> u64 {
    u64::try_from(value.max(0)).unwrap_or_default()
}

/// Current Unix timestamp in whole seconds.
pub(crate) fn current_unix_timestamp() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_secs() as i64)
        .unwrap_or_default()
}

/// Current time as an RFC-3339 string (UTC).
pub(crate) fn current_iso_timestamp() -> String {
    chrono::Utc::now().to_rfc3339()
}
