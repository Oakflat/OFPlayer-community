use crate::diagnostics::{DiagnosticStepProfile, ProcessResourceDiagnostics};
use serde::Serialize;
use std::{
    collections::{HashMap, VecDeque},
    sync::Mutex,
};
use tauri::{AppHandle, Emitter, State};
use uuid::Uuid;

const LIBRARY_SCAN_PROGRESS_EVENT: &str = "library://scan-progress";
const IMPORT_JOB_MAX_HISTORY: usize = 24;

pub(crate) const IMPORT_JOB_STATUS_QUEUED: &str = "queued";
pub(crate) const IMPORT_JOB_STATUS_RUNNING: &str = "running";
pub(crate) const IMPORT_JOB_STATUS_COMPLETED: &str = "completed";
pub(crate) const IMPORT_JOB_STATUS_EMPTY: &str = "empty";
pub(crate) const IMPORT_JOB_STATUS_FAILED: &str = "failed";

pub(crate) const IMPORT_STAGE_DISCOVER: &str = "discover";
pub(crate) const IMPORT_STAGE_FILTER: &str = "filter";
pub(crate) const IMPORT_STAGE_PREPARE: &str = "prepare";
pub(crate) const IMPORT_STAGE_PERSIST: &str = "persist";
pub(crate) const IMPORT_STAGE_PLAYBACK_SYNC: &str = "playbackSync";

const IMPORT_STAGE_STATUS_PENDING: &str = "pending";
pub(crate) const IMPORT_STAGE_STATUS_RUNNING: &str = "running";
pub(crate) const IMPORT_STAGE_STATUS_COMPLETED: &str = "completed";
pub(crate) const IMPORT_STAGE_STATUS_SKIPPED: &str = "skipped";
pub(crate) const IMPORT_STAGE_STATUS_FAILED: &str = "failed";

