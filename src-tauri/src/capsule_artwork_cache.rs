use crate::app_paths;
use base64::{engine::general_purpose::STANDARD as BASE64_STANDARD, Engine as _};
use image::{codecs::jpeg::JpegEncoder, imageops::FilterType};
use serde::Serialize;
use std::{
    collections::{hash_map::DefaultHasher, VecDeque},
    fs,
    hash::{Hash, Hasher},
    path::{Path, PathBuf},
    time::Instant,
};
use tauri::{AppHandle, Manager};

const CAPSULE_ARTWORK_CACHE_DIR: &str = "lyric-capsule-artwork";
const CAPSULE_ARTWORK_SIZE: u32 = 128;
const CAPSULE_ARTWORK_QUALITY: u8 = 82;
const CAPSULE_ARTWORK_MAX_ENTRIES: usize = 96;
const CAPSULE_ARTWORK_MAX_SRC_LENGTH: usize = 1024;

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CapsuleArtworkRef {
    pub artwork_key: Option<String>,
    pub artwork_src: Option<String>,
    pub cache_ms: u64,
    pub cache_miss: bool,
}

#[derive(Debug, Default)]
pub struct CapsuleArtworkCache {
    recent_keys: VecDeque<String>,
}

impl CapsuleArtworkCache {
    pub fn resolve(
        &mut self,
        app: &AppHandle,
        track_id: Option<&str>,
        artwork: Option<&str>,
    ) -> CapsuleArtworkRef {
        let started_at = Instant::now();
        let Some(artwork) = artwork.map(str::trim).filter(|value| !value.is_empty()) else {
            return CapsuleArtworkRef {
                cache_ms: elapsed_ms(started_at),
                ..CapsuleArtworkRef::default()
            };
        };

        if artwork.starts_with("data:image/") {
            return self
                .resolve_data_url(app, track_id, artwork, started_at)
                .unwrap_or_else(|| CapsuleArtworkRef {
                    cache_ms: elapsed_ms(started_at),
                    cache_miss: true,
                    ..CapsuleArtworkRef::default()
                });
        }

        if artwork.len() > CAPSULE_ARTWORK_MAX_SRC_LENGTH {
            return CapsuleArtworkRef {
                cache_ms: elapsed_ms(started_at),
                cache_miss: true,
                ..CapsuleArtworkRef::default()
            };
        }

        let artwork_key = format!("external-{}", stable_hash(artwork));
        CapsuleArtworkRef {
            artwork_key: Some(artwork_key),
            artwork_src: Some(normalize_external_artwork_src(artwork)),
            cache_ms: elapsed_ms(started_at),
            cache_miss: false,
        }
    }

    fn resolve_data_url(
        &mut self,
        app: &AppHandle,
        track_id: Option<&str>,
        artwork: &str,
        started_at: Instant,
    ) -> Option<CapsuleArtworkRef> {
        let (_, encoded) = artwork.split_once(',')?;
        let bytes = BASE64_STANDARD.decode(encoded).ok()?;
        let artwork_hash = stable_hash(artwork);
        let safe_track_id = sanitize_file_component(track_id.unwrap_or("track"));
        let artwork_key = format!("{safe_track_id}-{artwork_hash}");
        let cache_dir = app_paths::cache_subdir(CAPSULE_ARTWORK_CACHE_DIR).ok()?;
        let _ = app.asset_protocol_scope().allow_directory(&cache_dir, true);

        fs::create_dir_all(&cache_dir).ok()?;

        let thumbnail_path = cache_dir.join(format!("{artwork_key}.jpg"));
        let cache_miss = !thumbnail_path.exists();

        if cache_miss {
            let image = image::load_from_memory(&bytes).ok()?;
            let thumbnail = image.resize_to_fill(
                CAPSULE_ARTWORK_SIZE,
                CAPSULE_ARTWORK_SIZE,
                FilterType::Triangle,
            );
            let mut encoded_thumbnail = Vec::new();
            let mut encoder =
                JpegEncoder::new_with_quality(&mut encoded_thumbnail, CAPSULE_ARTWORK_QUALITY);
            encoder.encode_image(&thumbnail).ok()?;
            fs::write(&thumbnail_path, encoded_thumbnail).ok()?;
        }

        self.remember_key(&cache_dir, &artwork_key);

        Some(CapsuleArtworkRef {
            artwork_key: Some(artwork_key),
            artwork_src: Some(path_to_file_url(&thumbnail_path)),
            cache_ms: elapsed_ms(started_at),
            cache_miss,
        })
    }

    fn remember_key(&mut self, cache_dir: &Path, artwork_key: &str) {
        if self.recent_keys.iter().any(|key| key == artwork_key) {
            return;
        }

        self.recent_keys.push_back(String::from(artwork_key));

        while self.recent_keys.len() > CAPSULE_ARTWORK_MAX_ENTRIES {
            if let Some(stale_key) = self.recent_keys.pop_front() {
                let _ = fs::remove_file(cache_dir.join(format!("{stale_key}.jpg")));
            }
        }
    }
}

fn normalize_external_artwork_src(artwork: &str) -> String {
    let path = PathBuf::from(artwork);

    if path.is_absolute() || path.exists() {
        return path_to_file_url(&path);
    }

    String::from(artwork)
}

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

fn stable_hash(value: &str) -> String {
    let mut hasher = DefaultHasher::new();
    value.hash(&mut hasher);
    format!("{:016x}", hasher.finish())
}

fn path_to_file_url(path: &Path) -> String {
    path.to_string_lossy().to_string()
}

fn elapsed_ms(started_at: Instant) -> u64 {
    started_at.elapsed().as_millis().min(u128::from(u64::MAX)) as u64
}
