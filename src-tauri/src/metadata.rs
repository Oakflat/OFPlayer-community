use crate::{audio_formats, dsd_playback};
use base64::{engine::general_purpose::STANDARD as BASE64_STANDARD, Engine as _};
use dsd_reader::DsdReader;
use lofty::{
    picture::PictureType,
    prelude::{Accessor, AudioFile, ItemKey, TaggedFileExt},
    probe::Probe,
};
use serde::{Deserialize, Serialize};
use std::{
    fs,
    path::{Path, PathBuf},
};

const CURRENT_TRACK_METADATA_VERSION: u32 = 3;

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ParseAudioMetadataRequest {
    pub path: String,
    pub file_name: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MetadataParseError {
    pub code: String,
    pub message: String,
    pub path: String,
    pub file_name: String,
    pub source: String,
    pub recoverable: bool,
}

pub type MetadataParseResult<T> = Result<T, Box<MetadataParseError>>;

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ParsedAudioMetadata {
    pub title: String,
    pub artist: String,
    pub album_artist: String,
    pub album: String,
    pub genre: String,
    pub year: u32,
    pub track_number: u32,
    pub track_total: u32,
    pub disc_number: u32,
    pub disc_total: u32,
    pub composer: String,
    pub lyricist: String,
    pub comment: String,
    pub duration: f64,
    pub file_size: u64,
    pub format: String,
    pub bitrate: u32,
    pub sample_rate: u32,
    pub bit_depth: u32,
    pub artwork: String,
    pub metadata_version: u32,
}

pub fn parse_audio_metadata(
    request: ParseAudioMetadataRequest,
) -> MetadataParseResult<ParsedAudioMetadata> {
    let path = PathBuf::from(request.path.trim());
    let file_name = resolve_request_file_name(&path, request.file_name);

    if !path.is_file() {
        return Err(MetadataParseError::new(
            "metadata_file_unavailable",
            "The audio file is no longer available.",
            &path,
            &file_name,
            format!(
                "The audio file '{}' is not available anymore.",
                path.display()
            ),
            false,
        ));
    }

    if audio_formats::is_dsd_audio_path(&path) {
        return parse_dsd_audio_metadata(&path, &file_name);
    }

    let tagged_file = Probe::open(&path)
        .map_err(|error| {
            MetadataParseError::new(
                "metadata_open_failed",
                "Failed to open the audio file for metadata parsing.",
                &path,
                &file_name,
                error.to_string(),
                false,
            )
        })?
        .guess_file_type()
        .map_err(|error| {
            MetadataParseError::new(
                "metadata_format_detect_failed",
                "Failed to detect the audio format.",
                &path,
                &file_name,
                error.to_string(),
                true,
            )
        })?
        .read()
        .map_err(|error| {
            MetadataParseError::new(
                "metadata_read_failed",
                "Failed to read audio metadata.",
                &path,
                &file_name,
                error.to_string(),
                true,
            )
        })?;

    let properties = tagged_file.properties();
    let tag = tagged_file
        .primary_tag()
        .or_else(|| tagged_file.first_tag());
    let title = sanitize_title(resolve_title(tag, &file_name));
    let artist = resolve_artist(tag);

    Ok(ParsedAudioMetadata {
        title,
        artist,
        album_artist: resolve_album_artist(tag),
        album: resolve_album(tag),
        genre: resolve_genre(tag),
        year: resolve_year(tag),
        track_number: tag.and_then(|value| value.track()).unwrap_or_default(),
        track_total: tag
            .and_then(|value| value.track_total())
            .unwrap_or_default(),
        disc_number: tag.and_then(|value| value.disk()).unwrap_or_default(),
        disc_total: tag.and_then(|value| value.disk_total()).unwrap_or_default(),
        composer: resolve_joined_strings(tag, &[ItemKey::Composer]),
        lyricist: resolve_joined_strings(tag, &[ItemKey::Lyricist]),
        comment: resolve_comment(tag),
        duration: properties.duration().as_secs_f64(),
        file_size: fs::metadata(&path)
            .map(|metadata| metadata.len())
            .unwrap_or_default(),
        format: resolve_format(&path),
        bitrate: resolve_bitrate(properties),
        sample_rate: properties.sample_rate().unwrap_or_default(),
        bit_depth: properties.bit_depth().map(u32::from).unwrap_or_default(),
        artwork: resolve_artwork(tag),
        metadata_version: CURRENT_TRACK_METADATA_VERSION,
    })
}

fn parse_dsd_audio_metadata(
    path: &Path,
    file_name: &str,
) -> MetadataParseResult<ParsedAudioMetadata> {
    let reader = DsdReader::from_container(path.to_path_buf()).map_err(|error| {
        MetadataParseError::new(
            "metadata_read_failed",
            "Failed to read DSD metadata.",
            path,
            file_name,
            error.to_string(),
            true,
        )
    })?;
    let channels = u16::try_from(reader.channels_num()).map_err(|_| {
        MetadataParseError::new(
            "metadata_read_failed",
            "Failed to read DSD metadata.",
            path,
            file_name,
            String::from("DSD channel count is too large."),
            true,
        )
    })?;
    let sample_rate =
        dsd_playback::dsd_sample_rate_from_multiplier(reader.dsd_rate()).map_err(|error| {
            MetadataParseError::new(
                "metadata_read_failed",
                "Failed to read DSD metadata.",
                path,
                file_name,
                error,
                true,
            )
        })?;
    let duration = dsd_playback::dsd_duration_seconds(reader.audio_length(), channels, sample_rate);
    let bitrate = sample_rate.saturating_mul(u32::from(channels));

    Ok(ParsedAudioMetadata {
        title: sanitize_title(path_stem(file_name).unwrap_or_else(|| String::from("Untitled"))),
        artist: String::new(),
        album_artist: String::new(),
        album: String::new(),
        genre: String::new(),
        year: 0,
        track_number: 0,
        track_total: 0,
        disc_number: 0,
        disc_total: 0,
        composer: String::new(),
        lyricist: String::new(),
        comment: String::new(),
        duration,
        file_size: fs::metadata(path)
            .map(|metadata| metadata.len())
            .unwrap_or_default(),
        format: resolve_format(path),
        bitrate,
        sample_rate,
        bit_depth: 1,
        artwork: String::new(),
        metadata_version: CURRENT_TRACK_METADATA_VERSION,
    })
}

impl MetadataParseError {
    fn new(
        code: &str,
        message: &str,
        path: &Path,
        file_name: &str,
        source: String,
        recoverable: bool,
    ) -> Box<Self> {
        Box::new(Self {
            code: String::from(code),
            message: String::from(message),
            path: path.display().to_string(),
            file_name: String::from(file_name),
            source,
            recoverable,
        })
    }
}

fn resolve_request_file_name(path: &Path, file_name: Option<String>) -> String {
    file_name
        .filter(|value| !value.trim().is_empty())
        .or_else(|| {
            path.file_name()
                .map(|value| value.to_string_lossy().to_string())
        })
        .unwrap_or_else(|| String::from("track"))
}

pub fn create_fallback_audio_metadata(path: &Path, file_name: Option<&str>) -> ParsedAudioMetadata {
    let file_name = file_name
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .map(String::from)
        .or_else(|| {
            path.file_name()
                .map(|value| value.to_string_lossy().to_string())
        })
        .unwrap_or_else(|| String::from("track"));
    let title = sanitize_title(path_stem(&file_name).unwrap_or_else(|| String::from("Untitled")));

    ParsedAudioMetadata {
        title,
        artist: String::new(),
        album_artist: String::new(),
        album: String::new(),
        genre: String::new(),
        year: 0,
        track_number: 0,
        track_total: 0,
        disc_number: 0,
        disc_total: 0,
        composer: String::new(),
        lyricist: String::new(),
        comment: String::new(),
        duration: 0.0,
        file_size: fs::metadata(path)
            .map(|metadata| metadata.len())
            .unwrap_or_default(),
        format: resolve_format(path),
        bitrate: 0,
        sample_rate: 0,
        bit_depth: 0,
        artwork: String::new(),
        metadata_version: 0,
    }
}

fn resolve_title(tag: Option<&lofty::tag::Tag>, file_name: &str) -> String {
    let title = tag
        .and_then(|value| value.title())
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty())
        .or_else(|| path_stem(file_name));

    title.unwrap_or_else(|| String::from("Untitled"))
}

