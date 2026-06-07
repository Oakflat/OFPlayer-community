use lofty::{
    id3::v2::{Frame, Id3v2Tag, SyncTextContentType, SynchronizedTextFrame, TimestampFormat},
    prelude::{ItemKey, TaggedFileExt},
    probe::Probe,
    tag::TagType,
};
use serde::{Deserialize, Serialize};
use std::{
    collections::{HashSet, VecDeque},
    env, fs,
    path::{Path, PathBuf},
};

const CURRENT_LYRICS_METADATA_VERSION: u32 = 1;
const SIDECAR_EXTENSIONS: &[&str] = &["lrc", "txt"];
const SIDECAR_SEARCH_ANCESTOR_DEPTH: usize = 2;
const BILINGUAL_TIME_TOLERANCE_SECONDS: f64 = 0.08;
const SIDECAR_DIRECTORY_HINTS: &[&str] = &[
    "Lyrics", "lyrics", "Lyric", "lyric", "LRC", "lrc", "歌词", "歌詞",
];

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum LyricsStatus {
    Missing,
    Resolved,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum LyricsSourceKind {
    SidecarLrc,
    SidecarText,
    EmbeddedSynced,
    EmbeddedText,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "kebab-case")]
pub enum LyricsContentKind {
    Synced,
    Unsynced,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LyricsLine {
    pub index: usize,
    pub text: String,
    pub start_time: Option<f64>,
    pub end_time: Option<f64>,
    pub translated_lyric: String,
    pub roman_lyric: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResolvedTrackLyrics {
    pub track_id: Option<String>,
    pub audio_path: String,
    pub status: LyricsStatus,
    pub source: Option<LyricsSourceKind>,
    pub source_path: Option<String>,
    pub kind: Option<LyricsContentKind>,
    pub text: String,
    pub lines: Vec<LyricsLine>,
    pub title: String,
    pub artist: String,
    pub album: String,
    pub by: String,
    pub language: String,
    pub offset_ms: i64,
    pub active_line_index: Option<usize>,
    pub metadata_version: u32,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ResolveTrackLyricsRequest {
    pub track_id: Option<String>,
    pub audio_path: String,
    pub origin_path: Option<String>,
    pub title: Option<String>,
    pub artist: Option<String>,
    pub album: Option<String>,
    pub file_name: Option<String>,
    pub lyrics_path: Option<String>,
    pub lyrics_directories: Option<Vec<String>>,
    pub position_seconds: Option<f64>,
}

#[derive(Debug, Clone)]
struct LyricsCandidate {
    source: LyricsSourceKind,
    source_path: Option<String>,
    kind: LyricsContentKind,
    text: String,
    lines: Vec<LyricsLine>,
    title: String,
    artist: String,
    album: String,
    by: String,
    language: String,
    offset_ms: i64,
    match_score: u16,
}

#[derive(Debug, Clone, Default)]
struct LyricsLookupHints {
    origin_path: Option<PathBuf>,
    title: String,
    artist: String,
    album: String,
    file_name: String,
    lyrics_path: Option<PathBuf>,
    lyrics_directories: Vec<PathBuf>,
}

#[derive(Debug, Clone)]
struct SidecarSearchDirectory {
    path: PathBuf,
    rank: u8,
    recursive: bool,
}

#[derive(Debug, Default)]
struct ParsedLyricsText {
    title: String,
    artist: String,
    album: String,
    by: String,
    language: String,
    offset_ms: i64,
    timed_entries: Vec<TimedLyricEntry>,
    display_lines: Vec<String>,
}

#[derive(Debug, Clone)]
struct TimedLyricEntry {
    start_time: f64,
    text: String,
    original_index: usize,
}

pub fn resolve_track_lyrics(
    request: ResolveTrackLyricsRequest,
) -> Result<ResolvedTrackLyrics, String> {
    let audio_path = PathBuf::from(request.audio_path.trim());
    let lookup_hints = create_lookup_hints(&request);

    if !audio_path.is_file() {
        return Err(format!(
            "The audio file '{}' is not available for lyric parsing.",
            audio_path.display()
        ));
    }

    let mut candidates = collect_sidecar_candidates(&audio_path, &lookup_hints)?;
    candidates.extend(collect_embedded_candidates(&audio_path)?);

    let selected_candidate = select_best_candidate(candidates);
    let normalized_audio_path = audio_path.to_string_lossy().to_string();
    let position_seconds = sanitize_position_seconds(request.position_seconds);

    Ok(match selected_candidate {
        Some(candidate) => {
            let active_line_index = position_seconds
                .and_then(|seconds| find_active_line_index(&candidate.lines, seconds));

            ResolvedTrackLyrics {
                track_id: request.track_id,
                audio_path: normalized_audio_path,
                status: LyricsStatus::Resolved,
                source: Some(candidate.source),
                source_path: candidate.source_path,
                kind: Some(candidate.kind),
                text: candidate.text,
                lines: candidate.lines,
                title: candidate.title,
                artist: candidate.artist,
                album: candidate.album,
                by: candidate.by,
                language: candidate.language,
                offset_ms: candidate.offset_ms,
                active_line_index,
                metadata_version: CURRENT_LYRICS_METADATA_VERSION,
            }
        }
        None => ResolvedTrackLyrics {
            track_id: request.track_id,
            audio_path: normalized_audio_path,
            status: LyricsStatus::Missing,
            source: None,
            source_path: None,
            kind: None,
            text: String::new(),
            lines: Vec::new(),
            title: String::new(),
            artist: String::new(),
            album: String::new(),
            by: String::new(),
            language: String::new(),
            offset_ms: 0,
            active_line_index: None,
            metadata_version: CURRENT_LYRICS_METADATA_VERSION,
        },
    })
}

fn create_lookup_hints(request: &ResolveTrackLyricsRequest) -> LyricsLookupHints {
    LyricsLookupHints {
        origin_path: request
            .origin_path
            .as_deref()
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(PathBuf::from)
            .filter(|path| path.is_file()),
        title: normalize_lookup_text(request.title.as_deref()),
        artist: normalize_lookup_text(request.artist.as_deref()),
        album: normalize_lookup_text(request.album.as_deref()),
        file_name: normalize_lookup_text(request.file_name.as_deref()),
        lyrics_path: request
            .lyrics_path
            .as_deref()
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(PathBuf::from)
            .filter(|path| path.is_file()),
        lyrics_directories: request
            .lyrics_directories
            .clone()
            .unwrap_or_default()
            .into_iter()
            .map(|value| value.trim().to_string())
            .filter(|value| !value.is_empty())
            .map(PathBuf::from)
            .filter(|path| path.is_dir())
            .collect(),
    }
}

fn normalize_lookup_text(value: Option<&str>) -> String {
    value.map(str::trim).unwrap_or_default().to_string()
}

fn collect_sidecar_candidates(
    audio_path: &Path,
    lookup_hints: &LyricsLookupHints,
) -> Result<Vec<LyricsCandidate>, String> {
    let mut candidates = Vec::new();

    if let Some(explicit_path) = lookup_hints.lyrics_path.as_deref() {
        if is_supported_sidecar_file(explicit_path) {
            let text = read_text_file(explicit_path)?;
            let source = detect_sidecar_source_kind(explicit_path);

            if let Some(candidate) = build_candidate_from_text(
                &text,
                source,
                Some(explicit_path.to_string_lossy().to_string()),
                u16::MAX,
            ) {
                candidates.push(candidate);
            }
        }
    }

    for sidecar_path in collect_sidecar_paths(audio_path, lookup_hints)? {
        if lookup_hints
            .lyrics_path
            .as_deref()
            .is_some_and(|lyrics_path| lyrics_path == sidecar_path)
        {
            continue;
        }

        let text = read_text_file(&sidecar_path)?;
        let source = detect_sidecar_source_kind(&sidecar_path);
        let match_score = score_sidecar_path(audio_path, &sidecar_path, lookup_hints);

        if let Some(candidate) = build_candidate_from_text(
            &text,
            source,
            Some(sidecar_path.to_string_lossy().to_string()),
            match_score,
        ) {
            candidates.push(candidate);
        }
    }

    Ok(candidates)
}

fn collect_sidecar_paths(
    audio_path: &Path,
    lookup_hints: &LyricsLookupHints,
) -> Result<Vec<PathBuf>, String> {
    let mut sidecar_paths = Vec::new();
    let mut seen_paths = HashSet::new();
    let search_directories = collect_sidecar_search_directories(audio_path, lookup_hints);

    for directory in &search_directories {
        collect_sidecar_paths_from_directory(
            audio_path,
            lookup_hints,
            directory,
            &mut sidecar_paths,
            &mut seen_paths,
        )?;
    }

    sidecar_paths.sort_by(|left, right| {
        score_sidecar_path(audio_path, right, lookup_hints)
            .cmp(&score_sidecar_path(audio_path, left, lookup_hints))
            .then_with(|| sidecar_priority(left).0.cmp(&sidecar_priority(right).0))
            .then_with(|| left.cmp(right))
    });
    Ok(sidecar_paths)
}

fn collect_sidecar_search_directories(
    audio_path: &Path,
    lookup_hints: &LyricsLookupHints,
) -> Vec<SidecarSearchDirectory> {
    let mut directories = Vec::new();
    let has_distinct_origin = lookup_hints
        .origin_path
        .as_deref()
        .is_some_and(|origin_path| origin_path != audio_path);

    if let Some(origin_path) = lookup_hints.origin_path.as_deref() {
        collect_sidecar_search_scope(&mut directories, origin_path, 0);
    }

    collect_sidecar_search_scope(
        &mut directories,
        audio_path,
        if has_distinct_origin { 4 } else { 0 },
    );

    for path in &lookup_hints.lyrics_directories {
        push_sidecar_search_directory(&mut directories, path.clone(), 8, true);
    }

    for path in collect_common_lyrics_directories() {
        push_sidecar_search_directory(&mut directories, path.clone(), 12, false);

        for directory_name in SIDECAR_DIRECTORY_HINTS {
            push_sidecar_search_directory(&mut directories, path.join(directory_name), 13, false);
        }
    }

    directories.sort_by(|left, right| {
        left.rank
            .cmp(&right.rank)
            .then_with(|| left.path.cmp(&right.path))
    });
    directories
}

fn collect_sidecar_search_scope(
    directories: &mut Vec<SidecarSearchDirectory>,
    file_path: &Path,
    base_rank: u8,
) {
    let Some(mut directory) = file_path.parent().map(Path::to_path_buf) else {
        return;
    };

    for depth in 0..=SIDECAR_SEARCH_ANCESTOR_DEPTH {
        let ancestor_rank = base_rank.saturating_add((depth as u8) * 2);
        push_sidecar_search_directory(directories, directory.clone(), ancestor_rank, false);

        for directory_name in SIDECAR_DIRECTORY_HINTS {
            push_sidecar_search_directory(
                directories,
                directory.join(directory_name),
                ancestor_rank.saturating_add(1),
                false,
            );
        }

        let Some(parent) = directory.parent().map(Path::to_path_buf) else {
            break;
        };

        if parent == directory {
            break;
        }

        directory = parent;
    }
}

fn push_sidecar_search_directory(
    directories: &mut Vec<SidecarSearchDirectory>,
    path: PathBuf,
    rank: u8,
    recursive: bool,
) {
    let key = normalize_path_key(&path);

    if let Some(existing) = directories
        .iter_mut()
        .find(|directory| normalize_path_key(&directory.path) == key)
    {
        if rank < existing.rank {
            existing.rank = rank;
            existing.path = path;
        }
        existing.recursive = existing.recursive || recursive;
        return;
    }

    directories.push(SidecarSearchDirectory {
        path,
        rank,
        recursive,
    });
}

fn collect_common_lyrics_directories() -> Vec<PathBuf> {
    let Some(home_dir) = resolve_home_directory() else {
        return Vec::new();
    };

    ["Desktop", "Music", "Downloads"]
        .into_iter()
        .map(|segment| home_dir.join(segment))
        .filter(|path| path.is_dir())
        .collect()
}

fn resolve_home_directory() -> Option<PathBuf> {
    env::var_os("USERPROFILE")
        .or_else(|| env::var_os("HOME"))
        .map(PathBuf::from)
}

fn is_supported_sidecar_file(path: &Path) -> bool {
    path.extension()
        .and_then(|value| value.to_str())
        .is_some_and(|extension| {
            SIDECAR_EXTENSIONS
                .iter()
                .any(|allowed| extension.eq_ignore_ascii_case(allowed))
        })
}

fn score_sidecar_path(audio_path: &Path, path: &Path, lookup_hints: &LyricsLookupHints) -> u16 {
    let Some(candidate_stem) = path.file_stem().and_then(|value| value.to_str()) else {
        return 0;
    };
    let Some(audio_stem) = audio_path.file_stem().and_then(|value| value.to_str()) else {
        return 0;
    };
    let normalized_candidate = normalize_sidecar_match_text(candidate_stem);

    if normalized_candidate.is_empty() {
        return 0;
    }

    let ordered_names = collect_lookup_ordered_names(audio_stem, lookup_hints);
    let token_names = collect_lookup_token_signatures(audio_stem, lookup_hints);
    let candidate_signature = build_token_signature(candidate_stem);
    let directory_rank = resolve_search_directory_rank(audio_path, path, lookup_hints);
    let directory_penalty = u16::from(directory_rank) * 24;
    let mut score = 0;

    if candidate_stem.eq_ignore_ascii_case(audio_stem) {
        score = score.max(1_000u16.saturating_sub(directory_penalty));
    }

    if ordered_names
        .iter()
        .any(|name| name == &normalized_candidate)
    {
        score = score.max(940u16.saturating_sub(directory_penalty));
    }

    if token_names
        .iter()
        .any(|signature| signature == &candidate_signature)
    {
        score = score.max(900u16.saturating_sub(directory_penalty));
    }

    let normalized_title = normalize_sidecar_match_text(&lookup_hints.title);
    let normalized_artist = normalize_sidecar_match_text(&lookup_hints.artist);

    if !normalized_title.is_empty() && normalized_candidate == normalized_title {
        score = score.max(860u16.saturating_sub(directory_penalty));
    }

    if !normalized_title.is_empty()
        && normalized_candidate.contains(&normalized_title)
        && (normalized_artist.is_empty() || normalized_candidate.contains(&normalized_artist))
    {
        score = score.max(780u16.saturating_sub(directory_penalty));
    }

    if score > 0
        && path
            .extension()
            .and_then(|value| value.to_str())
            .is_some_and(|value| value.eq_ignore_ascii_case("lrc"))
    {
        score += 8;
    }

    score
}

fn resolve_search_directory_rank(
    audio_path: &Path,
    path: &Path,
    lookup_hints: &LyricsLookupHints,
) -> u8 {
    let Some(parent) = path.parent() else {
        return u8::MAX;
    };

    let mut best_rank = u8::MAX;

    for directory in collect_sidecar_search_directories(audio_path, lookup_hints) {
        let directory_key = normalize_path_key(&directory.path);
        let parent_key = normalize_path_key(parent);

        if parent_key == directory_key {
            best_rank = best_rank.min(directory.rank);
            continue;
        }

        if !directory.recursive {
            continue;
        }

        let Ok(relative_path) = parent.strip_prefix(&directory.path) else {
            continue;
        };
        let relative_depth = u8::try_from(relative_path.components().count()).unwrap_or(u8::MAX);
        best_rank = best_rank.min(directory.rank.saturating_add(relative_depth));
    }

    best_rank
}

fn collect_lookup_ordered_names(audio_stem: &str, lookup_hints: &LyricsLookupHints) -> Vec<String> {
    let mut names = Vec::new();
    let mut seen = HashSet::new();

    for candidate in [
        Some(audio_stem.to_string()),
        strip_extension(&lookup_hints.file_name),
        non_empty_lookup_value(&lookup_hints.title),
        non_empty_lookup_value(&lookup_hints.artist),
        non_empty_lookup_value(&lookup_hints.album),
        combine_lookup_values(&lookup_hints.artist, &lookup_hints.title, " - "),
        combine_lookup_values(&lookup_hints.title, &lookup_hints.artist, " - "),
        combine_lookup_values(&lookup_hints.artist, &lookup_hints.title, " "),
        combine_lookup_values(&lookup_hints.title, &lookup_hints.artist, " "),
    ]
    .into_iter()
    .flatten()
    {
        let normalized = normalize_sidecar_match_text(&candidate);

        if !normalized.is_empty() && seen.insert(normalized.clone()) {
            names.push(normalized);
        }
    }

    names
}

fn collect_lookup_token_signatures(
    audio_stem: &str,
    lookup_hints: &LyricsLookupHints,
) -> Vec<String> {
    let mut signatures = Vec::new();
    let mut seen = HashSet::new();

    for candidate in collect_lookup_ordered_names(audio_stem, lookup_hints) {
        let signature = build_token_signature(&candidate);

        if !signature.is_empty() && seen.insert(signature.clone()) {
            signatures.push(signature);
        }
    }

    signatures
}

fn non_empty_lookup_value(value: &str) -> Option<String> {
    let trimmed = value.trim();

    if trimmed.is_empty() {
        None
    } else {
        Some(trimmed.to_string())
    }
}

fn combine_lookup_values(left: &str, right: &str, separator: &str) -> Option<String> {
    let left = left.trim();
    let right = right.trim();

    if left.is_empty() || right.is_empty() {
        return None;
    }

    Some(format!("{left}{separator}{right}"))
}

fn strip_extension(value: &str) -> Option<String> {
    let trimmed = value.trim();

    if trimmed.is_empty() {
        return None;
    }

    Some(
        Path::new(trimmed)
            .file_stem()
            .map(|value| value.to_string_lossy().trim().to_string())
            .filter(|value| !value.is_empty())
            .unwrap_or_else(|| trimmed.to_string()),
    )
}

fn normalize_sidecar_match_text(value: &str) -> String {
    let mut normalized = String::new();
    let mut last_was_space = true;

    for character in value.chars() {
        if character.is_alphanumeric() {
            for lowercase in character.to_lowercase() {
                normalized.push(lowercase);
            }
            last_was_space = false;
            continue;
        }

        if !last_was_space {
            normalized.push(' ');
            last_was_space = true;
        }
    }

    normalized.trim().to_string()
}

fn build_token_signature(value: &str) -> String {
    let mut tokens = normalize_sidecar_match_text(value)
        .split_whitespace()
        .map(str::to_string)
        .collect::<Vec<_>>();

    tokens.sort();
    tokens.join(" ")
}

fn sidecar_priority(path: &Path) -> (u8, String) {
    let extension = path
        .extension()
        .and_then(|value| value.to_str())
        .unwrap_or_default()
        .to_ascii_lowercase();
    let rank = match extension.as_str() {
        "lrc" => 0,
        "txt" => 1,
        _ => 9,
    };

    (rank, path.to_string_lossy().to_string())
}

fn normalize_path_key(path: &Path) -> String {
    path.to_string_lossy()
        .replace('\\', "/")
        .to_ascii_lowercase()
}

fn collect_sidecar_paths_from_directory(
    audio_path: &Path,
    lookup_hints: &LyricsLookupHints,
    search_directory: &SidecarSearchDirectory,
    sidecar_paths: &mut Vec<PathBuf>,
    seen_paths: &mut HashSet<String>,
) -> Result<(), String> {
    if !search_directory.path.is_dir() {
        return Ok(());
    }

    let mut queue = VecDeque::from([search_directory.path.clone()]);

    while let Some(directory) = queue.pop_front() {
        let entries = fs::read_dir(&directory).map_err(|error| {
            format!(
                "Failed to read '{}' while discovering lyric sidecars: {error}",
                directory.display()
            )
        })?;

        for entry in entries {
            let entry = entry.map_err(|error| {
                format!(
                    "Failed to read a lyric sidecar candidate near '{}': {error}",
                    audio_path.display()
                )
            })?;
            let path = entry.path();
            let file_type = entry.file_type().map_err(|error| {
                format!(
                    "Failed to resolve a lyric sidecar candidate type near '{}': {error}",
                    audio_path.display()
                )
            })?;

            if file_type.is_dir() {
                if search_directory.recursive {
                    queue.push_back(path);
                }
                continue;
            }

            if !file_type.is_file() || !is_supported_sidecar_file(&path) {
                continue;
            }

            if score_sidecar_path(audio_path, &path, lookup_hints) == 0 {
                continue;
            }

            let dedupe_key = normalize_path_key(&path);

            if seen_paths.insert(dedupe_key) {
                sidecar_paths.push(path);
            }
        }
    }

    Ok(())
}

fn detect_sidecar_source_kind(path: &Path) -> LyricsSourceKind {
    match path
        .extension()
        .and_then(|value| value.to_str())
        .map(|value| value.to_ascii_lowercase())
        .as_deref()
    {
        Some("lrc") => LyricsSourceKind::SidecarLrc,
        _ => LyricsSourceKind::SidecarText,
    }
}

fn collect_embedded_candidates(audio_path: &Path) -> Result<Vec<LyricsCandidate>, String> {
    let tagged_file = Probe::open(audio_path)
        .map_err(|error| {
            format!(
                "Failed to open '{}' while parsing embedded lyrics: {error}",
                audio_path.display()
            )
        })?
        .guess_file_type()
        .map_err(|error| {
            format!(
                "Failed to detect the audio format for '{}' while parsing lyrics: {error}",
                audio_path.display()
            )
        })?
        .read()
        .map_err(|error| {
            format!(
                "Failed to read embedded metadata from '{}' while parsing lyrics: {error}",
                audio_path.display()
            )
        })?;
    let mut candidates = Vec::new();
    let primary_tag = tagged_file
        .primary_tag()
        .or_else(|| tagged_file.first_tag());

    if let Some(tag) = primary_tag {
        let mut embedded_texts = Vec::new();

        for text in collect_tag_lyrics_texts(tag, ItemKey::Lyrics) {
            if !embedded_texts.iter().any(|existing| existing == &text) {
                embedded_texts.push(text);
            }
        }

        for text in collect_tag_lyrics_texts(tag, ItemKey::UnsyncLyrics) {
            if !embedded_texts.iter().any(|existing| existing == &text) {
                embedded_texts.push(text);
            }
        }

        let mut embedded_text_candidates = embedded_texts
            .iter()
            .filter_map(|text| {
                build_candidate_from_text(text, LyricsSourceKind::EmbeddedText, None, 0)
            })
            .collect::<Vec<_>>();

        if let Some(candidate) = build_combined_embedded_synced_candidate(
            &embedded_text_candidates,
            LyricsSourceKind::EmbeddedText,
        ) {
            embedded_text_candidates.push(candidate);
        }

        candidates.extend(embedded_text_candidates);
    }

    if let Some(id3v2_tag) = tagged_file.tag(TagType::Id3v2).cloned().map(Id3v2Tag::from) {
        let mut sylt_candidates = build_sylt_candidates(&id3v2_tag);

        if let Some(candidate) = build_combined_embedded_synced_candidate(
            &sylt_candidates,
            LyricsSourceKind::EmbeddedSynced,
        ) {
            sylt_candidates.push(candidate);
        }

        if let Some(candidate) = select_best_candidate(sylt_candidates) {
            candidates.push(candidate);
        }
    }

    Ok(candidates)
}

fn collect_tag_lyrics_texts(tag: &lofty::tag::Tag, key: ItemKey) -> Vec<String> {
    let mut texts = Vec::new();

    for value in tag.get_strings(key) {
        let normalized = normalize_embedded_text(value);

        if normalized.is_empty() || texts.iter().any(|existing| existing == &normalized) {
            continue;
        }

        texts.push(normalized);
    }

    texts
}

fn normalize_embedded_text(value: &str) -> String {
    value
        .replace("\r\n", "\n")
        .replace('\r', "\n")
        .trim_matches('\u{feff}')
        .trim()
        .to_string()
}

fn build_sylt_candidates(id3v2_tag: &Id3v2Tag) -> Vec<LyricsCandidate> {
    let mut candidates = Vec::new();

    for frame in id3v2_tag {
        let Frame::Binary(binary_frame) = frame else {
            continue;
        };
        if binary_frame.id().as_str() != "SYLT" {
            continue;
        }

        let Ok(parsed_frame) =
            SynchronizedTextFrame::parse(binary_frame.data.as_ref(), binary_frame.flags())
        else {
            continue;
        };

        if !matches!(
            parsed_frame.content_type,
            SyncTextContentType::Lyrics | SyncTextContentType::TextTranscription
        ) {
            continue;
        }

        if let Some(candidate) = build_candidate_from_sylt(parsed_frame) {
            candidates.push(candidate);
        }
    }

    candidates
}

fn build_candidate_from_sylt(frame: SynchronizedTextFrame<'_>) -> Option<LyricsCandidate> {
    let language = String::from_utf8_lossy(&frame.language).trim().to_string();
    let mut display_lines = Vec::new();

    match frame.timestamp_format {
        TimestampFormat::MS => {
            let mut timed_entries = Vec::new();

            for (original_index, (timestamp, text)) in frame.content.into_iter().enumerate() {
                let normalized_text = normalize_lyric_line_text(&text);

                if !normalized_text.is_empty() {
                    display_lines.push(normalized_text.clone());
                }

                timed_entries.push(TimedLyricEntry {
                    start_time: (timestamp as f64 / 1_000.0).max(0.0),
                    text: normalized_text,
                    original_index,
                });
            }

            let display_text = join_display_lines(trim_empty_edge_lines(display_lines));
            let lines = build_synced_lines(timed_entries, 0);

            if lines.is_empty() && display_text.is_empty() {
                return None;
            }

            Some(LyricsCandidate {
                source: LyricsSourceKind::EmbeddedSynced,
                source_path: None,
                kind: LyricsContentKind::Synced,
                text: display_text,
                lines,
                title: String::new(),
                artist: String::new(),
                album: String::new(),
                by: String::new(),
                language,
                offset_ms: 0,
                match_score: 0,
            })
        }
        TimestampFormat::MPEG => {
            for (_, text) in frame.content {
                let normalized_text = normalize_lyric_line_text(&text);

                if normalized_text.is_empty() {
                    continue;
                }

                display_lines.push(normalized_text);
            }

            let display_lines = trim_empty_edge_lines(display_lines);
            let display_text = join_display_lines(display_lines.clone());
            let lines = build_unsynced_lines(display_lines);

            if lines.is_empty() {
                return None;
            }

            Some(LyricsCandidate {
                source: LyricsSourceKind::EmbeddedSynced,
                source_path: None,
                kind: LyricsContentKind::Unsynced,
                text: display_text,
                lines,
                title: String::new(),
                artist: String::new(),
                album: String::new(),
                by: String::new(),
                language,
                offset_ms: 0,
                match_score: 0,
            })
        }
    }
}

fn build_combined_embedded_synced_candidate(
    candidates: &[LyricsCandidate],
    source: LyricsSourceKind,
) -> Option<LyricsCandidate> {
    let synced_candidates = candidates
        .iter()
        .filter(|candidate| {
            candidate.source == source && candidate.kind == LyricsContentKind::Synced
        })
        .collect::<Vec<_>>();

    if synced_candidates.len() < 2 {
        return None;
    }

    let mut best_group = Vec::new();

    for base_candidate in &synced_candidates {
        let group = synced_candidates
            .iter()
            .copied()
            .filter(|candidate| {
                std::ptr::eq(*candidate, *base_candidate)
                    || synced_timelines_are_compatible(base_candidate, candidate)
            })
            .collect::<Vec<_>>();

        if group.len() > best_group.len()
            || (group.len() == best_group.len()
                && group
                    .iter()
                    .map(|candidate| candidate.text.len())
                    .sum::<usize>()
                    > best_group
                        .iter()
                        .map(|candidate: &&LyricsCandidate| candidate.text.len())
                        .sum::<usize>())
        {
            best_group = group;
        }
    }

    if best_group.len() < 2 {
        return None;
    }

    let base_candidate = best_group.iter().copied().max_by(|left, right| {
        candidate_score(left)
            .cmp(&candidate_score(right))
            .then_with(|| left.text.len().cmp(&right.text.len()))
            .then_with(|| left.lines.len().cmp(&right.lines.len()))
    })?;
    let mut timed_entries = Vec::new();
    let mut original_index = 0;

    for candidate in &best_group {
        for line in &candidate.lines {
            let Some(start_time) = line.start_time else {
                continue;
            };

            for text in collect_line_display_texts(line) {
                timed_entries.push(TimedLyricEntry {
                    start_time,
                    text,
                    original_index,
                });
                original_index += 1;
            }
        }
    }

    let lines = build_synced_lines(timed_entries, 0);

    if lines.is_empty() {
        return None;
    }

    Some(LyricsCandidate {
        source,
        source_path: None,
        kind: LyricsContentKind::Synced,
        text: join_display_lines(collect_lyrics_display_lines(&lines)),
        lines,
        title: base_candidate.title.clone(),
        artist: base_candidate.artist.clone(),
        album: base_candidate.album.clone(),
        by: base_candidate.by.clone(),
        language: base_candidate.language.clone(),
        offset_ms: base_candidate.offset_ms,
        match_score: best_group
            .iter()
            .map(|candidate| candidate.match_score)
            .max()
            .unwrap_or_default(),
    })
}

fn synced_timelines_are_compatible(left: &LyricsCandidate, right: &LyricsCandidate) -> bool {
    let left_times = collect_synced_start_times(&left.lines);
    let right_times = collect_synced_start_times(&right.lines);
    let min_len = left_times.len().min(right_times.len());

    if min_len == 0 {
        return false;
    }

    let matches = count_matching_timestamps(&left_times, &right_times);
    let required_matches = if min_len <= 2 {
        min_len
    } else {
        ((min_len as f64) * 0.5).ceil() as usize
    }
    .max(3.min(min_len));

    matches >= required_matches
}

fn collect_synced_start_times(lines: &[LyricsLine]) -> Vec<f64> {
    lines
        .iter()
        .filter_map(|line| line.start_time)
        .filter(|start_time| start_time.is_finite() && *start_time >= 0.0)
        .collect()
}

fn count_matching_timestamps(left_times: &[f64], right_times: &[f64]) -> usize {
    let mut left_index = 0;
    let mut right_index = 0;
    let mut matches = 0;

    while left_index < left_times.len() && right_index < right_times.len() {
        let diff = left_times[left_index] - right_times[right_index];

        if diff.abs() <= BILINGUAL_TIME_TOLERANCE_SECONDS {
            matches += 1;
            left_index += 1;
            right_index += 1;
        } else if diff < 0.0 {
            left_index += 1;
        } else {
            right_index += 1;
        }
    }

    matches
}

fn build_candidate_from_text(
    raw_text: &str,
    source: LyricsSourceKind,
    source_path: Option<String>,
    match_score: u16,
) -> Option<LyricsCandidate> {
    let parsed = parse_lyrics_text(raw_text);
    let display_lines = trim_empty_edge_lines(parsed.display_lines);
    let display_text = join_display_lines(display_lines.clone());

    if !parsed.timed_entries.is_empty() {
        let lines = build_synced_lines(parsed.timed_entries, parsed.offset_ms);

        if lines.is_empty() && display_text.is_empty() {
            return None;
        }

        return Some(LyricsCandidate {
            source,
            source_path,
            kind: LyricsContentKind::Synced,
            text: display_text,
            lines,
            title: parsed.title,
            artist: parsed.artist,
            album: parsed.album,
            by: parsed.by,
            language: parsed.language,
            offset_ms: parsed.offset_ms,
            match_score,
        });
    }

    let lines = build_unsynced_lines(display_lines);

    if lines.is_empty() {
        return None;
    }

    Some(LyricsCandidate {
        source,
        source_path,
        kind: LyricsContentKind::Unsynced,
        text: display_text,
        lines,
        title: parsed.title,
        artist: parsed.artist,
        album: parsed.album,
        by: parsed.by,
        language: parsed.language,
        offset_ms: parsed.offset_ms,
        match_score,
    })
}

fn parse_lyrics_text(raw_text: &str) -> ParsedLyricsText {
    let normalized_text = normalize_embedded_text(raw_text);
    let mut parsed = ParsedLyricsText::default();

    for raw_line in normalized_text.lines() {
        let trimmed_line = raw_line.trim_end_matches('\0');
        let parsed_line = parse_lrc_line(trimmed_line, &mut parsed);

        if !parsed_line.timestamps.is_empty() {
            let normalized_line = normalize_lyric_line_text(&parsed_line.text);

            parsed.display_lines.push(normalized_line.clone());

            for timestamp in parsed_line.timestamps {
                parsed.timed_entries.push(TimedLyricEntry {
                    start_time: timestamp,
                    text: normalized_line.clone(),
                    original_index: parsed.timed_entries.len(),
                });
            }

            continue;
        }

        if parsed_line.consumed_tag && parsed_line.text.trim().is_empty() {
            continue;
        }

        let normalized_line = normalize_unsynced_line(trimmed_line);
        parsed.display_lines.push(normalized_line);
    }

    parsed
}

#[derive(Debug, Default)]
struct ParsedLrcLine {
    timestamps: Vec<f64>,
    text: String,
    consumed_tag: bool,
}

fn parse_lrc_line(line: &str, parsed: &mut ParsedLyricsText) -> ParsedLrcLine {
    let mut remaining = line.trim();
    let mut timestamps = Vec::new();
    let mut consumed_tag = false;

    while let Some(stripped) = remaining.strip_prefix('[') {
        let Some(closing_index) = stripped.find(']') else {
            break;
        };
        let tag_content = stripped[..closing_index].trim();

        if let Some(timestamp) = parse_lrc_timestamp(tag_content) {
            timestamps.push(timestamp);
            consumed_tag = true;
            remaining = stripped[closing_index + 1..].trim_start();
            continue;
        }

        if apply_lrc_metadata_tag(tag_content, parsed) {
            consumed_tag = true;
            remaining = stripped[closing_index + 1..].trim_start();
            continue;
        }

        break;
    }

    ParsedLrcLine {
        timestamps,
        text: remaining.to_string(),
        consumed_tag,
    }
}

fn apply_lrc_metadata_tag(tag_content: &str, parsed: &mut ParsedLyricsText) -> bool {
    let Some((raw_key, raw_value)) = tag_content.split_once(':') else {
        return false;
    };
    let key = raw_key.trim().to_ascii_lowercase();
    let value = raw_value.trim();

    match key.as_str() {
        "ti" => parsed.title = value.to_string(),
        "ar" => parsed.artist = value.to_string(),
        "al" => parsed.album = value.to_string(),
        "by" => parsed.by = value.to_string(),
        "lang" | "la" => parsed.language = value.to_string(),
        "offset" => {
            parsed.offset_ms = value.parse::<i64>().unwrap_or_default();
        }
        "re" | "ve" | "tool" | "length" => {}
        _ => return false,
    }

    true
}

fn parse_lrc_timestamp(value: &str) -> Option<f64> {
    let normalized = value.trim().replace(',', ".");
    let parts = normalized.split(':').collect::<Vec<_>>();

    let total_seconds = match parts.as_slice() {
        [minutes, seconds] => {
            let minutes = minutes.parse::<f64>().ok()?;
            let seconds = seconds.parse::<f64>().ok()?;
            minutes * 60.0 + seconds
        }
        [hours, minutes, seconds] => {
            let hours = hours.parse::<f64>().ok()?;
            let minutes = minutes.parse::<f64>().ok()?;
            let seconds = seconds.parse::<f64>().ok()?;
            hours * 3_600.0 + minutes * 60.0 + seconds
        }
        _ => return None,
    };

    if total_seconds.is_finite() && total_seconds >= 0.0 {
        Some(total_seconds)
    } else {
        None
    }
}

fn build_synced_lines(mut timed_entries: Vec<TimedLyricEntry>, offset_ms: i64) -> Vec<LyricsLine> {
    if timed_entries.is_empty() {
        return Vec::new();
    }

    timed_entries.sort_by(|left, right| {
        left.start_time
            .partial_cmp(&right.start_time)
            .unwrap_or(std::cmp::Ordering::Equal)
            .then_with(|| left.original_index.cmp(&right.original_index))
    });

    let raw_lines = timed_entries
        .into_iter()
        .map(|entry| {
            let shifted_ms = (entry.start_time * 1_000.0).round() as i64 + offset_ms;
            LyricsLine {
                index: 0,
                text: entry.text,
                start_time: Some((shifted_ms.max(0) as f64) / 1_000.0),
                end_time: None,
                translated_lyric: String::new(),
                roman_lyric: String::new(),
            }
        })
        .collect::<Vec<_>>();
    let mut groups: Vec<Vec<LyricsLine>> = Vec::new();

    for line in raw_lines {
        let Some(start_time) = line.start_time else {
            continue;
        };
        let should_merge = groups
            .last()
            .and_then(|group| group.first())
            .and_then(|previous| previous.start_time)
            .is_some_and(|previous_start| {
                (start_time - previous_start).abs() <= BILINGUAL_TIME_TOLERANCE_SECONDS
            });

        if should_merge {
            if let Some(group) = groups.last_mut() {
                group.push(line);
            }
        } else {
            groups.push(vec![line]);
        }
    }

    let mut lines = groups
        .into_iter()
        .enumerate()
        .map(|(index, group)| {
            let primary_index = group
                .iter()
                .position(|line| !line.text.trim().is_empty())
                .unwrap_or(0);
            let primary = &group[primary_index];
            let mut translated_lines = Vec::new();
            let mut roman_lines = Vec::new();

            for (entry_index, entry) in group.iter().enumerate() {
                if entry_index == primary_index {
                    translated_lines.extend(
                        entry
                            .translated_lyric
                            .lines()
                            .map(str::to_string)
                            .collect::<Vec<_>>(),
                    );
                    roman_lines.extend(
                        entry
                            .roman_lyric
                            .lines()
                            .map(str::to_string)
                            .collect::<Vec<_>>(),
                    );
                    continue;
                }

                translated_lines.push(entry.text.clone());
                translated_lines.extend(
                    entry
                        .translated_lyric
                        .lines()
                        .map(str::to_string)
                        .collect::<Vec<_>>(),
                );
                roman_lines.extend(
                    entry
                        .roman_lyric
                        .lines()
                        .map(str::to_string)
                        .collect::<Vec<_>>(),
                );
            }

            LyricsLine {
                index,
                text: primary.text.clone(),
                start_time: primary.start_time,
                end_time: None,
                translated_lyric: unique_non_empty_texts(translated_lines).join("\n"),
                roman_lyric: unique_non_empty_texts(roman_lines).join("\n"),
            }
        })
        .collect::<Vec<_>>();

    for index in 0..lines.len() {
        let next_start_time = lines
            .get(index + 1)
            .and_then(|line| line.start_time)
            .filter(|next_start_time| {
                lines[index]
                    .start_time
                    .is_some_and(|current_start| *next_start_time >= current_start)
            });
        lines[index].end_time = next_start_time;
    }

    lines
}

fn build_unsynced_lines(lines: Vec<String>) -> Vec<LyricsLine> {
    lines
        .into_iter()
        .enumerate()
        .map(|(index, text)| LyricsLine {
            index,
            text,
            start_time: None,
            end_time: None,
            translated_lyric: String::new(),
            roman_lyric: String::new(),
        })
        .collect()
}

fn collect_line_display_texts(line: &LyricsLine) -> Vec<String> {
    let mut items = Vec::new();
    items.push(line.text.clone());
    items.extend(line.translated_lyric.lines().map(str::to_string));
    items.extend(line.roman_lyric.lines().map(str::to_string));
    unique_non_empty_texts(items)
}

fn collect_lyrics_display_lines(lines: &[LyricsLine]) -> Vec<String> {
    lines
        .iter()
        .flat_map(collect_line_display_texts)
        .collect::<Vec<_>>()
}

fn normalize_lyric_line_text(value: &str) -> String {
    value
        .replace("\r\n", "\n")
        .replace('\r', "\n")
        .trim_matches('\u{feff}')
        .trim()
        .to_string()
}

fn normalize_unsynced_line(value: &str) -> String {
    normalize_lyric_line_text(value)
}

fn unique_non_empty_texts<I>(items: I) -> Vec<String>
where
    I: IntoIterator<Item = String>,
{
    let mut seen = HashSet::new();
    let mut result = Vec::new();

    for item in items {
        let text = normalize_lyric_line_text(&item);

        if text.is_empty() || !seen.insert(text.clone()) {
            continue;
        }

        result.push(text);
    }

    result
}

fn trim_empty_edge_lines(lines: Vec<String>) -> Vec<String> {
    let mut start_index = 0;
    let mut end_index = lines.len();

    while start_index < end_index && lines[start_index].is_empty() {
        start_index += 1;
    }

    while end_index > start_index && lines[end_index - 1].is_empty() {
        end_index -= 1;
    }

    lines[start_index..end_index].to_vec()
}

fn join_display_lines(lines: Vec<String>) -> String {
    lines.join("\n")
}

fn read_text_file(path: &Path) -> Result<String, String> {
    let bytes = fs::read(path).map_err(|error| {
        format!(
            "Failed to read the lyric sidecar '{}' from disk: {error}",
            path.display()
        )
    })?;

    Ok(decode_text_bytes(&bytes))
}

fn decode_text_bytes(bytes: &[u8]) -> String {
    if let Some(utf8_without_bom) = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]) {
        return String::from_utf8_lossy(utf8_without_bom).into_owned();
    }

    if let Some(utf16le_without_bom) = bytes.strip_prefix(&[0xFF, 0xFE]) {
        return decode_utf16_bytes(utf16le_without_bom, true);
    }

    if let Some(utf16be_without_bom) = bytes.strip_prefix(&[0xFE, 0xFF]) {
        return decode_utf16_bytes(utf16be_without_bom, false);
    }

    if let Some(decoded_utf16) = decode_heuristic_utf16(bytes) {
        return decoded_utf16;
    }

    String::from_utf8_lossy(bytes).into_owned()
}

fn decode_utf16_bytes(bytes: &[u8], little_endian: bool) -> String {
    let mut utf16 = Vec::with_capacity(bytes.len() / 2);

    for chunk in bytes.chunks_exact(2) {
        let code_unit = if little_endian {
            u16::from_le_bytes([chunk[0], chunk[1]])
        } else {
            u16::from_be_bytes([chunk[0], chunk[1]])
        };
        utf16.push(code_unit);
    }

    String::from_utf16_lossy(&utf16)
}

fn decode_heuristic_utf16(bytes: &[u8]) -> Option<String> {
    if bytes.len() < 2 || !bytes.len().is_multiple_of(2) {
        return None;
    }

    let even_zero_count = bytes.iter().step_by(2).filter(|byte| **byte == 0).count();
    let odd_zero_count = bytes
        .iter()
        .skip(1)
        .step_by(2)
        .filter(|byte| **byte == 0)
        .count();
    let threshold = bytes.len() / 8;

    if even_zero_count < threshold && odd_zero_count < threshold {
        return None;
    }

    Some(decode_utf16_bytes(bytes, odd_zero_count >= even_zero_count))
}

fn select_best_candidate(candidates: Vec<LyricsCandidate>) -> Option<LyricsCandidate> {
    candidates.into_iter().max_by(|left, right| {
        candidate_score(left)
            .cmp(&candidate_score(right))
            .then_with(|| left.text.len().cmp(&right.text.len()))
            .then_with(|| left.lines.len().cmp(&right.lines.len()))
    })
}

fn candidate_score(candidate: &LyricsCandidate) -> (u8, u16, u8, usize) {
    let source_rank = match candidate.source {
        LyricsSourceKind::SidecarLrc => 4,
        LyricsSourceKind::SidecarText => 3,
        LyricsSourceKind::EmbeddedSynced => 2,
        LyricsSourceKind::EmbeddedText => 1,
    };
    let timing_rank = match candidate.kind {
        LyricsContentKind::Synced => 2,
        LyricsContentKind::Unsynced => 1,
    };

    (
        source_rank,
        candidate.match_score,
        timing_rank,
        candidate.lines.len(),
    )
}

fn sanitize_position_seconds(value: Option<f64>) -> Option<f64> {
    value.filter(|seconds| seconds.is_finite() && *seconds >= 0.0)
}

pub fn find_active_line_index(lines: &[LyricsLine], position_seconds: f64) -> Option<usize> {
    if !position_seconds.is_finite() || position_seconds < 0.0 {
        return None;
    }

    let mut active_line_index = None;

    for line in lines {
        let Some(start_time) = line.start_time else {
            continue;
        };

        if position_seconds < start_time {
            break;
        }

        if let Some(end_time) = line.end_time {
            if position_seconds >= end_time {
                active_line_index = Some(line.index);
                continue;
            }
        }

        active_line_index = Some(line.index);
    }

    active_line_index
}

#[cfg(test)]
mod tests {
    use super::{
        build_candidate_from_text, build_combined_embedded_synced_candidate, build_synced_lines,
        collect_sidecar_paths, find_active_line_index, parse_lyrics_text, score_sidecar_path,
        LyricsLine, LyricsLookupHints, LyricsSourceKind,
    };
    use std::{
        fs,
        path::PathBuf,
        time::{SystemTime, UNIX_EPOCH},
    };

