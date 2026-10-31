use crate::{
    db_helpers::{
        current_iso_timestamp, load_json_from_connection, load_track_id_query,
        optional_number_as_f64, optional_text_field, save_json_to_connection,
    },
    desktop_types::SessionStateSnapshot,
};
use rusqlite::Connection;
use serde_json::Value;
use std::collections::HashSet;
use uuid::Uuid;

pub(crate) const SESSION_STATE_KEY: &str = "session";
pub(crate) const SESSION_PLAYBACK_STATUS_IDLE: &str = "idle";
pub(crate) const SESSION_PLAYBACK_STATUS_PAUSED: &str = "paused";
pub(crate) const SESSION_PLAYBACK_STATUS_PLAYING: &str = "playing";

pub(crate) fn load_session_snapshot_from_connection(
    connection: &Connection,
) -> Result<SessionStateSnapshot, String> {
    let existing_session = load_json_from_connection(connection, SESSION_STATE_KEY)?;
    let now = current_iso_timestamp();
    let id = existing_session
        .as_ref()
        .and_then(|session| optional_text_field(session, "id"))
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(create_session_id);
    let started_at = existing_session
        .as_ref()
        .and_then(|session| optional_text_field(session, "startedAt"))
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| now.clone());
    let last_interacted_at = existing_session
        .as_ref()
        .and_then(|session| optional_text_field(session, "lastInteractedAt"))
        .filter(|value| !value.trim().is_empty())
        .unwrap_or_else(|| now.clone());
    let queue_track_ids = existing_session
        .as_ref()
        .and_then(|session| session.get("queueTrackIds"))
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(Value::as_str)
                .map(String::from)
                .collect::<Vec<_>>()
        })
        .unwrap_or_default();
    let normalized_queue_track_ids = normalize_session_queue_track_ids(
        &queue_track_ids,
        &queue_track_ids.iter().cloned().collect(),
    );
    let current_track_id = existing_session
        .as_ref()
        .and_then(|session| optional_text_field(session, "currentTrackId"))
        .filter(|track_id| {
            normalized_queue_track_ids
                .iter()
                .any(|queued_track_id| queued_track_id == track_id)
        });
    let mut playback_status = existing_session
        .as_ref()
        .and_then(|session| optional_text_field(session, "playbackStatus"))
        .map(|value| normalize_session_playback_status(&value))
        .unwrap_or_else(|| String::from(SESSION_PLAYBACK_STATUS_IDLE));
    let mut current_time = existing_session
        .as_ref()
        .and_then(|session| optional_number_as_f64(session, "currentTime"))
        .map(clamp_session_playback_time)
        .unwrap_or_default();
    let mut duration = existing_session
        .as_ref()
        .and_then(|session| optional_number_as_f64(session, "duration"))
        .map(clamp_session_playback_time)
        .unwrap_or_default();

    if current_track_id.is_none() {
        playback_status = String::from(SESSION_PLAYBACK_STATUS_IDLE);
        current_time = 0.0;
        duration = 0.0;
    }

    Ok(SessionStateSnapshot {
        id,
        started_at,
        last_interacted_at,
        current_track_id,
        queue_track_ids: normalized_queue_track_ids,
        queue_revision: existing_session
            .as_ref()
            .and_then(|s| s.get("queueRevision"))
            .and_then(Value::as_u64)
            .unwrap_or(0),
        queue_initialized: existing_session
            .as_ref()
            .and_then(|s| s.get("queueInitialized"))
            .and_then(Value::as_bool)
            .unwrap_or(false),
        playback_status,
        current_time,
        duration,
    })
}

pub(crate) fn save_session_snapshot_to_connection(
    connection: &Connection,
    session: &mut SessionStateSnapshot,
) -> Result<(), String> {
    let mut payload = serde_json::to_value(&*session)
        .map_err(|error| format!("Failed to encode desktop playback session snapshot: {error}"))?;
    save_session_value_to_connection(connection, &mut payload)?;
    session.queue_revision = payload["queueRevision"].as_u64().unwrap_or(0);
    Ok(())
}