fn resolve_artist(tag: Option<&lofty::tag::Tag>) -> String {
    resolve_joined_strings(
        tag,
        &[
            ItemKey::TrackArtist,
            ItemKey::TrackArtists,
            ItemKey::AlbumArtist,
            ItemKey::AlbumArtists,
        ],
    )
}

fn resolve_album_artist(tag: Option<&lofty::tag::Tag>) -> String {
    resolve_joined_strings(tag, &[ItemKey::AlbumArtist, ItemKey::AlbumArtists])
}

fn resolve_album(tag: Option<&lofty::tag::Tag>) -> String {
    tag.and_then(|value| value.album())
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty())
        .unwrap_or_default()
}

fn resolve_genre(tag: Option<&lofty::tag::Tag>) -> String {
    tag.and_then(|value| value.genre())
        .map(|value| value.trim().to_string())
        .filter(|value| !value.is_empty())
        .unwrap_or_default()
}

fn resolve_year(tag: Option<&lofty::tag::Tag>) -> u32 {
    tag.and_then(|value| value.date())
        .map(|value| u32::from(value.year))
        .unwrap_or_default()
}

fn resolve_comment(tag: Option<&lofty::tag::Tag>) -> String {
    resolve_joined_strings(tag, &[ItemKey::Comment])
}