    #[test]
    fn parses_synced_lrc_metadata_and_offset() {
        let parsed = parse_lyrics_text(
            "[ti:Test Song]\n[ar:OFPlayer]\n[offset:500]\n[00:01.00]Line A\n[00:02.50][00:04.00]Line B",
        );

        assert_eq!(parsed.title, "Test Song");
        assert_eq!(parsed.artist, "OFPlayer");
        assert_eq!(parsed.offset_ms, 500);
        assert_eq!(parsed.timed_entries.len(), 3);
        assert_eq!(parsed.display_lines, vec!["Line A", "Line B"]);
    }

    #[test]
    fn finds_active_synced_line_index() {
        let lines = vec![
            LyricsLine {
                index: 0,
                text: String::from("Line A"),
                start_time: Some(1.0),
                end_time: Some(3.0),
                translated_lyric: String::new(),
                roman_lyric: String::new(),
            },
            LyricsLine {
                index: 1,
                text: String::from("Line B"),
                start_time: Some(3.0),
                end_time: Some(6.0),
                translated_lyric: String::new(),
                roman_lyric: String::new(),
            },
            LyricsLine {
                index: 2,
                text: String::from("Line C"),
                start_time: Some(6.0),
                end_time: None,
                translated_lyric: String::new(),
                roman_lyric: String::new(),
            },
        ];

        assert_eq!(find_active_line_index(&lines, 0.5), None);
        assert_eq!(find_active_line_index(&lines, 1.2), Some(0));
        assert_eq!(find_active_line_index(&lines, 4.5), Some(1));
        assert_eq!(find_active_line_index(&lines, 9.0), Some(2));
    }

