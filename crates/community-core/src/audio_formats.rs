use std::path::Path;

pub const SUPPORTED_AUDIO_EXTENSIONS: &[&str] = &[
    "mp3", "mp2", "mp1", "mpa", "wav", "wave", "flac", "ogg", "oga", "m4a", "m4b", "m4r", "mp4",
    "aac", "adts", "aif", "aiff", "aifc", "caf", "mka", "dsf", "dff",
];

pub fn is_supported_audio_extension(extension: &str) -> bool {
    let extension = normalize_extension(extension);

    SUPPORTED_AUDIO_EXTENSIONS.contains(&extension.as_str())
}

pub fn is_supported_audio_path(path: &Path) -> bool {
    path.extension()
        .map(|extension| is_supported_audio_extension(&extension.to_string_lossy()))
        .unwrap_or(false)
}

pub fn is_dsd_audio_path(path: &Path) -> bool {
    path.extension()
        .map(|extension| is_dsd_audio_extension(&extension.to_string_lossy()))
        .unwrap_or(false)
}

pub fn is_dsd_audio_extension(extension: &str) -> bool {
    matches!(normalize_extension(extension).as_str(), "dsf" | "dff")
}

pub fn is_supported_audio_content_type(content_type: &str) -> bool {
    let content_type = content_type
        .split(';')
        .next()
        .unwrap_or_default()
        .trim()
        .to_ascii_lowercase();

    matches!(
        content_type.as_str(),
        "audio/mpeg"
            | "audio/mp3"
            | "audio/wav"
            | "audio/wave"
            | "audio/x-wav"
            | "audio/flac"
            | "audio/x-flac"
            | "audio/ogg"
            | "audio/mp4"
            | "audio/x-m4a"
            | "audio/aac"
            | "audio/aacp"
            | "audio/x-aac"
            | "audio/aif"
            | "audio/aiff"
            | "audio/x-aiff"
            | "audio/x-caf"
            | "audio/caf"
            | "audio/x-matroska"
            | "audio/dsd"
            | "audio/x-dsd"
            | "audio/x-dsf"
            | "audio/x-dff"
    )
}

pub fn mime_type_for_extension(extension: &str) -> &'static str {
    match normalize_extension(extension).as_str() {
        "mp3" | "mp2" | "mp1" | "mpa" => "audio/mpeg",
        "wav" | "wave" => "audio/wav",
        "flac" => "audio/flac",
        "ogg" | "oga" => "audio/ogg",
        "m4a" | "m4b" | "m4r" | "mp4" => "audio/mp4",
        "aac" | "adts" => "audio/aac",
        "aif" | "aiff" | "aifc" => "audio/aiff",
        "caf" => "audio/x-caf",
        "mka" => "audio/x-matroska",
        "dsf" => "audio/x-dsf",
        "dff" => "audio/x-dff",
        _ => "",
    }
}

fn normalize_extension(extension: &str) -> String {
    extension
        .trim()
        .trim_start_matches('.')
        .to_ascii_lowercase()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn recognizes_extended_mainstream_audio_extensions() {
        for extension in [
            "mp3", "M4A", ".flac", "aiff", "caf", "mka", "mp2", "adts", "dsf", ".DFF",
        ] {
            assert!(is_supported_audio_extension(extension));
        }
    }

    #[test]
    fn does_not_claim_unsupported_decoder_families() {
        for extension in ["opus", "wma", "ape", "wv", "dst", "iso"] {
            assert!(!is_supported_audio_extension(extension));
        }
    }

    #[test]
    fn identifies_dsd_container_extensions() {
        assert!(is_dsd_audio_extension(".dsf"));
        assert!(is_dsd_audio_extension("DFF"));
        assert!(!is_dsd_audio_extension("dst"));
    }

    #[test]
    fn accepts_supported_audio_content_types_only() {
        assert!(is_supported_audio_content_type("audio/mp4; codecs=alac"));
        assert!(is_supported_audio_content_type("audio/x-caf"));
        assert!(is_supported_audio_content_type("audio/x-dsf"));
        assert!(!is_supported_audio_content_type("audio/opus"));
        assert!(!is_supported_audio_content_type("audio/x-ms-wma"));
    }

    #[test]
    fn resolves_mime_types_for_picker_generated_files() {
        assert_eq!(mime_type_for_extension("m4b"), "audio/mp4");
        assert_eq!(mime_type_for_extension(".aifc"), "audio/aiff");
        assert_eq!(mime_type_for_extension("mka"), "audio/x-matroska");
        assert_eq!(mime_type_for_extension("dsf"), "audio/x-dsf");
        assert_eq!(mime_type_for_extension("unknown"), "");
    }
}
