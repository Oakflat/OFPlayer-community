use serde::Deserialize;
use tauri::{AppHandle, Manager};

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CapsuleHitRegionRequest {
    pub capsule_width: f64,
    pub expanded: bool,
}

#[cfg(target_os = "windows")]
use std::{
    collections::HashMap,
    sync::{Arc, Mutex, OnceLock},
    thread,
    time::Duration,
};

#[cfg(target_os = "windows")]
const CAPSULE_LABEL: &str = "lyric-capsule";
#[cfg(target_os = "windows")]
const WINDOW_WIDTH: f64 = 560.0;
#[cfg(target_os = "windows")]
const CAPSULE_MIN_WIDTH: f64 = 320.0;
#[cfg(target_os = "windows")]
const CAPSULE_MAX_WIDTH: f64 = 540.0;
#[cfg(target_os = "windows")]
const REGION_TOP: f64 = 4.0;
#[cfg(target_os = "windows")]
const REGION_COLLAPSED_BOTTOM: f64 = 56.0;
#[cfg(target_os = "windows")]
const REGION_EXPANDED_BOTTOM: f64 = 84.0;
#[cfg(target_os = "windows")]
const MAPPER_ACTIVE_INTERVAL_MS: u64 = 16;
#[cfg(target_os = "windows")]
const MAPPER_IDLE_INTERVAL_MS: u64 = 28;

#[cfg(target_os = "windows")]
#[derive(Debug, Clone, Copy)]
struct CapsuleMappedRegion {
    left: i32,
    top: i32,
    right: i32,
    collapsed_bottom: i32,
    expanded_bottom: i32,
}

#[cfg(target_os = "windows")]
#[derive(Debug, Clone, Copy)]
struct CapsuleMouseMapState {
    region: CapsuleMappedRegion,
    expanded: bool,
    hovering: bool,
    ignore_cursor_events: Option<bool>,
}

#[cfg(target_os = "windows")]
static CAPSULE_MOUSE_MAP_STATES: OnceLock<Arc<Mutex<HashMap<isize, CapsuleMouseMapState>>>> =
    OnceLock::new();

#[cfg(target_os = "windows")]
pub fn apply_capsule_hit_region(
    app: &AppHandle,
    request: CapsuleHitRegionRequest,
) -> Result<(), String> {
    let window = app
        .get_webview_window(CAPSULE_LABEL)
        .ok_or_else(|| String::from("Failed to locate the lyric capsule window."))?;
    let hwnd_key = capsule_window_key(&window)?;
    let scale_factor = window.scale_factor().unwrap_or(1.0).max(0.1);
    let region = build_mapped_region(&request, scale_factor);
    let states = CAPSULE_MOUSE_MAP_STATES
        .get_or_init(|| Arc::new(Mutex::new(HashMap::new())))
        .clone();

    let should_spawn = {
        let mut states = states
            .lock()
            .map_err(|_| String::from("Lyric capsule mouse map state lock was poisoned."))?;
        match states.get_mut(&hwnd_key) {
            Some(state) => {
                state.region = region;
                state.expanded = request.expanded;
                false
            }
            None => {
                states.insert(
                    hwnd_key,
                    CapsuleMouseMapState {
                        region,
                        expanded: request.expanded,
                        hovering: false,
                        ignore_cursor_events: None,
                    },
                );
                true
            }
        }
    };

    if should_spawn {
        let _ = window.set_ignore_cursor_events(true);
        spawn_capsule_mouse_mapper(app.clone(), hwnd_key, states);
    }

    Ok(())
}

#[cfg(target_os = "windows")]
pub fn clear_capsule_hit_region(app: &AppHandle) -> Result<(), String> {
    let Some(window) = app.get_webview_window(CAPSULE_LABEL) else {
        return Ok(());
    };
    let hwnd_key = capsule_window_key(&window)?;

    if let Some(states) = CAPSULE_MOUSE_MAP_STATES.get() {
        let mut states = states
            .lock()
            .map_err(|_| String::from("Lyric capsule mouse map state lock was poisoned."))?;
        states.remove(&hwnd_key);
    }

    let _ = window.set_ignore_cursor_events(false);
    Ok(())
}