    #[test]
    fn merges_same_timestamp_lrc_lines_as_translation() {
        let parsed =
            parse_lyrics_text("[00:01.00]Hello world\n[00:01.00]你好，世界\n[00:04.00]Next line");
        let lines = build_synced_lines(parsed.timed_entries, parsed.offset_ms);

        assert_eq!(lines.len(), 2);
        assert_eq!(lines[0].text, "Hello world");
        assert_eq!(lines[0].translated_lyric, "你好，世界");
        assert_eq!(lines[0].start_time, Some(1.0));
        assert_eq!(lines[0].end_time, Some(4.0));
        assert_eq!(lines[1].text, "Next line");
    }

    #[test]
    fn combines_compatible_embedded_text_lyrics() {
        let original = build_candidate_from_text(
            "[00:01.00]Hello world\n[00:04.00]Next line",
            LyricsSourceKind::EmbeddedText,
            None,
            0,
        )
        .unwrap();
        let translated = build_candidate_from_text(
            "[00:01.00]你好，世界\n[00:04.00]下一句",
            LyricsSourceKind::EmbeddedText,
            None,
            0,
        )
        .unwrap();
        let combined = build_combined_embedded_synced_candidate(
            &[original, translated],
            LyricsSourceKind::EmbeddedText,
        )
        .unwrap();

        assert_eq!(combined.lines.len(), 2);
        assert_eq!(combined.lines[0].text, "Hello world");
        assert_eq!(combined.lines[0].translated_lyric, "你好，世界");
        assert_eq!(combined.lines[1].text, "Next line");
        assert_eq!(combined.lines[1].translated_lyric, "下一句");
    }

