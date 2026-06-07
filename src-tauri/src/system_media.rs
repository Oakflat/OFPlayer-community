use crate::app_paths;
use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use serde::Serialize;
use std::{ffi::c_void, time::Duration};
use tauri::{AppHandle, Emitter, Manager};

#[cfg(target_os = "windows")]
use base64::{engine::general_purpose::STANDARD as BASE64_STANDARD, Engine as _};
#[cfg(target_os = "windows")]
use image::{codecs::jpeg::JpegEncoder, imageops::FilterType};
#[cfg(target_os = "windows")]
use souvlaki::{
    MediaControlEvent, MediaControls, MediaMetadata, MediaPlayback, MediaPosition, PlatformConfig,
    SeekDirection,
};
#[cfg(target_os = "windows")]
use std::{
    collections::hash_map::DefaultHasher,
    fs,
    hash::{Hash, Hasher},
    path::{Path, PathBuf},
};
#[cfg(target_os = "windows")]
use windows::{
    core::{factory, HSTRING},
    Media::SystemMediaTransportControls,
    Win32::{
        Foundation::HWND,
        Storage::EnhancedStorage::{
            PKEY_AppUserModel_ID, PKEY_AppUserModel_RelaunchDisplayNameResource,
            PKEY_AppUserModel_RelaunchIconResource,
        },
        System::WinRT::ISystemMediaTransportControlsInterop,
        System::{
            Com::{
                CoTaskMemAlloc,
                StructuredStorage::{PropVariantClear, PROPVARIANT},
            },
            Variant::VT_LPWSTR,
        },
        UI::Shell::{
            PropertiesSystem::{IPropertyStore, SHGetPropertyStoreForWindow},
            SetCurrentProcessExplicitAppUserModelID,
        },
    },
};

pub const SYSTEM_MEDIA_CONTROL_EVENT: &str = "playback://system-media-control";
const MEDIA_CONTROL_SEEK_STEP_SECONDS: f64 = 10.0;
#[cfg(target_os = "windows")]
const WINDOWS_APP_USER_MODEL_ID: &str = app_paths::APP_IDENTIFIER;
#[cfg(target_os = "windows")]
const WINDOWS_APP_MEDIA_ID: &str = "OFPlayer";
#[cfg(target_os = "windows")]
const WINDOWS_DISPLAY_NAME: &str = "OFPlayer";
#[cfg(target_os = "windows")]
const WINDOWS_MEDIA_COVER_SIZE: u32 = 512;
#[cfg(target_os = "windows")]
const WINDOWS_MEDIA_COVER_QUALITY: u8 = 88;
#[cfg(target_os = "windows")]
const WINDOWS_MEDIA_CACHE_DIR: &str = "system-media";

#[derive(Clone, Debug, Default, PartialEq)]
pub struct SystemMediaMetadata {
    pub track_id: String,
    pub title: String,
    pub artist: String,
    pub album: String,
    pub cover_url: String,
    pub duration: f64,
}

#[derive(Clone, Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SystemMediaControlPayload {
    pub action: String,
    pub seconds: Option<f64>,
}

#[derive(Default)]
pub struct SystemMediaSession {
    #[cfg(target_os = "windows")]
    controls: Option<MediaControls>,
    #[cfg(target_os = "windows")]
    media_cache_dir: Option<PathBuf>,
    current_metadata: Option<SystemMediaMetadata>,
    current_status: Option<SystemPlaybackStatus>,
    current_position_secs: Option<u64>,
}

impl SystemMediaSession {
    pub fn initialize(&mut self, app: &AppHandle) -> Result<(), String> {
        #[cfg(target_os = "windows")]
        {
            let window = app.get_webview_window("main").ok_or_else(|| {
                String::from("Failed to locate the main window for system media controls.")
            })?;

            let hwnd = extract_window_handle(&window)?;
            configure_windows_media_identity(hwnd)?;
            self.media_cache_dir = app_paths::cache_subdir(WINDOWS_MEDIA_CACHE_DIR).ok();

            let mut controls = MediaControls::new(PlatformConfig {
                display_name: "OFPlayer",
                dbus_name: WINDOWS_APP_USER_MODEL_ID,
                hwnd: Some(hwnd),
            })
            .map_err(|error| {
                format!("Failed to initialize Windows system media controls: {error}")
            })?;

            let app_handle = app.clone();
            controls
                .attach(move |event| {
                    if let Some(payload) = map_media_control_event(event) {
                        let _ = app_handle.emit(SYSTEM_MEDIA_CONTROL_EVENT, payload);
                    }
                })
                .map_err(|error| {
                    format!("Failed to attach Windows system media controls: {error}")
                })?;

            self.controls = Some(controls);
        }

        Ok(())
    }

