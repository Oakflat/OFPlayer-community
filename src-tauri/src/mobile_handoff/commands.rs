use serde_json::json;
use std::sync::Mutex;
use tauri::State;

use crate::diagnostics::{DiagnosticsLogEventRequest, DiagnosticsLogStore};

use super::{
    events::build_event_record,
    state::MobileHandoffState,
    types::{
        capabilities, MobileHandoffCapabilities, MobileHandoffEventRecord,
        MobileHandoffEventRequest, MobileHandoffStateSnapshot,
    },
};

#[tauri::command]
pub fn mobile_handoff_capabilities() -> MobileHandoffCapabilities {
    capabilities()
}

#[tauri::command]
pub fn mobile_handoff_state_snapshot(
    state: State<'_, Mutex<MobileHandoffState>>,
) -> Result<MobileHandoffStateSnapshot, String> {
    let state = state
        .lock()
        .map_err(|_| String::from("Mobile handoff state lock was poisoned."))?;

    Ok(state.snapshot())
}

#[tauri::command]
pub fn mobile_handoff_record_event(
    state: State<'_, Mutex<MobileHandoffState>>,
    diagnostics: State<'_, Mutex<DiagnosticsLogStore>>,
    request: MobileHandoffEventRequest,
) -> Result<MobileHandoffEventRecord, String> {
    let record = build_event_record(request)?;

    {
        let mut state = state
            .lock()
            .map_err(|_| String::from("Mobile handoff state lock was poisoned."))?;
        state.record_event(record.clone());
    }

    append_handoff_diagnostics(diagnostics, &record);
    Ok(record)
}

fn append_handoff_diagnostics(
    diagnostics: State<'_, Mutex<DiagnosticsLogStore>>,
    record: &MobileHandoffEventRecord,
) {
    let request = DiagnosticsLogEventRequest {
        level: Some(String::from("info")),
        category: String::from("mobile_handoff"),
        event: record.event.clone(),
        label: Some(String::from("[OFPlayer mobile handoff]")),
        payload: Some(json!({
            "id": &record.id,
            "schemaVersion": record.schema_version,
            "attributes": &record.attributes,
        })),
    };

    let result = diagnostics
        .lock()
        .map_err(|_| String::from("Diagnostics log lock was poisoned."))
        .and_then(|diagnostics| diagnostics.append_event(&request));

    if let Err(error) = result {
        #[cfg(debug_assertions)]
        eprintln!("[OFPlayer mobile handoff] diagnostics write skipped: {error}");

        #[cfg(not(debug_assertions))]
        let _ = error;
    }
}