    #[test]
    fn matches_swapped_artist_title_sidecar_names() {
        let root = create_test_directory_root("swapped-sidecar-score");
        let audio_dir = root.join("Music");
        let lyrics_dir = root.join("Lyrics");
        let audio_path = audio_dir.join("Loren Allred - No Promises to Keep LOVELESS Ver.mp3");
        let sidecar_path = lyrics_dir.join("No Promises to Keep LOVELESS Ver. - Loren Allred.lrc");
        let hints = LyricsLookupHints {
            origin_path: Some(audio_path.clone()),
            title: String::from("No Promises to Keep LOVELESS Ver."),
            artist: String::from("Loren Allred"),
            album: String::new(),
            file_name: String::from("Loren Allred - No Promises to Keep LOVELESS Ver.mp3"),
            lyrics_path: None,
            lyrics_directories: vec![lyrics_dir],
        };

        assert!(score_sidecar_path(&audio_path, &sidecar_path, &hints) >= 600);
    }

    #[test]
    fn exact_same_stem_beats_fuzzy_match() {
        let root = create_test_directory_root("exact-sidecar-score");
        let audio_dir = root.join("Music");
        let lyrics_dir = root.join("Lyrics");
        let audio_path = audio_dir.join("Loren Allred - No Promises to Keep LOVELESS Ver.mp3");
        let exact_path = audio_dir.join("Loren Allred - No Promises to Keep LOVELESS Ver.lrc");
        let fuzzy_path = lyrics_dir.join("No Promises to Keep LOVELESS Ver. - Loren Allred.lrc");
        let hints = LyricsLookupHints {
            origin_path: Some(audio_path.clone()),
            title: String::from("No Promises to Keep LOVELESS Ver."),
            artist: String::from("Loren Allred"),
            album: String::new(),
            file_name: String::from("Loren Allred - No Promises to Keep LOVELESS Ver.mp3"),
            lyrics_path: None,
            lyrics_directories: vec![lyrics_dir],
        };

        assert!(
            score_sidecar_path(&audio_path, &exact_path, &hints)
                > score_sidecar_path(&audio_path, &fuzzy_path, &hints)
        );
    }