#[cfg(not(target_os = "windows"))]
pub fn apply_capsule_hit_region(
    _app: &AppHandle,
    _request: CapsuleHitRegionRequest,
) -> Result<(), String> {
    Ok(())
}

#[cfg(not(target_os = "windows"))]
pub fn clear_capsule_hit_region(_app: &AppHandle) -> Result<(), String> {
    Ok(())
}

#[cfg(target_os = "windows")]
fn spawn_capsule_mouse_mapper(
    app: AppHandle,
    hwnd_key: isize,
    states: Arc<Mutex<HashMap<isize, CapsuleMouseMapState>>>,
) {
    thread::spawn(move || loop {
        let Some(window) = app.get_webview_window(CAPSULE_LABEL) else {
            remove_mouse_map_state(&states, hwnd_key);
            break;
        };

        let Ok(current_key) = capsule_window_key(&window) else {
            remove_mouse_map_state(&states, hwnd_key);
            break;
        };
        if current_key != hwnd_key {
            remove_mouse_map_state(&states, hwnd_key);
            break;
        }

        let Some((next_ignore, sleep_ms)) = next_ignore_cursor_state(&states, hwnd_key, &window)
        else {
            let _ = window.set_ignore_cursor_events(false);
            break;
        };

        if let Some(ignore) = next_ignore {
            let _ = window.set_ignore_cursor_events(ignore);
        }

        thread::sleep(Duration::from_millis(sleep_ms));
    });
}

#[cfg(target_os = "windows")]
fn next_ignore_cursor_state<R: tauri::Runtime>(
    states: &Arc<Mutex<HashMap<isize, CapsuleMouseMapState>>>,
    hwnd_key: isize,
    window: &tauri::WebviewWindow<R>,
) -> Option<(Option<bool>, u64)> {
    let hwnd = capsule_window_hwnd(window).ok()?;
    let cursor = current_cursor_position()?;
    let window_rect = window_rect(hwnd)?;

    let mut states = states.lock().ok()?;
    let state = states.get_mut(&hwnd_key)?;

    let x = cursor.x.saturating_sub(window_rect.left);
    let y = cursor.y.saturating_sub(window_rect.top);
    let inside_collapsed = is_inside_rounded_region(state.region, x, y, false);
    let inside_expanded = is_inside_rounded_region(state.region, x, y, true);
    state.hovering = inside_collapsed || (state.hovering && inside_expanded);
    let interactive = inside_collapsed || ((state.expanded || state.hovering) && inside_expanded);
    let next_ignore = !interactive;
    let should_update = state.ignore_cursor_events != Some(next_ignore);

    if should_update {
        state.ignore_cursor_events = Some(next_ignore);
    }

    let sleep_ms = if interactive {
        MAPPER_ACTIVE_INTERVAL_MS
    } else {
        MAPPER_IDLE_INTERVAL_MS
    };
    Some((should_update.then_some(next_ignore), sleep_ms))
}

#[cfg(target_os = "windows")]
fn remove_mouse_map_state(
    states: &Arc<Mutex<HashMap<isize, CapsuleMouseMapState>>>,
    hwnd_key: isize,
) {
    if let Ok(mut states) = states.lock() {
        states.remove(&hwnd_key);
    }
}

