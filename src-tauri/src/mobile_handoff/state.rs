use chrono::Utc;
use std::collections::VecDeque;

use super::types::{
    MobileHandoffEventRecord, MobileHandoffStateSnapshot, MOBILE_HANDOFF_EVENT_SCHEMA_VERSION,
    MOBILE_HANDOFF_PROTOCOL_VERSION, MOBILE_HANDOFF_STAGE,
};

const RECENT_EVENT_LIMIT: usize = 128;

pub struct MobileHandoffState {
    started_at: String,
    last_event_at: Option<String>,
    recent_events: VecDeque<MobileHandoffEventRecord>,
}

impl Default for MobileHandoffState {
    fn default() -> Self {
        Self {
            started_at: Utc::now().to_rfc3339(),
            last_event_at: None,
            recent_events: VecDeque::with_capacity(RECENT_EVENT_LIMIT),
        }
    }
}

impl MobileHandoffState {
    pub(crate) fn record_event(&mut self, record: MobileHandoffEventRecord) {
        self.last_event_at = Some(record.created_at.clone());

        if self.recent_events.len() >= RECENT_EVENT_LIMIT {
            self.recent_events.pop_front();
        }

        self.recent_events.push_back(record);
    }

    pub(crate) fn snapshot(&self) -> MobileHandoffStateSnapshot {
        MobileHandoffStateSnapshot {
            protocol_version: MOBILE_HANDOFF_PROTOCOL_VERSION,
            event_schema_version: MOBILE_HANDOFF_EVENT_SCHEMA_VERSION,
            implementation_stage: MOBILE_HANDOFF_STAGE,
            started_at: self.started_at.clone(),
            last_event_at: self.last_event_at.clone(),
            recent_event_count: self.recent_events.len(),
            recent_events: self.recent_events.iter().cloned().collect(),
        }
    }
}
