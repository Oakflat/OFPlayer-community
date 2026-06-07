use chrono::Utc;
use uuid::Uuid;

use super::types::{
    MobileHandoffEventAttributes, MobileHandoffEventRecord, MobileHandoffEventRequest,
    MOBILE_HANDOFF_EVENT_SCHEMA_VERSION,
};

const ALLOWED_EVENTS: &[&str] = &[
    "handoff.discovery_started",
    "handoff.discovery_succeeded",
    "handoff.discovery_failed",
    "handoff.pairing_started",
    "handoff.pairing_succeeded",
    "handoff.pairing_failed",
    "handoff.offer_created",
    "handoff.offer_accepted",
    "handoff.offer_rejected",
    "handoff.resume_started",
    "handoff.resume_succeeded",
    "handoff.resume_failed",
    "stream.transfer_started",
    "stream.transfer_first_byte",
    "stream.transfer_ready",
    "stream.transfer_failed",
    "stream.playback_first_audio",
    "stream.playback_stall",
    "stream.playback_seek_failed",
];

const ALLOWED_PLATFORMS: &[&str] = &["android", "harmonyos"];
const ALLOWED_DIRECTIONS: &[&str] = &["phone-to-desktop", "desktop-to-phone"];
const ALLOWED_TRANSPORTS: &[&str] = &[
    "mdns",
    "qr",
    "websocket-json",
    "https-json",
    "http-file",
    "http-range",
];
const ALLOWED_PHASES: &[&str] = &[
    "discovery",
    "pairing",
    "offer",
    "resume",
    "transfer",
    "playback",
];
const ALLOWED_OUTCOMES: &[&str] = &[
    "started",
    "succeeded",
    "failed",
    "cancelled",
    "rejected",
    "timed-out",
];
const MAX_DURATION_MS: u64 = 24 * 60 * 60 * 1_000;
const MAX_QUEUE_SIZE: u64 = 10_000;
const MAX_POSITION_DRIFT_MS: u64 = 60 * 60 * 1_000;

pub(crate) fn build_event_record(
    request: MobileHandoffEventRequest,
) -> Result<MobileHandoffEventRecord, String> {
    let event = normalize_event_name(&request.event)?;

    Ok(MobileHandoffEventRecord {
        id: format!("handoff-event-{}", Uuid::new_v4()),
        created_at: Utc::now().to_rfc3339(),
        event,
        schema_version: MOBILE_HANDOFF_EVENT_SCHEMA_VERSION,
        attributes: MobileHandoffEventAttributes {
            platform: normalize_allowed_text(request.platform, ALLOWED_PLATFORMS),
            direction: normalize_allowed_text(request.direction, ALLOWED_DIRECTIONS),
            transport: normalize_allowed_text(request.transport, ALLOWED_TRANSPORTS),
            phase: normalize_allowed_text(request.phase, ALLOWED_PHASES),
            outcome: normalize_allowed_text(request.outcome, ALLOWED_OUTCOMES),
            duration_ms: bounded_u64(request.duration_ms, MAX_DURATION_MS),
            error_code: normalize_slug(request.error_code, 64),
            session_key: fingerprint_identifier(request.session_id),
            device_key: fingerprint_identifier(request.device_id),
            track_matched: request.track_matched,
            queue_size: bounded_u64(request.queue_size, MAX_QUEUE_SIZE),
            position_drift_ms: bounded_u64(request.position_drift_ms, MAX_POSITION_DRIFT_MS),
        },
    })
}

fn normalize_event_name(value: &str) -> Result<String, String> {
    let normalized = value.trim().to_ascii_lowercase();

    if ALLOWED_EVENTS.contains(&normalized.as_str()) {
        Ok(normalized)
    } else {
        Err(String::from(
            "Mobile handoff event is not part of the backend-owned schema.",
        ))
    }
}

fn normalize_allowed_text(value: Option<String>, allowed: &[&str]) -> Option<String> {
    let normalized = value?.trim().to_ascii_lowercase();

    if allowed.contains(&normalized.as_str()) {
        Some(normalized)
    } else {
        None
    }
}

fn normalize_slug(value: Option<String>, max_len: usize) -> Option<String> {
    let normalized = value?.trim().to_ascii_lowercase();

    if normalized.is_empty() || normalized.len() > max_len {
        return None;
    }

    if normalized
        .bytes()
        .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'.' | b'_' | b'-'))
    {
        Some(normalized)
    } else {
        None
    }
}

fn fingerprint_identifier(value: Option<String>) -> Option<String> {
    let normalized = normalize_slug(value, 128)?;
    let mut hash = 0xcbf2_9ce4_8422_2325_u64;

    for byte in normalized.as_bytes() {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
    }

    Some(format!("h{hash:016x}"))
}

fn bounded_u64(value: Option<i64>, max: u64) -> Option<u64> {
    value
        .filter(|value| *value >= 0)
        .map(|value| (value as u64).min(max))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn build_event_record_rejects_unknown_events() {
        let result = build_event_record(MobileHandoffEventRequest {
            event: String::from("handoff.unknown"),
            platform: None,
            direction: None,
            transport: None,
            phase: None,
            outcome: None,
            duration_ms: None,
            error_code: None,
            session_id: None,
            device_id: None,
            track_matched: None,
            queue_size: None,
            position_drift_ms: None,
        });

        assert!(result.is_err());
    }

    #[test]
    fn build_event_record_sanitizes_attributes() {
        let record = build_event_record(MobileHandoffEventRequest {
            event: String::from("STREAM.TRANSFER_READY"),
            platform: Some(String::from("Android")),
            direction: Some(String::from("phone-to-desktop")),
            transport: Some(String::from("http-file")),
            phase: Some(String::from("transfer")),
            outcome: Some(String::from("succeeded")),
            duration_ms: Some(200),
            error_code: Some(String::from("Ignored Raw Text!")),
            session_id: Some(String::from("session-123")),
            device_id: Some(String::from("device-123")),
            track_matched: Some(true),
            queue_size: Some(12),
            position_drift_ms: Some(35),
        })
        .unwrap();

        assert_eq!(record.event, "stream.transfer_ready");
        assert_eq!(record.attributes.platform.as_deref(), Some("android"));
        assert_eq!(record.attributes.error_code, None);
        assert_eq!(record.attributes.track_matched, Some(true));
        assert_eq!(record.attributes.queue_size, Some(12));
        assert!(record.attributes.session_key.is_some());
        assert!(record.attributes.device_key.is_some());
    }
}