    #[test]
    fn discovers_sidecars_from_outer_lyrics_directory() {
        let root = create_test_directory_root("outer-lyrics");
        let album_dir = root.join("Album");
        let disc_dir = album_dir.join("Disc 1");
        let lyrics_dir = album_dir.join("Lyrics");
        let audio_path = disc_dir.join("Track 01.mp3");
        let lyrics_path = lyrics_dir.join("Track 01.lrc");

        fs::create_dir_all(&disc_dir).unwrap();
        fs::create_dir_all(&lyrics_dir).unwrap();
        fs::write(&lyrics_path, "[00:01.00]Hello world").unwrap();

        let hints = LyricsLookupHints {
            origin_path: Some(audio_path.clone()),
            title: String::from("Track 01"),
            artist: String::from("Composer"),
            album: String::from("Album"),
            file_name: String::from("Track 01.mp3"),
            lyrics_path: None,
            lyrics_directories: Vec::new(),
        };

        let sidecar_paths = collect_sidecar_paths(&audio_path, &hints).unwrap();

        assert!(sidecar_paths.iter().any(|path| path == &lyrics_path));

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn prefers_origin_folder_chain_over_managed_storage_chain() {
        let root = create_test_directory_root("origin-priority");
        let managed_dir = root.join("Managed");
        let source_album_dir = root.join("Source").join("Album");
        let source_lyrics_dir = source_album_dir.join("Lyrics");
        let managed_audio_path = managed_dir.join("Track 02.mp3");
        let origin_audio_path = source_album_dir.join("Track 02.mp3");
        let lyrics_path = source_lyrics_dir.join("Track 02.lrc");

        fs::create_dir_all(&managed_dir).unwrap();
        fs::create_dir_all(&source_lyrics_dir).unwrap();
        fs::write(&lyrics_path, "[00:02.00]From source folder").unwrap();

        let hints = LyricsLookupHints {
            origin_path: Some(origin_audio_path),
            title: String::from("Track 02"),
            artist: String::from("Composer"),
            album: String::from("Album"),
            file_name: String::from("Track 02.mp3"),
            lyrics_path: None,
            lyrics_directories: Vec::new(),
        };

        let sidecar_paths = collect_sidecar_paths(&managed_audio_path, &hints).unwrap();

        assert_eq!(sidecar_paths.first(), Some(&lyrics_path));

        let _ = fs::remove_dir_all(root);
    }

    #[test]
    fn scans_configured_lyrics_directories_recursively() {
        let root = create_test_directory_root("configured-lyrics");
        let audio_dir = root.join("Audio");
        let lyrics_root = root.join("Lyric Vault");
        let nested_lyrics_dir = lyrics_root.join("Final Fantasy").join("Rebirth");
        let audio_path = audio_dir.join("No Promises to Keep LOVELESS Ver.mp3");
        let lyrics_path =
            nested_lyrics_dir.join("No Promises to Keep LOVELESS Ver. - Loren Allred.lrc");

        fs::create_dir_all(&audio_dir).unwrap();
        fs::create_dir_all(&nested_lyrics_dir).unwrap();
        fs::write(&lyrics_path, "[00:03.00]Promise me").unwrap();

        let hints = LyricsLookupHints {
            origin_path: Some(audio_path.clone()),
            title: String::from("No Promises to Keep LOVELESS Ver."),
            artist: String::from("Loren Allred"),
            album: String::new(),
            file_name: String::from("No Promises to Keep LOVELESS Ver.mp3"),
            lyrics_path: None,
            lyrics_directories: vec![lyrics_root.clone()],
        };

        let sidecar_paths = collect_sidecar_paths(&audio_path, &hints).unwrap();

        assert_eq!(sidecar_paths.first(), Some(&lyrics_path));

        let _ = fs::remove_dir_all(root);
    }

    fn create_test_directory_root(label: &str) -> PathBuf {
        let nonce = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos();
        std::env::temp_dir().join(format!("ofplayer-lyrics-{label}-{nonce}"))
    }
}
