use crate::app_paths;
use chrono::Utc;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    fs::{self, OpenOptions},
    io::{ErrorKind, Write},
    path::{Path, PathBuf},
    sync::mpsc::{sync_channel, RecvTimeoutError, SyncSender, TrySendError},
    thread,
    time::Duration,
};
use tauri::AppHandle;

#[cfg(windows)]
use std::mem::size_of;
#[cfg(windows)]
use windows::Win32::{
    Foundation::FILETIME,
    System::{
        ProcessStatus::{
            K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS, PROCESS_MEMORY_COUNTERS_EX,
        },
        Threading::{GetCurrentProcess, GetProcessHandleCount, GetProcessTimes},
    },
};

const DIAGNOSTICS_LOG_FILE_NAME: &str = "ofplayer-diagnostics.ndjson";
const DIAGNOSTICS_LOG_ARCHIVE_FILE_NAME: &str = "ofplayer-diagnostics.previous.ndjson";
const DIAGNOSTICS_LOG_MAX_BYTES: u64 = 2_000_000;
const DIAGNOSTICS_WORKER_QUEUE_CAPACITY: usize = 512;
const DIAGNOSTICS_WORKER_FLUSH_INTERVAL_MS: u64 = 250;
const DIAGNOSTICS_WORKER_BATCH_LIMIT: usize = 64;

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessResourceSnapshot {
    pub user_cpu_ms: u64,
    pub kernel_cpu_ms: u64,
    pub total_cpu_ms: u64,
    pub working_set_bytes: u64,
    pub private_bytes: u64,
    pub pagefile_bytes: u64,
    pub handle_count: u64,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessResourceDelta {
    pub user_cpu_ms: i64,
    pub kernel_cpu_ms: i64,
    pub total_cpu_ms: i64,
    pub working_set_bytes: i64,
    pub private_bytes: i64,
    pub pagefile_bytes: i64,
    pub handle_count: i64,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessResourceDiagnostics {
    pub sampled: bool,
    pub start: Option<ProcessResourceSnapshot>,
    pub end: Option<ProcessResourceSnapshot>,
    pub delta: Option<ProcessResourceDelta>,
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiagnosticStepProfile {
    pub key: String,
    pub elapsed_ms: u64,
    pub sampled: bool,
    pub user_cpu_ms: i64,
    pub kernel_cpu_ms: i64,
    pub total_cpu_ms: i64,
    pub working_set_bytes: u64,
    pub working_set_delta_bytes: i64,
    pub private_bytes: u64,
    pub private_delta_bytes: i64,
    pub pagefile_bytes: u64,
    pub pagefile_delta_bytes: i64,
    pub handle_count: u64,
    pub handle_delta: i64,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DiagnosticsLogEventRequest {
    pub level: Option<String>,
    pub category: String,
    pub event: String,
    pub label: Option<String>,
    pub payload: Option<Value>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DiagnosticsLogStatus {
    pub path: String,
    pub directory: String,
    pub directory_kind: String,
    pub fallback_reason: Option<String>,
}

#[derive(Default)]
pub struct DiagnosticsLogStore {
    log_path: Option<PathBuf>,
    diagnostics_dir: Option<PathBuf>,
    directory_kind: Option<String>,
    fallback_reason: Option<String>,
    sender: Option<SyncSender<DiagnosticsWorkerMessage>>,
}

enum DiagnosticsWorkerMessage {
    Append(Value),
}

impl DiagnosticsLogStore {
    pub fn initialize(&mut self, app: &AppHandle) -> Result<(), String> {
        if self.log_path.is_some() {
            return Ok(());
        }

        let diagnostics_target = resolve_diagnostics_target(app)?;
        let diagnostics_dir = diagnostics_target.directory;

        let log_path = diagnostics_dir.join(DIAGNOSTICS_LOG_FILE_NAME);
        Self::rotate_if_needed(&log_path)?;
        let sender = spawn_diagnostics_worker(log_path.clone());
        self.log_path = Some(log_path.clone());
        self.diagnostics_dir = Some(diagnostics_dir.clone());
        self.directory_kind = Some(diagnostics_target.kind.clone());
        self.fallback_reason = diagnostics_target.fallback_reason.clone();
        self.sender = Some(sender);

        self.append_event(&DiagnosticsLogEventRequest {
            level: Some(String::from("info")),
            category: String::from("app"),
            event: String::from("session_started"),
            label: Some(String::from("[OFPlayer diagnostics]")),
            payload: Some(json!({
                "processId": std::process::id(),
                "version": env!("CARGO_PKG_VERSION"),
                "path": log_path.display().to_string(),
                "directory": diagnostics_dir.display().to_string(),
                "directoryKind": diagnostics_target.kind,
                "fallbackReason": diagnostics_target.fallback_reason,
            })),
        })?;

        Ok(())
    }

    pub fn log_status(&self) -> Result<DiagnosticsLogStatus, String> {
        let log_path = self
            .log_path
            .as_ref()
            .ok_or_else(|| String::from("OFPlayer diagnostics logging is not initialized yet."))?;

        Ok(DiagnosticsLogStatus {
            path: log_path.display().to_string(),
            directory: self
                .diagnostics_dir
                .as_ref()
                .map(|path| path.display().to_string())
                .unwrap_or_default(),
            directory_kind: self
                .directory_kind
                .clone()
                .unwrap_or_else(|| String::from("unknown")),
            fallback_reason: self.fallback_reason.clone(),
        })
    }

    pub fn append_event(&self, request: &DiagnosticsLogEventRequest) -> Result<(), String> {
        let sender = self
            .sender
            .as_ref()
            .ok_or_else(|| String::from("OFPlayer diagnostics logging is not initialized yet."))?;

        let entry = json!({
            "timestamp": Utc::now().to_rfc3339(),
            "level": request.level.as_deref().unwrap_or("info"),
            "category": request.category,
            "event": request.event,
            "label": request.label,
            "payload": request.payload.clone().unwrap_or(Value::Null),
        });

        match sender.try_send(DiagnosticsWorkerMessage::Append(entry)) {
            Ok(()) => Ok(()),
            Err(TrySendError::Full(_)) => Ok(()),
            Err(TrySendError::Disconnected(_)) => {
                Err(String::from("OFPlayer diagnostics worker is unavailable."))
            }
        }
    }

    fn write_entries(log_path: &Path, entries: &[Value]) -> Result<(), String> {
        if entries.is_empty() {
            return Ok(());
        }

        Self::rotate_if_needed(log_path)?;

        let mut file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(log_path)
            .map_err(|error| {
                format!(
                    "Failed to open the OFPlayer diagnostics log '{}': {error}",
                    log_path.display()
                )
            })?;

        for entry in entries {
            serde_json::to_writer(&mut file, entry).map_err(|error| {
                format!("Failed to serialize an OFPlayer diagnostics event: {error}")
            })?;
            file.write_all(b"\n").map_err(|error| {
                format!("Failed to finalize an OFPlayer diagnostics log entry: {error}")
            })?;
        }

        file.flush().map_err(|error| {
            format!(
                "Failed to flush the OFPlayer diagnostics log '{}': {error}",
                log_path.display()
            )
        })?;
        Ok(())
    }

    fn rotate_if_needed(log_path: &Path) -> Result<(), String> {
        let metadata = match fs::metadata(log_path) {
            Ok(metadata) => metadata,
            Err(error) if error.kind() == ErrorKind::NotFound => return Ok(()),
            Err(error) => {
                return Err(format!(
                    "Failed to inspect the OFPlayer diagnostics log '{}': {error}",
                    log_path.display()
                ))
            }
        };

        if metadata.len() < DIAGNOSTICS_LOG_MAX_BYTES {
            return Ok(());
        }

        let archive_path = log_path.with_file_name(DIAGNOSTICS_LOG_ARCHIVE_FILE_NAME);

        match fs::remove_file(&archive_path) {
            Ok(()) => {}
            Err(error) if error.kind() == ErrorKind::NotFound => {}
            Err(error) => {
                return Err(format!(
                    "Failed to remove the previous OFPlayer diagnostics archive '{}': {error}",
                    archive_path.display()
                ))
            }
        }

        fs::rename(log_path, &archive_path).map_err(|error| {
            format!(
                "Failed to rotate the OFPlayer diagnostics log '{}' -> '{}': {error}",
                log_path.display(),
                archive_path.display()
            )
        })?;

        Ok(())
    }
}

fn spawn_diagnostics_worker(log_path: PathBuf) -> SyncSender<DiagnosticsWorkerMessage> {
    let (sender, receiver) =
        sync_channel::<DiagnosticsWorkerMessage>(DIAGNOSTICS_WORKER_QUEUE_CAPACITY);

    let _ = thread::Builder::new()
        .name(String::from("ofplayer-diagnostics-worker"))
        .spawn(move || {
            let mut pending_entries = Vec::with_capacity(DIAGNOSTICS_WORKER_BATCH_LIMIT);

            loop {
                match receiver
                    .recv_timeout(Duration::from_millis(DIAGNOSTICS_WORKER_FLUSH_INTERVAL_MS))
                {
                    Ok(DiagnosticsWorkerMessage::Append(entry)) => pending_entries.push(entry),
                    Err(RecvTimeoutError::Timeout) => {}
                    Err(RecvTimeoutError::Disconnected) => {
                        let _ = DiagnosticsLogStore::write_entries(&log_path, &pending_entries);
                        break;
                    }
                }

                while pending_entries.len() < DIAGNOSTICS_WORKER_BATCH_LIMIT {
                    match receiver.try_recv() {
                        Ok(DiagnosticsWorkerMessage::Append(entry)) => pending_entries.push(entry),
                        Err(_) => break,
                    }
                }

                if pending_entries.is_empty() {
                    continue;
                }

                let _ = DiagnosticsLogStore::write_entries(&log_path, &pending_entries);
                pending_entries.clear();
            }
        });

    sender
}

#[derive(Debug)]
struct DiagnosticsTarget {
    directory: PathBuf,
    kind: String,
    fallback_reason: Option<String>,
}

fn resolve_diagnostics_target(_app: &AppHandle) -> Result<DiagnosticsTarget, String> {
    let directory = app_paths::diagnostics_dir().map_err(|error| {
        format!(
            "Failed to prepare OFPlayer diagnostics under the AppData product directory: {error}"
        )
    })?;

    Ok(DiagnosticsTarget {
        directory,
        kind: String::from("appDataProduct"),
        fallback_reason: None,
    })
}

pub fn capture_process_resource_snapshot() -> Option<ProcessResourceSnapshot> {
    #[cfg(not(windows))]
    {
        None
    }

    #[cfg(windows)]
    {
        let process = unsafe { GetCurrentProcess() };
        let mut counters = PROCESS_MEMORY_COUNTERS_EX::default();
        let memory_info_loaded = unsafe {
            K32GetProcessMemoryInfo(
                process,
                &mut counters as *mut _ as *mut PROCESS_MEMORY_COUNTERS,
                size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32,
            )
            .as_bool()
        };

        if !memory_info_loaded {
            return None;
        }

        let mut creation_time = FILETIME::default();
        let mut exit_time = FILETIME::default();
        let mut kernel_time = FILETIME::default();
        let mut user_time = FILETIME::default();
        let timing_loaded = unsafe {
            GetProcessTimes(
                process,
                &mut creation_time,
                &mut exit_time,
                &mut kernel_time,
                &mut user_time,
            )
            .is_ok()
        };

        if !timing_loaded {
            return None;
        }

        let mut handle_count = 0u32;
        let _ = unsafe { GetProcessHandleCount(process, &mut handle_count) };

        let user_cpu_ms = filetime_to_ms(&user_time);
        let kernel_cpu_ms = filetime_to_ms(&kernel_time);

        Some(ProcessResourceSnapshot {
            user_cpu_ms,
            kernel_cpu_ms,
            total_cpu_ms: user_cpu_ms.saturating_add(kernel_cpu_ms),
            working_set_bytes: counters.WorkingSetSize as u64,
            private_bytes: counters.PrivateUsage as u64,
            pagefile_bytes: counters.PagefileUsage as u64,
            handle_count: u64::from(handle_count),
        })
    }
}

pub fn build_process_resource_diagnostics(
    start: Option<&ProcessResourceSnapshot>,
    end: Option<&ProcessResourceSnapshot>,
) -> ProcessResourceDiagnostics {
    let sampled = start.is_some() && end.is_some();

    ProcessResourceDiagnostics {
        sampled,
        start: start.cloned(),
        end: end.cloned(),
        delta: match (start, end) {
            (Some(start), Some(end)) => Some(ProcessResourceDelta {
                user_cpu_ms: delta_i64(end.user_cpu_ms, start.user_cpu_ms),
                kernel_cpu_ms: delta_i64(end.kernel_cpu_ms, start.kernel_cpu_ms),
                total_cpu_ms: delta_i64(end.total_cpu_ms, start.total_cpu_ms),
                working_set_bytes: delta_i64(end.working_set_bytes, start.working_set_bytes),
                private_bytes: delta_i64(end.private_bytes, start.private_bytes),
                pagefile_bytes: delta_i64(end.pagefile_bytes, start.pagefile_bytes),
                handle_count: delta_i64(end.handle_count, start.handle_count),
            }),
            _ => None,
        },
    }
}

pub fn build_diagnostic_step_profile(
    key: &str,
    elapsed_ms: u64,
    start: Option<&ProcessResourceSnapshot>,
    end: Option<&ProcessResourceSnapshot>,
) -> DiagnosticStepProfile {
    let sampled = start.is_some() && end.is_some();
    let working_set_bytes = end.map(|snapshot| snapshot.working_set_bytes).unwrap_or(0);
    let private_bytes = end.map(|snapshot| snapshot.private_bytes).unwrap_or(0);
    let pagefile_bytes = end.map(|snapshot| snapshot.pagefile_bytes).unwrap_or(0);
    let handle_count = end.map(|snapshot| snapshot.handle_count).unwrap_or(0);

    DiagnosticStepProfile {
        key: String::from(key),
        elapsed_ms,
        sampled,
        user_cpu_ms: sampled_delta(start, end, |snapshot| snapshot.user_cpu_ms),
        kernel_cpu_ms: sampled_delta(start, end, |snapshot| snapshot.kernel_cpu_ms),
        total_cpu_ms: sampled_delta(start, end, |snapshot| snapshot.total_cpu_ms),
        working_set_bytes,
        working_set_delta_bytes: sampled_delta(start, end, |snapshot| snapshot.working_set_bytes),
        private_bytes,
        private_delta_bytes: sampled_delta(start, end, |snapshot| snapshot.private_bytes),
        pagefile_bytes,
        pagefile_delta_bytes: sampled_delta(start, end, |snapshot| snapshot.pagefile_bytes),
        handle_count,
        handle_delta: sampled_delta(start, end, |snapshot| snapshot.handle_count),
    }
}

fn sampled_delta<F>(
    start: Option<&ProcessResourceSnapshot>,
    end: Option<&ProcessResourceSnapshot>,
    projector: F,
) -> i64
where
    F: Fn(&ProcessResourceSnapshot) -> u64,
{
    match (start, end) {
        (Some(start), Some(end)) => delta_i64(projector(end), projector(start)),
        _ => 0,
    }
}

fn delta_i64(end: u64, start: u64) -> i64 {
    if end >= start {
        end.saturating_sub(start).min(i64::MAX as u64) as i64
    } else {
        -(start.saturating_sub(end).min(i64::MAX as u64) as i64)
    }
}

#[cfg(windows)]
fn filetime_to_ms(value: &FILETIME) -> u64 {
    let ticks = (u64::from(value.dwHighDateTime) << 32) | u64::from(value.dwLowDateTime);
    ticks / 10_000
}
