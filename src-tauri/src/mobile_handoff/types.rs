use serde::{Deserialize, Serialize};

pub(crate) const MOBILE_HANDOFF_PROTOCOL_VERSION: &str = "mobile-handoff-v1";
pub(crate) const MOBILE_HANDOFF_EVENT_SCHEMA_VERSION: &str = "mobile-handoff-events-v1";
pub(crate) const MOBILE_HANDOFF_STAGE: &str = "instrumentation-foundation";

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MobileHandoffCapabilities {
    pub protocol_version: &'static str,
    pub event_schema_version: &'static str,
    pub implementation_stage: &'static str,
    pub backend_owned: bool,
    pub supported_device_platforms: Vec<&'static str>,
    pub discovery_methods: Vec<&'static str>,
    pub control_transports: Vec<&'static str>,
    pub media_transports: Vec<&'static str>,
    pub can_record_events: bool,
    pub can_pair_devices: bool,
    pub can_resume_playback: bool,
    pub can_transfer_media: bool,
    pub requires_telemetry_consent: bool,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MobileHandoffEventRequest {
    pub event: String,
    pub platform: Option<String>,
    pub direction: Option<String>,
    pub transport: Option<String>,
    pub phase: Option<String>,
    pub outcome: Option<String>,
    pub duration_ms: Option<i64>,
    pub error_code: Option<String>,
    pub session_id: Option<String>,
    pub device_id: Option<String>,
    pub track_matched: Option<bool>,
    pub queue_size: Option<i64>,
    pub position_drift_ms: Option<i64>,
}

#[derive(Debug, Clone, Default, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MobileHandoffEventAttributes {
    pub platform: Option<String>,
    pub direction: Option<String>,
    pub transport: Option<String>,
    pub phase: Option<String>,
    pub outcome: Option<String>,
    pub duration_ms: Option<u64>,
    pub error_code: Option<String>,
    pub session_key: Option<String>,
    pub device_key: Option<String>,
    pub track_matched: Option<bool>,
    pub queue_size: Option<u64>,
    pub position_drift_ms: Option<u64>,
}

#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct MobileHandoffEventRecord {
    pub id: String,
    pub created_at: String,
    pub event: String,
    pub schema_version: &'static str,
    pub attributes: MobileHandoffEventAttributes,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MobileHandoffStateSnapshot {
    pub protocol_version: &'static str,
    pub event_schema_version: &'static str,
    pub implementation_stage: &'static str,
    pub started_at: String,
    pub last_event_at: Option<String>,
    pub recent_event_count: usize,
    pub recent_events: Vec<MobileHandoffEventRecord>,
}

pub(crate) fn capabilities() -> MobileHandoffCapabilities {
    MobileHandoffCapabilities {
        protocol_version: MOBILE_HANDOFF_PROTOCOL_VERSION,
        event_schema_version: MOBILE_HANDOFF_EVENT_SCHEMA_VERSION,
        implementation_stage: MOBILE_HANDOFF_STAGE,
        backend_owned: true,
        supported_device_platforms: vec!["android", "harmonyos"],
        discovery_methods: vec!["mdns", "qr"],
        control_transports: vec!["websocket-json", "https-json"],
        media_transports: vec!["http-file", "http-range"],
        can_record_events: true,
        can_pair_devices: false,
        can_resume_playback: false,
        can_transfer_media: false,
        requires_telemetry_consent: true,
    }
}