#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
pub(crate) struct LibraryImportDiagnostics {
    pub(crate) total_ms: u64,
    pub(crate) discover_ms: u64,
    pub(crate) filter_ms: u64,
    pub(crate) prepare_ms: u64,
    pub(crate) persist_ms: u64,
    pub(crate) playback_sync_ms: u64,
    pub(crate) copy_ms: u64,
    pub(crate) metadata_ms: u64,
    pub(crate) metadata_fallback_count: usize,
    pub(crate) directories_scanned: usize,
    pub(crate) entries_scanned: usize,
    pub(crate) discovered_total: usize,
    pub(crate) candidate_total: usize,
    pub(crate) imported_total: usize,
    pub(crate) process: ProcessResourceDiagnostics,
    pub(crate) step_profiles: Vec<DiagnosticStepProfile>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct LibraryScanProgressEventPayload {
    job: LibraryImportJobSnapshot,
    phase: String,
    percent: u8,
    processed: usize,
    total: usize,
    imported: usize,
    discovered_total: usize,
    candidate_total: usize,
    directories_scanned: usize,
    entries_scanned: usize,
    current_file: String,
    elapsed_ms: u64,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct LibraryImportJobStageSnapshot {
    pub(crate) key: String,
    pub(crate) status: String,
    pub(crate) started_at: Option<String>,
    pub(crate) completed_at: Option<String>,
    pub(crate) duration_ms: Option<u64>,
    pub(crate) processed: usize,
    pub(crate) total: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct LibraryImportJobSnapshot {
    pub(crate) id: String,
    pub(crate) mode: String,
    pub(crate) status: String,
    pub(crate) library_id: String,
    pub(crate) created_at: String,
    pub(crate) updated_at: String,
    pub(crate) completed_at: Option<String>,
    pub(crate) current_stage: String,
    pub(crate) discovered_total: usize,
    pub(crate) candidate_total: usize,
    pub(crate) imported_total: usize,
    pub(crate) directories_scanned: usize,
    pub(crate) entries_scanned: usize,
    pub(crate) current_file: String,
    pub(crate) error: Option<String>,
    pub(crate) diagnostics: Option<LibraryImportDiagnostics>,
    pub(crate) stages: Vec<LibraryImportJobStageSnapshot>,
}

#[derive(Debug, Default)]
pub(crate) struct LibraryImportJobStore {
    jobs: HashMap<String, LibraryImportJobSnapshot>,
    order: VecDeque<String>,
}

impl LibraryImportJobStore {
    fn insert(&mut self, snapshot: LibraryImportJobSnapshot) {
        let job_id = snapshot.id.clone();

        if !self.jobs.contains_key(&job_id) {
            self.order.push_back(job_id.clone());
        }

        self.jobs.insert(job_id.clone(), snapshot);

        while self.order.len() > IMPORT_JOB_MAX_HISTORY {
            if let Some(removed_job_id) = self.order.pop_front() {
                self.jobs.remove(&removed_job_id);
            }
        }
    }

    pub(crate) fn clear(&mut self) {
        self.jobs.clear();
        self.order.clear();
    }
}

pub(crate) fn create_library_import_job_snapshot(
    mode: &str,
    library_id: &str,
) -> LibraryImportJobSnapshot {
    let now = current_iso_timestamp();

    LibraryImportJobSnapshot {
        id: format!("import-job-{}", Uuid::new_v4()),
        mode: String::from(mode),
        status: String::from(IMPORT_JOB_STATUS_QUEUED),
        library_id: String::from(library_id),
        created_at: now.clone(),
        updated_at: now,
        completed_at: None,
        current_stage: String::new(),
        discovered_total: 0,
        candidate_total: 0,
        imported_total: 0,
        directories_scanned: 0,
        entries_scanned: 0,
        current_file: String::new(),
        error: None,
        diagnostics: None,
        stages: vec![
            create_library_import_job_stage(IMPORT_STAGE_DISCOVER),
            create_library_import_job_stage(IMPORT_STAGE_FILTER),
            create_library_import_job_stage(IMPORT_STAGE_PREPARE),
            create_library_import_job_stage(IMPORT_STAGE_PERSIST),
            create_library_import_job_stage(IMPORT_STAGE_PLAYBACK_SYNC),
        ],
    }
}

fn create_library_import_job_stage(key: &str) -> LibraryImportJobStageSnapshot {
    LibraryImportJobStageSnapshot {
        key: String::from(key),
        status: String::from(IMPORT_STAGE_STATUS_PENDING),
        started_at: None,
        completed_at: None,
        duration_ms: None,
        processed: 0,
        total: 0,
    }
}

pub(crate) fn persist_library_import_job_snapshot(
    job_store: &State<'_, Mutex<LibraryImportJobStore>>,
    snapshot: &LibraryImportJobSnapshot,
) -> Result<(), String> {
    let mut job_store = job_store
        .lock()
        .map_err(|_| String::from("Library import job state lock was poisoned."))?;
    job_store.insert(snapshot.clone());
    Ok(())
}

pub(crate) fn update_library_import_job_stage(
    snapshot: &mut LibraryImportJobSnapshot,
    stage_key: &str,
    stage_status: &str,
    processed: usize,
    total: usize,
    duration_ms: Option<u64>,
) {
    let now = current_iso_timestamp();

    snapshot.status = if matches!(
        stage_status,
        IMPORT_STAGE_STATUS_PENDING | IMPORT_STAGE_STATUS_RUNNING
    ) {
        String::from(IMPORT_JOB_STATUS_RUNNING)
    } else {
        snapshot.status.clone()
    };
    snapshot.current_stage = String::from(stage_key);
    snapshot.updated_at = now.clone();

    if let Some(stage) = snapshot
        .stages
        .iter_mut()
        .find(|stage| stage.key == stage_key)
    {
        if matches!(
            stage_status,
            IMPORT_STAGE_STATUS_RUNNING
                | IMPORT_STAGE_STATUS_COMPLETED
                | IMPORT_STAGE_STATUS_FAILED
        ) && stage.started_at.is_none()
        {
            stage.started_at = Some(now.clone());
        }

        if matches!(
            stage_status,
            IMPORT_STAGE_STATUS_COMPLETED
                | IMPORT_STAGE_STATUS_SKIPPED
                | IMPORT_STAGE_STATUS_FAILED
        ) {
            stage.completed_at = Some(now);
        }

        stage.status = String::from(stage_status);
        stage.processed = processed;
        stage.total = total;
        stage.duration_ms = duration_ms;
    }
}

pub(crate) fn finalize_library_import_job(
    snapshot: &mut LibraryImportJobSnapshot,
    status: &str,
    diagnostics: LibraryImportDiagnostics,
    error: Option<String>,
) {
    let now = current_iso_timestamp();
    snapshot.status = String::from(status);
    snapshot.updated_at = now.clone();
    snapshot.completed_at = Some(now);
    snapshot.error = error;
    snapshot.diagnostics = Some(diagnostics.clone());
    snapshot.discovered_total = diagnostics.discovered_total;
    snapshot.candidate_total = diagnostics.candidate_total;
    snapshot.imported_total = diagnostics.imported_total;
    snapshot.directories_scanned = diagnostics.directories_scanned;
    snapshot.entries_scanned = diagnostics.entries_scanned;
    snapshot.current_file.clear();
}

pub(crate) fn progress_percent_from_ratio(range_start: u8, range_end: u8, ratio: f64) -> u8 {
    if range_end <= range_start {
        return range_end;
    }

    let span = f64::from(range_end - range_start);
    let clamped_ratio = ratio.clamp(0.0, 1.0);
    let value = f64::from(range_start) + span * clamped_ratio;

    value.round().clamp(0.0, 100.0) as u8
}

pub(crate) fn progress_percent(
    range_start: u8,
    range_end: u8,
    processed: usize,
    total: usize,
) -> u8 {
    if total == 0 {
        return range_end;
    }

    progress_percent_from_ratio(
        range_start,
        range_end,
        processed.min(total) as f64 / total as f64,
    )
}

fn emit_library_scan_progress(app: &AppHandle, payload: LibraryScanProgressEventPayload) {
    let _ = app.emit(LIBRARY_SCAN_PROGRESS_EVENT, payload);
}

#[allow(clippy::too_many_arguments)]
pub(crate) fn emit_library_import_job_progress(
    app: &AppHandle,
    job: &LibraryImportJobSnapshot,
    phase: String,
    percent: u8,
    processed: usize,
    total: usize,
    imported: usize,
    discovered_total: usize,
    candidate_total: usize,
    directories_scanned: usize,
    entries_scanned: usize,
    current_file: String,
    elapsed_ms: u64,
) {
    emit_library_scan_progress(
        app,
        LibraryScanProgressEventPayload {
            job: job.clone(),
            phase,
            percent,
            processed,
            total,
            imported,
            discovered_total,
            candidate_total,
            directories_scanned,
            entries_scanned,
            current_file,
            elapsed_ms,
        },
    );
}

fn current_iso_timestamp() -> String {
    chrono::Utc::now().to_rfc3339()
}

fn find_job_stage(
    snapshot: &LibraryImportJobSnapshot,
    stage_key: &str,
) -> Option<LibraryImportJobStageSnapshot> {
    snapshot
        .stages
        .iter()
        .find(|stage| stage.key == stage_key)
        .cloned()
}

pub(crate) fn mark_library_import_job_failed(snapshot: &mut LibraryImportJobSnapshot) {
    let stage_key = if snapshot.current_stage.is_empty() {
        snapshot
            .stages
            .iter()
            .find(|stage| stage.status == IMPORT_STAGE_STATUS_PENDING)
            .map(|stage| stage.key.clone())
            .unwrap_or_else(|| String::from(IMPORT_STAGE_FILTER))
    } else {
        snapshot.current_stage.clone()
    };
    let failed_stage = find_job_stage(snapshot, &stage_key);
    update_library_import_job_stage(
        snapshot,
        &stage_key,
        IMPORT_STAGE_STATUS_FAILED,
        failed_stage
            .as_ref()
            .map(|stage| stage.processed)
            .unwrap_or(0),
        failed_stage.as_ref().map(|stage| stage.total).unwrap_or(0),
        failed_stage.and_then(|stage| stage.duration_ms),
    );
}

pub(crate) fn build_library_import_diagnostics_from_job(
    snapshot: &LibraryImportJobSnapshot,
    total_ms: u64,
    process: ProcessResourceDiagnostics,
    step_profiles: Vec<DiagnosticStepProfile>,
) -> LibraryImportDiagnostics {
    let stage_duration = |stage_key: &str| {
        find_job_stage(snapshot, stage_key)
            .and_then(|stage| stage.duration_ms)
            .unwrap_or(0)
    };

    LibraryImportDiagnostics {
        total_ms,
        discover_ms: stage_duration(IMPORT_STAGE_DISCOVER),
        filter_ms: stage_duration(IMPORT_STAGE_FILTER),
        prepare_ms: stage_duration(IMPORT_STAGE_PREPARE),
        persist_ms: stage_duration(IMPORT_STAGE_PERSIST),
        playback_sync_ms: stage_duration(IMPORT_STAGE_PLAYBACK_SYNC),
        copy_ms: snapshot
            .diagnostics
            .as_ref()
            .map(|diagnostics| diagnostics.copy_ms)
            .unwrap_or(0),
        metadata_ms: snapshot
            .diagnostics
            .as_ref()
            .map(|diagnostics| diagnostics.metadata_ms)
            .unwrap_or(0),
        metadata_fallback_count: snapshot
            .diagnostics
            .as_ref()
            .map(|diagnostics| diagnostics.metadata_fallback_count)
            .unwrap_or(0),
        directories_scanned: snapshot.directories_scanned,
        entries_scanned: snapshot.entries_scanned,
        discovered_total: snapshot.discovered_total,
        candidate_total: snapshot.candidate_total,
        imported_total: snapshot.imported_total,
        process,
        step_profiles,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn progress_percent_clamps_to_configured_range() {
        assert_eq!(progress_percent(42, 88, 0, 0), 88);
        assert_eq!(progress_percent(42, 88, 0, 10), 42);
        assert_eq!(progress_percent(42, 88, 5, 10), 65);
        assert_eq!(progress_percent(42, 88, 99, 10), 88);
        assert_eq!(progress_percent_from_ratio(90, 80, 0.5), 80);
        assert_eq!(progress_percent_from_ratio(0, 100, -1.0), 0);
        assert_eq!(progress_percent_from_ratio(0, 100, 1.5), 100);
    }

    #[test]
    fn stage_updates_preserve_job_totals_until_finalization() {
        let mut job = create_library_import_job_snapshot("scan-import", "library-1");

        update_library_import_job_stage(
            &mut job,
            IMPORT_STAGE_PREPARE,
            IMPORT_STAGE_STATUS_RUNNING,
            2,
            5,
            None,
        );

        let running_stage = find_job_stage(&job, IMPORT_STAGE_PREPARE).unwrap();
        assert_eq!(job.status, IMPORT_JOB_STATUS_RUNNING);
        assert_eq!(job.current_stage, IMPORT_STAGE_PREPARE);
        assert_eq!(running_stage.processed, 2);
        assert_eq!(running_stage.total, 5);
        assert!(running_stage.started_at.is_some());
        assert!(running_stage.completed_at.is_none());

        update_library_import_job_stage(
            &mut job,
            IMPORT_STAGE_PREPARE,
            IMPORT_STAGE_STATUS_COMPLETED,
            5,
            5,
            Some(123),
        );

        let completed_stage = find_job_stage(&job, IMPORT_STAGE_PREPARE).unwrap();
        assert_eq!(completed_stage.status, IMPORT_STAGE_STATUS_COMPLETED);
        assert_eq!(completed_stage.duration_ms, Some(123));
        assert!(completed_stage.completed_at.is_some());
        assert_eq!(job.status, IMPORT_JOB_STATUS_RUNNING);
    }

    #[test]
    fn job_store_evicts_oldest_history_entries() {
        let mut store = LibraryImportJobStore::default();

        for index in 0..(IMPORT_JOB_MAX_HISTORY + 3) {
            let mut job = create_library_import_job_snapshot("scan-import", "library-1");
            job.id = format!("job-{index}");
            store.insert(job);
        }

        assert_eq!(store.jobs.len(), IMPORT_JOB_MAX_HISTORY);
        assert_eq!(store.order.len(), IMPORT_JOB_MAX_HISTORY);
        assert!(!store.jobs.contains_key("job-0"));
        assert!(!store.jobs.contains_key("job-1"));
        assert!(!store.jobs.contains_key("job-2"));
        assert!(store.jobs.contains_key("job-3"));
    }

    #[test]
    fn mark_failed_uses_current_stage_progress() {
        let mut job = create_library_import_job_snapshot("scan-import", "library-1");
        update_library_import_job_stage(
            &mut job,
            IMPORT_STAGE_PREPARE,
            IMPORT_STAGE_STATUS_RUNNING,
            2,
            5,
            Some(77),
        );

        mark_library_import_job_failed(&mut job);

        let failed_stage = find_job_stage(&job, IMPORT_STAGE_PREPARE).unwrap();
        assert_eq!(job.status, IMPORT_JOB_STATUS_RUNNING);
        assert_eq!(job.current_stage, IMPORT_STAGE_PREPARE);
        assert_eq!(failed_stage.status, IMPORT_STAGE_STATUS_FAILED);
        assert_eq!(failed_stage.processed, 2);
        assert_eq!(failed_stage.total, 5);
        assert_eq!(failed_stage.duration_ms, Some(77));
        assert!(failed_stage.completed_at.is_some());
    }

    #[test]
    fn diagnostics_from_job_reads_stage_durations_and_totals() {
        let mut job = create_library_import_job_snapshot("scan-import", "library-1");
        job.discovered_total = 9;
        job.candidate_total = 7;
        job.imported_total = 5;
        job.directories_scanned = 3;
        job.entries_scanned = 42;
        job.diagnostics = Some(LibraryImportDiagnostics {
            copy_ms: 11,
            metadata_ms: 13,
            metadata_fallback_count: 2,
            ..LibraryImportDiagnostics::default()
        });

        update_library_import_job_stage(
            &mut job,
            IMPORT_STAGE_DISCOVER,
            IMPORT_STAGE_STATUS_COMPLETED,
            3,
            3,
            Some(17),
        );
        update_library_import_job_stage(
            &mut job,
            IMPORT_STAGE_FILTER,
            IMPORT_STAGE_STATUS_COMPLETED,
            9,
            9,
            Some(19),
        );

        let diagnostics = build_library_import_diagnostics_from_job(
            &job,
            101,
            ProcessResourceDiagnostics::default(),
            Vec::new(),
        );

        assert_eq!(diagnostics.total_ms, 101);
        assert_eq!(diagnostics.discover_ms, 17);
        assert_eq!(diagnostics.filter_ms, 19);
        assert_eq!(diagnostics.copy_ms, 11);
        assert_eq!(diagnostics.metadata_ms, 13);
        assert_eq!(diagnostics.metadata_fallback_count, 2);
        assert_eq!(diagnostics.discovered_total, 9);
        assert_eq!(diagnostics.candidate_total, 7);
        assert_eq!(diagnostics.imported_total, 5);
        assert_eq!(diagnostics.directories_scanned, 3);
        assert_eq!(diagnostics.entries_scanned, 42);
    }
}