    pub fn sync(
        &mut self,
        status: SystemPlaybackStatus,
        current_time: f64,
        metadata: Option<&SystemMediaMetadata>,
    ) {
        #[cfg(target_os = "windows")]
        {
            let Some(controls) = self.controls.as_mut() else {
                return;
            };

            let should_refresh_metadata = match (self.current_metadata.as_ref(), metadata) {
                (Some(current), Some(next)) => current != next,
                (None, Some(_)) | (Some(_), None) => true,
                (None, None) => false,
            };

            if should_refresh_metadata {
                let next_metadata = metadata.cloned().unwrap_or_default();
                let resolved_cover_url =
                    resolve_cover_url(&next_metadata, self.media_cache_dir.as_deref());
                let resolved_cover_url =
                    cover_url_option(resolved_cover_url.as_deref().unwrap_or(""));

                let metadata_result =
                    set_controls_metadata(controls, &next_metadata, resolved_cover_url);
                let metadata_applied = metadata_result.is_ok()
                    || resolved_cover_url.is_some()
                        && set_controls_metadata(controls, &next_metadata, None).is_ok();

                if metadata_applied {
                    self.current_metadata = metadata.cloned();
                }
            }

            let safe_current_time = clamp_time(current_time);
            let next_position_secs = match status {
                SystemPlaybackStatus::Playing | SystemPlaybackStatus::Paused => {
                    Some(safe_current_time.floor() as u64)
                }
                SystemPlaybackStatus::Stopped => None,
            };
            let should_refresh_playback = self.current_status != Some(status)
                || self.current_position_secs != next_position_secs;

            if should_refresh_playback {
                let playback = match status {
                    SystemPlaybackStatus::Playing => MediaPlayback::Playing {
                        progress: Some(MediaPosition(Duration::from_secs_f64(safe_current_time))),
                    },
                    SystemPlaybackStatus::Paused => MediaPlayback::Paused {
                        progress: Some(MediaPosition(Duration::from_secs_f64(safe_current_time))),
                    },
                    SystemPlaybackStatus::Stopped => MediaPlayback::Stopped,
                };

                let _ = controls.set_playback(playback);
                self.current_status = Some(status);
                self.current_position_secs = next_position_secs;
            }
        }
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SystemPlaybackStatus {
    Stopped,
    Paused,
    Playing,
}

#[cfg(target_os = "windows")]
fn extract_window_handle<R: tauri::Runtime>(
    window: &tauri::WebviewWindow<R>,
) -> Result<*mut c_void, String> {
    let window_handle = window
        .window_handle()
        .map_err(|error| format!("Failed to access the main window handle: {error}"))?;

    match window_handle.as_raw() {
        RawWindowHandle::Win32(handle) => Ok(handle.hwnd.get() as *mut c_void),
        _ => Err(String::from(
            "OFPlayer expected a Win32 window handle for Windows system media controls.",
        )),
    }
}

#[cfg(target_os = "windows")]
fn configure_windows_media_identity(hwnd: *mut c_void) -> Result<(), String> {
    unsafe {
        SetCurrentProcessExplicitAppUserModelID(&HSTRING::from(WINDOWS_APP_USER_MODEL_ID))
            .map_err(|error| {
                format!(
                    "Failed to register OFPlayer AppUserModelID for Windows media flyouts: {error}"
                )
            })?;
    }

    configure_windows_window_properties(hwnd)?;

    let interop = factory::<SystemMediaTransportControls, ISystemMediaTransportControlsInterop>()
        .map_err(|error| {
        format!("Failed to access Windows media transport interop for OFPlayer: {error}")
    })?;
    let controls: SystemMediaTransportControls = unsafe { interop.GetForWindow(HWND(hwnd)) }
        .map_err(|error| format!("Failed to bind the Windows media flyout to OFPlayer: {error}"))?;
    let display_updater = controls
        .DisplayUpdater()
        .map_err(|error| format!("Failed to access the Windows media display updater: {error}"))?;
    display_updater
        .SetAppMediaId(&HSTRING::from(WINDOWS_APP_MEDIA_ID))
        .map_err(|error| {
            format!("Failed to register the OFPlayer media label with Windows: {error}")
        })?;

    Ok(())
}

#[cfg(target_os = "windows")]
fn configure_windows_window_properties(hwnd: *mut c_void) -> Result<(), String> {
    let store: IPropertyStore =
        unsafe { SHGetPropertyStoreForWindow(HWND(hwnd)) }.map_err(|error| {
            format!("Failed to access the main window property store for OFPlayer: {error}")
        })?;

    let mut app_id = string_propvariant(WINDOWS_APP_USER_MODEL_ID)?;
    let mut display_name = string_propvariant(WINDOWS_DISPLAY_NAME)?;
    let mut icon_resource = string_propvariant(&resolve_relaunch_icon_resource())?;

    let set_result = unsafe {
        store
            .SetValue(&PKEY_AppUserModel_ID, &app_id)
            .and_then(|_| {
                store.SetValue(
                    &PKEY_AppUserModel_RelaunchDisplayNameResource,
                    &display_name,
                )
            })
            .and_then(|_| store.SetValue(&PKEY_AppUserModel_RelaunchIconResource, &icon_resource))
            .and_then(|_| store.Commit())
    };

    unsafe {
        let _ = PropVariantClear(&mut app_id);
        let _ = PropVariantClear(&mut display_name);
        let _ = PropVariantClear(&mut icon_resource);
    }

    set_result.map_err(|error| {
        format!("Failed to register OFPlayer window identity properties for Windows: {error}")
    })
}

#[cfg(target_os = "windows")]
fn string_propvariant(value: &str) -> Result<PROPVARIANT, String> {
    let wide: Vec<u16> = value.encode_utf16().chain(std::iter::once(0)).collect();
    let bytes = wide
        .len()
        .checked_mul(std::mem::size_of::<u16>())
        .ok_or_else(|| String::from("Failed to allocate Windows property string buffer."))?;
    let buffer = unsafe { CoTaskMemAlloc(bytes) } as *mut u16;

    if buffer.is_null() {
        return Err(String::from(
            "Failed to allocate Windows property storage for OFPlayer identity.",
        ));
    }

    unsafe {
        std::ptr::copy_nonoverlapping(wide.as_ptr(), buffer, wide.len());
    }

    let mut propvariant = PROPVARIANT::default();
    unsafe {
        let inner = &mut *propvariant.Anonymous.Anonymous;
        inner.vt = VT_LPWSTR;
        inner.Anonymous.pwszVal = windows::core::PWSTR(buffer);
    }

    Ok(propvariant)
}

#[cfg(target_os = "windows")]
fn resolve_relaunch_icon_resource() -> String {
    #[cfg(debug_assertions)]
    {
        let icon_path = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("icons")
            .join("icon.ico");

        if icon_path.exists() {
            return format!("{},0", icon_path.to_string_lossy());
        }
    }

    std::env::current_exe()
        .ok()
        .map(|path| format!("{},0", path.to_string_lossy()))
        .unwrap_or_else(|| String::from("OFPlayer"))
}

#[cfg(target_os = "windows")]
fn resolve_cover_url(
    metadata: &SystemMediaMetadata,
    media_cache_dir: Option<&Path>,
) -> Option<String> {
    let trimmed = metadata.cover_url.trim();

    if trimmed.is_empty() {
        return None;
    }

    if trimmed.starts_with("data:") {
        return media_cache_dir.and_then(|cache_dir| {
            materialize_embedded_cover(cache_dir, &metadata.track_id, trimmed)
        });
    }

    if trimmed.contains("://") {
        return Some(trimmed.to_string());
    }

    let path = Path::new(trimmed);

    if path.is_absolute() || path.exists() {
        return Some(path_to_file_url(path));
    }

    Some(trimmed.to_string())
}

#[cfg(target_os = "windows")]
fn materialize_embedded_cover(cache_dir: &Path, track_id: &str, data_uri: &str) -> Option<String> {
    let (header, encoded) = data_uri.split_once(',')?;

    if !header.contains(";base64") {
        return None;
    }

    let bytes = BASE64_STANDARD.decode(encoded).ok()?;

    fs::create_dir_all(cache_dir).ok()?;

    let file_name = format!(
        "{}-{}.jpg",
        sanitize_file_component(track_id),
        stable_hash(data_uri)
    );
    let path = cache_dir.join(file_name);

    if !path.exists() {
        let image = image::load_from_memory(&bytes).ok()?;
        let cover = image.resize_to_fill(
            WINDOWS_MEDIA_COVER_SIZE,
            WINDOWS_MEDIA_COVER_SIZE,
            FilterType::Triangle,
        );
        let mut encoded_cover = Vec::new();
        let mut encoder =
            JpegEncoder::new_with_quality(&mut encoded_cover, WINDOWS_MEDIA_COVER_QUALITY);
        encoder.encode_image(&cover).ok()?;
        fs::write(&path, encoded_cover).ok()?;
    }

    Some(path_to_file_url(&path))
}

#[cfg(target_os = "windows")]
fn sanitize_file_component(value: &str) -> String {
    let sanitized: String = value
        .chars()
        .map(|character| {
            if character.is_ascii_alphanumeric() || character == '-' || character == '_' {
                character
            } else {
                '_'
            }
        })
        .collect();

    if sanitized.is_empty() {
        String::from("track")
    } else {
        sanitized
    }
}

#[cfg(target_os = "windows")]
fn stable_hash(value: &str) -> String {
    let mut hasher = DefaultHasher::new();
    value.hash(&mut hasher);
    format!("{:016x}", hasher.finish())
}

#[cfg(target_os = "windows")]
fn path_to_file_url(path: &Path) -> String {
    format!("file://{}", path.to_string_lossy())
}

#[cfg(target_os = "windows")]
fn map_media_control_event(event: MediaControlEvent) -> Option<SystemMediaControlPayload> {
    match event {
        MediaControlEvent::Play => Some(SystemMediaControlPayload {
            action: String::from("play"),
            seconds: None,
        }),
        MediaControlEvent::Pause => Some(SystemMediaControlPayload {
            action: String::from("pause"),
            seconds: None,
        }),
        MediaControlEvent::Toggle => Some(SystemMediaControlPayload {
            action: String::from("toggle"),
            seconds: None,
        }),
        MediaControlEvent::Next => Some(SystemMediaControlPayload {
            action: String::from("next"),
            seconds: None,
        }),
        MediaControlEvent::Previous => Some(SystemMediaControlPayload {
            action: String::from("previous"),
            seconds: None,
        }),
        MediaControlEvent::Stop => Some(SystemMediaControlPayload {
            action: String::from("stop"),
            seconds: None,
        }),
        MediaControlEvent::SetPosition(position) => Some(SystemMediaControlPayload {
            action: String::from("seekTo"),
            seconds: Some(position.0.as_secs_f64()),
        }),
        MediaControlEvent::Seek(direction) => Some(SystemMediaControlPayload {
            action: String::from("seekBy"),
            seconds: Some(match direction {
                SeekDirection::Forward => MEDIA_CONTROL_SEEK_STEP_SECONDS,
                SeekDirection::Backward => -MEDIA_CONTROL_SEEK_STEP_SECONDS,
            }),
        }),
        MediaControlEvent::SeekBy(direction, amount) => Some(SystemMediaControlPayload {
            action: String::from("seekBy"),
            seconds: Some(match direction {
                SeekDirection::Forward => amount.as_secs_f64(),
                SeekDirection::Backward => -amount.as_secs_f64(),
            }),
        }),
        _ => None,
    }
}

fn text_option(value: &str) -> Option<&str> {
    let trimmed = value.trim();

    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed)
    }
}

fn cover_url_option(value: &str) -> Option<&str> {
    let trimmed = value.trim();

    if trimmed.is_empty() || trimmed.starts_with("data:") {
        None
    } else {
        Some(trimmed)
    }
}

#[cfg(target_os = "windows")]
fn set_controls_metadata(
    controls: &mut MediaControls,
    metadata: &SystemMediaMetadata,
    cover_url: Option<&str>,
) -> Result<(), souvlaki::Error> {
    controls.set_metadata(MediaMetadata {
        title: text_option(&metadata.title),
        artist: text_option(&metadata.artist),
        album: text_option(&metadata.album),
        cover_url,
        duration: duration_option(metadata.duration),
    })
}

fn duration_option(value: f64) -> Option<Duration> {
    let safe_duration = clamp_time(value);

    if safe_duration > 0.0 {
        Some(Duration::from_secs_f64(safe_duration))
    } else {
        None
    }
}

fn clamp_time(value: f64) -> f64 {
    if value.is_finite() && value >= 0.0 {
        value
    } else {
        0.0
    }
}