/// Also used by legacy session restoration: a restored older payload must not
/// roll the queue revision backwards and make delayed IPC replies look fresh.
pub(crate) fn save_session_value_to_connection(
    connection: &Connection,
    payload: &mut Value,
) -> Result<(), String> {
    if !payload.is_object() {
        return Err("Playback session must be an object.".into());
    }
    let previous = load_json_from_connection(connection, SESSION_STATE_KEY)?;
    let revision = previous
        .as_ref()
        .and_then(|s| s.get("queueRevision"))
        .and_then(Value::as_u64)
        .unwrap_or(0);
    let changed =
        previous.as_ref().and_then(|s| s.get("queueTrackIds")) != payload.get("queueTrackIds");
    payload["queueRevision"] = Value::from(if changed {
        revision
            .checked_add(1)
            .ok_or("Playback queue revision exhausted.")?
    } else {
        revision
    });
    save_json_to_connection(connection, SESSION_STATE_KEY, payload)
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum QueueEdit {
    Next,
    Append,
    Remove,
}

impl QueueEdit {
    pub fn parse(value: &str) -> Result<Self, String> {
        match value {
            "next" => Ok(Self::Next),
            "append" => Ok(Self::Append),
            "remove" => Ok(Self::Remove),
            _ => Err("Unknown playback queue operation.".into()),
        }
    }
}

/// Queue-only editing. The current item is the continuation anchor, including
/// while paused; never remove/move it or change its persisted playback fields.
pub(crate) fn edit_queue(
    session: &mut SessionStateSnapshot,
    action: QueueEdit,
    ids: &[String],
) -> Vec<String> {
    let valid = ids.iter().map(|id| id.trim().to_owned()).collect();
    let mut edited = normalize_session_queue_track_ids(ids, &valid);
    let queued: HashSet<_> = session.queue_track_ids.iter().cloned().collect();
    edited.retain(|id| {
        Some(id) != session.current_track_id.as_ref()
            && (action != QueueEdit::Remove || queued.contains(id))
    });
    let moving: HashSet<_> = edited.iter().collect();
    session.queue_track_ids.retain(|id| !moving.contains(id));
    if action != QueueEdit::Remove {
        let index = if action == QueueEdit::Next {
            session
                .current_track_id
                .as_ref()
                .and_then(|id| session.queue_track_ids.iter().position(|q| q == id))
                .map(|i| i + 1)
                .unwrap_or(0)
        } else {
            session.queue_track_ids.len()
        };
        session
            .queue_track_ids
            .splice(index..index, edited.iter().cloned());
    }
    session.queue_initialized = true;
    edited
}

pub(crate) fn touch_session_snapshot(session: &mut SessionStateSnapshot) {
    session.last_interacted_at = current_iso_timestamp();
}

pub(crate) fn reset_session_playback_state(session: &mut SessionStateSnapshot) {
    apply_session_playback_state(session, SESSION_PLAYBACK_STATUS_IDLE, 0.0, 0.0);
}

pub(crate) fn apply_session_playback_state(
    session: &mut SessionStateSnapshot,
    status: &str,
    current_time: f64,
    duration: f64,
) {
    session.playback_status = normalize_session_playback_status(status);
    session.current_time = clamp_session_playback_time(current_time);
    session.duration = clamp_session_playback_time(duration);

    if session.current_track_id.is_none() {
        session.playback_status = String::from(SESSION_PLAYBACK_STATUS_IDLE);
        session.current_time = 0.0;
        session.duration = 0.0;
    }
}

pub(crate) fn normalize_session_playback_status(status: &str) -> String {
    match status {
        SESSION_PLAYBACK_STATUS_PAUSED => String::from(SESSION_PLAYBACK_STATUS_PAUSED),
        SESSION_PLAYBACK_STATUS_PLAYING => String::from(SESSION_PLAYBACK_STATUS_PLAYING),
        _ => String::from(SESSION_PLAYBACK_STATUS_IDLE),
    }
}

pub(crate) fn clamp_session_playback_time(value: f64) -> f64 {
    if value.is_finite() && value >= 0.0 {
        value
    } else {
        0.0
    }
}

pub(crate) fn load_all_catalog_track_ids(connection: &Connection) -> Result<Vec<String>, String> {
    load_track_id_query(
        connection,
        "SELECT id FROM tracks ORDER BY library_id ASC, library_order ASC, imported_at_text ASC, id ASC",
        [],
        "desktop catalog track order",
    )
}

pub(crate) fn normalize_session_queue_track_ids(
    track_ids: &[String],
    valid_track_ids: &HashSet<String>,
) -> Vec<String> {
    let mut seen_track_ids = HashSet::new();
    let mut normalized_track_ids = Vec::new();

    for track_id in track_ids
        .iter()
        .map(String::as_str)
        .map(str::trim)
        .filter(|track_id| !track_id.is_empty())
    {
        if !valid_track_ids.contains(track_id) || seen_track_ids.contains(track_id) {
            continue;
        }

        seen_track_ids.insert(String::from(track_id));
        normalized_track_ids.push(String::from(track_id));
    }

    normalized_track_ids
}

pub(crate) fn build_queue_from_catalog(
    persisted_queue_track_ids: &[String],
    available_track_ids: &[String],
) -> Vec<String> {
    let available_track_id_set = available_track_ids.iter().cloned().collect::<HashSet<_>>();
    let visible_queue_track_ids =
        normalize_session_queue_track_ids(persisted_queue_track_ids, &available_track_id_set);

    if persisted_queue_track_ids.is_empty() {
        return available_track_ids.to_vec();
    }

    // A saved playlist/filtered queue must not grow into the whole library on
    // restart or catalog synchronization. Only prune tracks that disappeared.
    visible_queue_track_ids
}

pub(crate) fn resolve_adjacent_session_track_id(
    session: &SessionStateSnapshot,
    step: isize,
) -> Option<String> {
    if session.queue_track_ids.is_empty() {
        return None;
    }

    let current_index = session.current_track_id.as_ref().and_then(|track_id| {
        session
            .queue_track_ids
            .iter()
            .position(|queued_track_id| queued_track_id == track_id)
    });
    let next_index = match current_index {
        Some(index) => {
            ((index as isize + step).rem_euclid(session.queue_track_ids.len() as isize)) as usize
        }
        None => 0,
    };

    session.queue_track_ids.get(next_index).cloned()
}

fn create_session_id() -> String {
    format!("session-{}", Uuid::new_v4())
}
