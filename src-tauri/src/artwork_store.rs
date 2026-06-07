use crate::app_paths;
use base64::{engine::general_purpose::STANDARD as BASE64_STANDARD, Engine as _};
use std::{fs, path::PathBuf};

pub(crate) const TRACK_ARTWORK_ASSET_DIR_NAME: &str = "track-artwork";

#[derive(Debug, Clone)]
pub(crate) struct StoredTrackArtwork {
    pub(crate) artwork_text: String,
    pub(crate) artwork_path: String,
    pub(crate) mime_type: String,
    pub(crate) content_hash: String,
    pub(crate) byte_length: i64,
}

pub(crate) fn track_artwork_asset_dir() -> Result<PathBuf, String> {
    app_paths::ensure_writable_directory(&app_paths::data_dir()?.join(TRACK_ARTWORK_ASSET_DIR_NAME))
}

pub(crate) fn store_track_artwork(value: &str) -> Result<Option<StoredTrackArtwork>, String> {
    let artwork = value.trim();

    if artwork.is_empty() {
        return Ok(None);
    }

    let Some(data_url) = parse_base64_data_url(artwork) else {
        return Ok(Some(StoredTrackArtwork {
            artwork_text: artwork.to_string(),
            artwork_path: String::new(),
            mime_type: String::new(),
            content_hash: String::new(),
            byte_length: i64::try_from(artwork.len()).unwrap_or(i64::MAX),
        }));
    };

    let bytes = BASE64_STANDARD
        .decode(data_url.payload)
        .map_err(|error| format!("Failed to decode embedded track artwork: {error}"))?;
    let content_hash = fnv1a64_hex(&bytes);
    let byte_length = i64::try_from(bytes.len()).unwrap_or(i64::MAX);
    let extension = image_extension_for_mime(&data_url.mime_type);
    let file_name = format!("{content_hash}-{}.{}", bytes.len(), extension);
    let path = track_artwork_asset_dir()?.join(file_name);

    if !path.is_file() {
        fs::write(&path, &bytes).map_err(|error| {
            format!(
                "Failed to write track artwork asset '{}': {error}",
                path.display()
            )
        })?;
    }

    Ok(Some(StoredTrackArtwork {
        artwork_text: String::new(),
        artwork_path: path.display().to_string(),
        mime_type: data_url.mime_type,
        content_hash,
        byte_length,
    }))
}

pub(crate) fn resolve_stored_track_artwork(artwork_text: String, artwork_path: String) -> String {
    let path = artwork_path.trim();

    if !path.is_empty() {
        return path.to_string();
    }

    artwork_text.trim().to_string()
}

struct ParsedDataUrl<'a> {
    mime_type: String,
    payload: &'a str,
}

fn parse_base64_data_url(value: &str) -> Option<ParsedDataUrl<'_>> {
    let (metadata, payload) = value.split_once(',')?;
    let metadata_lower = metadata.to_ascii_lowercase();

    if !metadata_lower.starts_with("data:") || !metadata_lower.contains(";base64") {
        return None;
    }

    let mime_type = metadata
        .strip_prefix("data:")
        .unwrap_or_default()
        .split(';')
        .next()
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .unwrap_or("image/jpeg")
        .to_ascii_lowercase();
    let payload = payload.trim();

    if payload.is_empty() {
        return None;
    }

    Some(ParsedDataUrl { mime_type, payload })
}

fn image_extension_for_mime(mime_type: &str) -> &'static str {
    match mime_type.trim().to_ascii_lowercase().as_str() {
        "image/png" => "png",
        "image/webp" => "webp",
        "image/gif" => "gif",
        "image/bmp" | "image/x-ms-bmp" => "bmp",
        "image/jpeg" | "image/jpg" | "image/pjpeg" => "jpg",
        _ => "img",
    }
}

fn fnv1a64_hex(bytes: &[u8]) -> String {
    let mut hash = 0xcbf29ce484222325u64;

    for byte in bytes {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(0x100000001b3);
    }

    format!("{hash:016x}")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_base64_data_url_metadata() {
        let parsed = parse_base64_data_url("data:image/png;base64,SGVsbG8=").unwrap();

        assert_eq!(parsed.mime_type, "image/png");
        assert_eq!(parsed.payload, "SGVsbG8=");
    }

    #[test]
    fn ignores_non_base64_artwork_urls() {
        assert!(parse_base64_data_url("https://example.test/cover.jpg").is_none());
    }
}