fn resolve_joined_strings(tag: Option<&lofty::tag::Tag>, keys: &[ItemKey]) -> String {
    let Some(tag) = tag else {
        return String::new();
    };

    let mut values = Vec::new();

    for key in keys {
        for value in tag.get_strings(*key) {
            let trimmed = value.trim();

            if !trimmed.is_empty() && !values.iter().any(|existing| existing == trimmed) {
                values.push(trimmed.to_string());
            }
        }
    }

    values.join(", ")
}

fn resolve_bitrate(properties: &lofty::properties::FileProperties) -> u32 {
    properties
        .audio_bitrate()
        .or_else(|| properties.overall_bitrate())
        .map(|value| value.saturating_mul(1_000))
        .unwrap_or_default()
}

fn resolve_artwork(tag: Option<&lofty::tag::Tag>) -> String {
    let Some(tag) = tag else {
        return String::new();
    };

    let picture = tag
        .pictures()
        .iter()
        .find(|picture| picture.pic_type() == PictureType::CoverFront)
        .or_else(|| tag.pictures().first());

    let Some(picture) = picture else {
        return String::new();
    };

    let mime_type = picture
        .mime_type()
        .map(|value| value.as_str())
        .unwrap_or("image/jpeg");
    let encoded = BASE64_STANDARD.encode(picture.data());

    format!("data:{mime_type};base64,{encoded}")
}

fn resolve_format(path: &Path) -> String {
    path.extension()
        .map(|value| value.to_string_lossy().trim().to_uppercase())
        .filter(|value| !value.is_empty())
        .unwrap_or_default()
}

fn path_stem(file_name: &str) -> Option<String> {
    let stem = file_name
        .rsplit_once('.')
        .map(|(value, _)| value)
        .unwrap_or(file_name)
        .trim();

    if stem.is_empty() {
        None
    } else {
        Some(stem.to_string())
    }
}

fn sanitize_title(value: String) -> String {
    let trimmed = value.trim();

    if trimmed.is_empty() {
        String::from("Untitled")
    } else {
        trimmed.to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::env;
    use uuid::Uuid;

    #[test]
    fn parse_audio_metadata_returns_structured_errors() {
        let path = env::temp_dir().join(format!("ofplayer-missing-audio-{}.wav", Uuid::new_v4()));

        let error = parse_audio_metadata(ParseAudioMetadataRequest {
            path: path.to_string_lossy().to_string(),
            file_name: Some(String::from("Missing.wav")),
        })
        .expect_err("missing files should return a structured metadata error");

        assert_eq!(error.code, "metadata_file_unavailable");
        assert_eq!(error.file_name, "Missing.wav");
        assert_eq!(error.path, path.display().to_string());
        assert!(!error.recoverable);
        assert!(error.source.contains("not available"));
    }
}
