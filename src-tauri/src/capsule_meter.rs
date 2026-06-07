use crate::{
    capsule_state::{now_ms, CapsuleStateStore, CAPSULE_LABEL},
    playback::AudioMeter,
};
use serde::Serialize;
use std::{
    sync::Mutex,
    thread,
    time::{Duration, Instant},
};
use tauri::{AppHandle, Emitter, Manager};

pub const CAPSULE_METER_EVENT: &str = "capsule://meter";

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CapsuleMeterFrame {
    pub seq: u64,
    pub track_id: Option<String>,
    pub is_playing: bool,
    pub levels: [u8; 8],
    pub sent_at_ms: u64,
}

impl CapsuleMeterFrame {
    pub fn from_levels(seq: u64, audio_levels: &[f32]) -> Self {
        let mut levels = [0_u8; 8];

        for (index, level) in audio_levels.iter().take(8).enumerate() {
            let scaled = (level.clamp(0.0, 1.0) * 255.0).round();
            levels[index] = scaled.clamp(0.0, 255.0) as u8;
        }

        Self {
            seq,
            track_id: None,
            is_playing: levels.iter().any(|level| *level > 0),
            levels,
            sent_at_ms: now_ms(),
        }
    }
}

pub fn spawn_capsule_meter_emitter(app_handle: AppHandle, audio_meter: AudioMeter) {
    thread::spawn(move || {
        let mut last_frame: Option<CapsuleMeterFrame> = None;

        loop {
            let interval_ms = {
                let capsule_state = app_handle.state::<Mutex<CapsuleStateStore>>();
                let interval_ms = match capsule_state.lock() {
                    Ok(state) => {
                        if !state.is_ready() || !state.allows_meter() {
                            250
                        } else {
                            state.meter_interval_ms()
                        }
                    }
                    Err(_) => break,
                };

                interval_ms
            };

            let should_send = {
                let capsule_state = app_handle.state::<Mutex<CapsuleStateStore>>();
                let should_send = match capsule_state.lock() {
                    Ok(state) => state.is_ready() && state.allows_meter(),
                    Err(_) => break,
                };

                should_send
            };

            if !should_send {
                thread::sleep(Duration::from_millis(interval_ms));
                continue;
            }

            let audio_levels = audio_meter.shared_snapshot();

            let frame = {
                let capsule_state = app_handle.state::<Mutex<CapsuleStateStore>>();
                let mut state = match capsule_state.lock() {
                    Ok(state) => state,
                    Err(_) => break,
                };

                CapsuleMeterFrame::from_levels(state.next_seq(), &audio_levels)
            };

            if last_frame.as_ref() != Some(&frame) {
                emit_meter_frame(&app_handle, &frame);
                last_frame = Some(frame);
            }

            thread::sleep(Duration::from_millis(interval_ms));
        }
    });
}

fn emit_meter_frame(app: &AppHandle, frame: &CapsuleMeterFrame) {
    let payload_bytes = 64;
    let started_at = Instant::now();
    let result = app.emit_to(CAPSULE_LABEL, CAPSULE_METER_EVENT, frame.clone());
    let elapsed_ms = started_at.elapsed().as_millis().min(u128::from(u64::MAX)) as u64;

    if elapsed_ms <= 50 && result.is_ok() {
        return;
    }

    let capsule_state = app.state::<Mutex<CapsuleStateStore>>();

    if let Ok(mut state) = capsule_state.lock() {
        state.record_send_result("meter", elapsed_ms, payload_bytes, result.is_ok());
    };
}