#[cfg(target_os = "windows")]
fn build_mapped_region(
    request: &CapsuleHitRegionRequest,
    scale_factor: f64,
) -> CapsuleMappedRegion {
    let requested_width = if request.capsule_width.is_finite() {
        request.capsule_width
    } else {
        CAPSULE_MAX_WIDTH
    };
    let region_width = requested_width
        .clamp(CAPSULE_MIN_WIDTH, CAPSULE_MAX_WIDTH)
        .min(WINDOW_WIDTH);
    let left = ((WINDOW_WIDTH - region_width) / 2.0).max(0.0);
    let right = (left + region_width).min(WINDOW_WIDTH);

    CapsuleMappedRegion {
        left: logical_floor(left, scale_factor),
        top: logical_floor(REGION_TOP, scale_factor),
        right: logical_ceil(right, scale_factor),
        collapsed_bottom: logical_ceil(REGION_COLLAPSED_BOTTOM, scale_factor),
        expanded_bottom: logical_ceil(REGION_EXPANDED_BOTTOM, scale_factor),
    }
}

#[cfg(target_os = "windows")]
fn is_inside_rounded_region(region: CapsuleMappedRegion, x: i32, y: i32, expanded: bool) -> bool {
    let bottom = if expanded {
        region.expanded_bottom
    } else {
        region.collapsed_bottom
    };

    if x < region.left || x >= region.right || y < region.top || y >= bottom {
        return false;
    }

    let width = (region.right - region.left).max(1) as f64;
    let height = (bottom - region.top).max(1) as f64;
    let radius = height / 2.0;
    let px = f64::from(x - region.left);
    let py = f64::from(y - region.top);

    if px >= radius && px <= width - radius {
        return true;
    }

    let center_x = if px < radius { radius } else { width - radius };
    let center_y = radius;
    let dx = px - center_x;
    let dy = py - center_y;
    dx * dx + dy * dy <= radius * radius
}

#[cfg(target_os = "windows")]
fn capsule_window_key<R: tauri::Runtime>(
    window: &tauri::WebviewWindow<R>,
) -> Result<isize, String> {
    Ok(capsule_window_hwnd(window)?.0 as isize)
}

#[cfg(target_os = "windows")]
fn capsule_window_hwnd<R: tauri::Runtime>(
    window: &tauri::WebviewWindow<R>,
) -> Result<windows::Win32::Foundation::HWND, String> {
    use raw_window_handle::{HasWindowHandle, RawWindowHandle};
    use windows::Win32::Foundation::HWND;

    let window_handle = window
        .window_handle()
        .map_err(|error| format!("Failed to access the lyric capsule window handle: {error}"))?;
    let hwnd = match window_handle.as_raw() {
        RawWindowHandle::Win32(handle) => HWND(handle.hwnd.get() as *mut std::ffi::c_void),
        _ => {
            return Err(String::from(
                "OFPlayer expected a Win32 window handle for the lyric capsule mouse map.",
            ))
        }
    };

    if hwnd.is_invalid() {
        return Err(String::from("The lyric capsule window handle was invalid."));
    }

    Ok(hwnd)
}

#[cfg(target_os = "windows")]
fn current_cursor_position() -> Option<windows::Win32::Foundation::POINT> {
    use windows::Win32::{Foundation::POINT, UI::WindowsAndMessaging::GetCursorPos};

    let mut point = POINT::default();
    unsafe { GetCursorPos(&mut point).ok()? };
    Some(point)
}

#[cfg(target_os = "windows")]
fn window_rect(hwnd: windows::Win32::Foundation::HWND) -> Option<windows::Win32::Foundation::RECT> {
    use windows::Win32::{Foundation::RECT, UI::WindowsAndMessaging::GetWindowRect};

    let mut rect = RECT::default();
    unsafe { GetWindowRect(hwnd, &mut rect).ok()? };
    Some(rect)
}

#[cfg(target_os = "windows")]
fn logical_floor(value: f64, scale_factor: f64) -> i32 {
    (value * scale_factor).floor().clamp(0.0, i32::MAX as f64) as i32
}

#[cfg(target_os = "windows")]
fn logical_ceil(value: f64, scale_factor: f64) -> i32 {
    (value * scale_factor).ceil().clamp(0.0, i32::MAX as f64) as i32
}
