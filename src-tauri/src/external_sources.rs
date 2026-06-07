use crate::{
    audio_formats,
    metadata::{parse_audio_metadata, ParseAudioMetadataRequest},
};
use futures_util::stream::{FuturesUnordered, StreamExt};
use quick_xml::{events::Event, Reader};
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    collections::{hash_map::DefaultHasher, HashSet, VecDeque},
    fs,
    hash::{Hash, Hasher},
    io::Write,
    path::{Path, PathBuf},
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};
use url::Url;

const PROVIDER_WEBDAV: &str = "webdav";
const PROVIDER_FTP: &str = "ftp";
const PROVIDER_SUBSONIC: &str = "subsonic";
const SUBSONIC_API_VERSION: &str = "1.16.1";
const SUBSONIC_CLIENT_NAME: &str = "OFPlayer";
const HTTP_TIMEOUT_SECONDS: u64 = 25;
const EXTERNAL_PLAYBACK_CACHE_MAX_BYTES: u64 = 512 * 1024 * 1024;
const EXTERNAL_TRANSIENT_DOWNLOAD_STALE_AFTER: Duration = Duration::from_secs(24 * 60 * 60);
const EXTERNAL_PLAYBACK_CACHE_LOCK_STALE_AFTER: Duration = Duration::from_secs(2 * 60);
const EXTERNAL_PLAYBACK_CACHE_LOCK_WAIT: Duration = Duration::from_secs(8);
const EXTERNAL_PLAYBACK_CACHE_LOCK_POLL: Duration = Duration::from_millis(80);
const WEBDAV_SCAN_CONCURRENCY: usize = 8;
const WEBDAV_PROGRESS_LOG_INTERVAL_DIRECTORIES: usize = 25;
const WEBDAV_SLOW_REQUEST_MS: u64 = 1_500;
const WEBDAV_RETAINED_REQUEST_PROFILES: usize = 8;
const WEBDAV_PROPFIND_BODY: &str = r#"<?xml version="1.0" encoding="utf-8"?>
<d:propfind xmlns:d="DAV:">
  <d:prop>
    <d:displayname />
    <d:getcontentlength />
    <d:getcontenttype />
    <d:getetag />
    <d:getlastmodified />
    <d:resourcetype />
  </d:prop>
</d:propfind>"#;

#[derive(Debug, Clone, Deserialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ExternalLibraryAuth {
    pub username: Option<String>,
    pub password: Option<String>,
    pub token: Option<String>,
    pub salt: Option<String>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExternalLibraryConnection {
    pub provider: String,
    pub name: Option<String>,
    pub endpoint: String,
    pub root_path: Option<String>,
    pub auth: Option<ExternalLibraryAuth>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExternalProviderCapabilitiesRequest {
    pub provider: String,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExternalLibraryConnectionRequest {
    pub connection: ExternalLibraryConnection,
    pub limit: Option<usize>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExternalPlaybackSourceRequest {
    pub connection: ExternalLibraryConnection,
    pub track: Value,
    pub include_metadata: Option<bool>,
    pub metadata_only: Option<bool>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExternalProviderCapabilities {
    pub provider: String,
    pub rust_core: bool,
    pub can_list_libraries: bool,
    pub can_list_tracks: bool,
    pub can_stream: bool,
    pub requires_bridge: bool,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExternalLibraryTestResult {
    pub ok: bool,
    pub provider: String,
    pub capabilities: ExternalProviderCapabilities,
    pub message: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExternalRemoteLibrary {
    pub id: String,
    pub name: String,
    pub root_path: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExternalLibraryListResult {
    pub provider: String,
    pub libraries: Vec<ExternalRemoteLibrary>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExternalTrackListResult {
    pub provider: String,
    pub tracks: Vec<Value>,
    pub total: usize,
    pub diagnostics: ExternalTrackListDiagnostics,
}

#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ExternalTrackListDiagnostics {
    pub provider: String,
    pub root_url: Option<String>,
    pub total_ms: u64,
    pub request_count: usize,
    pub failed_request_count: usize,
    pub directories_scanned: usize,
    pub directories_queued: usize,
    pub duplicate_directory_count: usize,
    pub entries_seen: usize,
    pub collections_seen: usize,
    pub files_seen: usize,
    pub audio_files_seen: usize,
    pub non_audio_files_skipped: usize,
    pub max_concurrency: usize,
    pub limit: Option<usize>,
    pub truncated: bool,
    pub slow_requests: Vec<ExternalRequestDiagnostics>,
}

#[derive(Debug, Clone, Serialize, Default)]
#[serde(rename_all = "camelCase")]
pub struct ExternalRequestDiagnostics {
    pub url: String,
    pub depth: String,
    pub status: u16,
    pub duration_ms: u64,
    pub entry_count: usize,
    pub byte_count: usize,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ExternalPlaybackSourceResult {
    pub provider: String,
    pub source: Value,
    pub metadata: Option<Value>,
}

#[derive(Debug, Default, Clone)]
struct WebDavEntry {
    href: String,
    display_name: String,
    content_length: u64,
    content_type: String,
    etag: String,
    modified_at: String,
    is_collection: bool,
}

#[derive(Debug, Default, Clone)]
struct InferredRemoteMetadata {
    title: String,
    artist: String,
    album_artist: String,
    album: String,
    track_number: u32,
}

#[derive(Debug, Clone)]
struct WebDavTrackListResult {
    tracks: Vec<Value>,
    diagnostics: ExternalTrackListDiagnostics,
}

#[derive(Debug)]
struct WebDavPropfindResult {
    entries: Vec<WebDavEntry>,
    diagnostics: ExternalRequestDiagnostics,
}

fn normalize_provider(provider: &str) -> String {
    match provider.trim().to_ascii_lowercase().as_str() {
        PROVIDER_SUBSONIC | "navidrome" => String::from(PROVIDER_SUBSONIC),
        PROVIDER_FTP => String::from(PROVIDER_FTP),
        _ => String::from(PROVIDER_WEBDAV),
    }
}

pub fn provider_capabilities(provider: &str) -> ExternalProviderCapabilities {
    let provider = normalize_provider(provider);
    let requires_bridge = provider == PROVIDER_FTP;

    ExternalProviderCapabilities {
        provider,
        rust_core: true,
        can_list_libraries: !requires_bridge,
        can_list_tracks: !requires_bridge,
        can_stream: !requires_bridge,
        requires_bridge,
    }
}

pub async fn test_connection(
    request: ExternalLibraryConnectionRequest,
) -> Result<ExternalLibraryTestResult, String> {
    let provider = normalize_provider(&request.connection.provider);
    let capabilities = provider_capabilities(&provider);

    if capabilities.requires_bridge {
        return Ok(ExternalLibraryTestResult {
            ok: false,
            provider,
            capabilities,
            message: String::from(
                "FTP requires a native transport bridge before it can be tested.",
            ),
        });
    }

    match provider.as_str() {
        PROVIDER_SUBSONIC => {
            subsonic_request(&request.connection, "ping", &[]).await?;
        }
        PROVIDER_WEBDAV => {
            let client = http_client()?;
            let root_url = resolve_webdav_root_url(&request.connection)?;
            webdav_propfind(&client, &request.connection, &root_url, "0").await?;
        }
        _ => return Err(String::from("Unsupported external library provider.")),
    }

    Ok(ExternalLibraryTestResult {
        ok: true,
        provider,
        capabilities,
        message: String::from("Connection test completed."),
    })
}

pub async fn list_libraries(
    request: ExternalLibraryConnectionRequest,
) -> Result<ExternalLibraryListResult, String> {
    let provider = normalize_provider(&request.connection.provider);
    let libraries = match provider.as_str() {
        PROVIDER_SUBSONIC => subsonic_list_libraries(&request.connection).await?,
        PROVIDER_WEBDAV => vec![ExternalRemoteLibrary {
            id: normalized_text(request.connection.root_path.as_deref())
                .unwrap_or_else(|| "/".to_string()),
            name: normalized_text(request.connection.name.as_deref())
                .unwrap_or_else(|| String::from("WebDAV")),
            root_path: normalized_text(request.connection.root_path.as_deref()).unwrap_or_default(),
        }],
        PROVIDER_FTP => {
            return Err(String::from(
                "FTP requires a native transport bridge before libraries can be listed.",
            ));
        }
        _ => return Err(String::from("Unsupported external library provider.")),
    };

    Ok(ExternalLibraryListResult {
        provider,
        libraries,
    })
}

pub async fn list_tracks(
    request: ExternalLibraryConnectionRequest,
) -> Result<ExternalTrackListResult, String> {
    let started_at = Instant::now();
    let provider = normalize_provider(&request.connection.provider);
    let (tracks, diagnostics) = match provider.as_str() {
        PROVIDER_SUBSONIC => {
            let tracks = subsonic_list_tracks(&request.connection, request.limit).await?;
            let diagnostics = ExternalTrackListDiagnostics {
                provider: provider.clone(),
                total_ms: elapsed_ms(started_at),
                audio_files_seen: tracks.len(),
                limit: request.limit,
                truncated: limit_reached(tracks.len(), request.limit),
                ..ExternalTrackListDiagnostics::default()
            };
            (tracks, diagnostics)
        }
        PROVIDER_WEBDAV => {
            let result = webdav_list_tracks(&request.connection, request.limit).await?;
            (result.tracks, result.diagnostics)
        }
        PROVIDER_FTP => {
            return Err(String::from(
                "FTP requires a native transport bridge before tracks can be listed.",
            ));
        }
        _ => return Err(String::from("Unsupported external library provider.")),
    };

    Ok(ExternalTrackListResult {
        provider,
        total: tracks.len(),
        tracks,
        diagnostics,
    })
}

pub async fn resolve_playback_source(
    request: ExternalPlaybackSourceRequest,
    cache_dir: PathBuf,
) -> Result<ExternalPlaybackSourceResult, String> {
    let provider = normalize_provider(&request.connection.provider);
    let include_metadata = request.include_metadata.unwrap_or(true);

    let source = match provider.as_str() {
        PROVIDER_SUBSONIC => {
            let remote_id = track_source_text(&request.track, "remoteId")
                .or_else(|| value_text(&request.track, "remoteId"))
                .ok_or_else(|| String::from("Subsonic track is missing its remote id."))?;
            let stream_url =
                build_subsonic_url(&request.connection, "stream", &[("id", remote_id.as_str())])?;
            let transient_path = download_external_track_to_transient_file(
                &request.connection,
                provider.as_str(),
                &remote_id,
                &stream_url,
                &request.track,
                &cache_dir,
            )
            .await?;

            json!({
                "kind": "external-cache",
                "url": "",
                "provider": PROVIDER_SUBSONIC,
                "remoteId": remote_id,
                "path": transient_path,
                "originPath": track_source_text(&request.track, "originPath")
                    .or_else(|| track_source_text(&request.track, "path"))
                    .unwrap_or_default(),
                "transient": false,
                "deleteOnRelease": false,
                "persistUrl": false,
            })
        }
        PROVIDER_WEBDAV => {
            let path = resolve_webdav_remote_track_url(&request.track)
                .ok_or_else(|| String::from("WebDAV track is missing its source URL."))?;
            let root_url = resolve_webdav_root_url(&request.connection)?;
            let track_url =
                Url::parse(&path).map_err(|error| format!("Invalid WebDAV track URL: {error}"))?;

            if !same_url_scope(&root_url, &track_url) {
                return Err(String::from(
                    "Refusing to resolve a WebDAV track outside the configured library endpoint.",
                ));
            }

            let remote_id =
                track_source_text(&request.track, "remoteId").unwrap_or_else(|| path.clone());
            let transient_path = download_external_track_to_transient_file(
                &request.connection,
                provider.as_str(),
                &remote_id,
                &path,
                &request.track,
                &cache_dir,
            )
            .await?;

            json!({
                "kind": "external-cache",
                "url": "",
                "provider": PROVIDER_WEBDAV,
                "remoteId": remote_id,
                "path": transient_path,
                "originPath": path,
                "transient": false,
                "deleteOnRelease": false,
                "persistUrl": false
            })
        }
        PROVIDER_FTP => {
            return Err(String::from(
                "FTP requires a native playback bridge before tracks can be resolved.",
            ));
        }
        _ => return Err(String::from("Unsupported external library provider.")),
    };

    let metadata = if include_metadata {
        parse_playback_source_metadata(&source, &request.track)
    } else {
        None
    };

    if request.metadata_only.unwrap_or(false) {
        cleanup_transient_playback_source(&source, &cache_dir);
    }

    Ok(ExternalPlaybackSourceResult {
        provider,
        source,
        metadata,
    })
}

fn http_client() -> Result<reqwest::Client, String> {
    reqwest::Client::builder()
        .timeout(Duration::from_secs(HTTP_TIMEOUT_SECONDS))
        .build()
        .map_err(|error| format!("Failed to prepare external source HTTP client: {error}"))
}

async fn webdav_propfind(
    client: &reqwest::Client,
    connection: &ExternalLibraryConnection,
    url: &Url,
    depth: &str,
) -> Result<WebDavPropfindResult, String> {
    let started_at = Instant::now();
    let method = reqwest::Method::from_bytes(b"PROPFIND")
        .map_err(|error| format!("Failed to create WebDAV request method: {error}"))?;
    let mut request = client
        .request(method, url.clone())
        .header(
            reqwest::header::CONTENT_TYPE,
            "application/xml; charset=utf-8",
        )
        .header("Depth", depth)
        .body(String::from(WEBDAV_PROPFIND_BODY));

    if let Some(auth) = connection.auth.as_ref() {
        if auth_has_basic_credentials(auth) {
            request = request.basic_auth(
                auth.username.clone().unwrap_or_default(),
                Some(auth.password.clone().unwrap_or_default()),
            );
        }
    }

    let response = request
        .send()
        .await
        .map_err(|error| format!("WebDAV request failed: {error}"))?;
    let status = response.status();

    if !status.is_success() {
        if status == reqwest::StatusCode::METHOD_NOT_ALLOWED {
            return Err(format!(
                "WebDAV endpoint rejected PROPFIND with HTTP {status}. Check that the server URL points to a WebDAV endpoint such as /dav. Navidrome URLs such as /app or port 4533 should use the Navidrome / Subsonic provider."
            ));
        }

        return Err(format!("WebDAV request failed with HTTP {status}."));
    }

    let body = response
        .text()
        .await
        .map_err(|error| format!("Failed to read WebDAV response body: {error}"))?;
    let byte_count = body.len();
    let entries = parse_webdav_multistatus(&body)?;

    Ok(WebDavPropfindResult {
        diagnostics: ExternalRequestDiagnostics {
            url: url.as_str().to_string(),
            depth: depth.to_string(),
            status: status.as_u16(),
            duration_ms: elapsed_ms(started_at),
            entry_count: entries.len(),
            byte_count,
        },
        entries,
    })
}

async fn webdav_list_tracks(
    connection: &ExternalLibraryConnection,
    limit: Option<usize>,
) -> Result<WebDavTrackListResult, String> {
    let started_at = Instant::now();
    let client = http_client()?;
    let root_url = resolve_webdav_root_url(connection)?;
    let root_url_text = root_url.as_str().to_string();
    let mut queue = VecDeque::from([root_url.clone()]);
    let mut scheduled = HashSet::<String>::from([root_url_text.clone()]);
    let mut seen = HashSet::<String>::new();
    let mut in_flight = FuturesUnordered::new();
    let mut tracks = Vec::new();
    let mut diagnostics = ExternalTrackListDiagnostics {
        provider: String::from(PROVIDER_WEBDAV),
        root_url: Some(root_url_text.clone()),
        max_concurrency: WEBDAV_SCAN_CONCURRENCY,
        limit,
        directories_queued: 1,
        ..ExternalTrackListDiagnostics::default()
    };

    debug_external_sources_log(
        "webdav_scan_start",
        json!({
            "rootUrl": root_url_text,
            "limit": limit,
            "concurrency": WEBDAV_SCAN_CONCURRENCY,
        }),
    );

    loop {
        while in_flight.len() < WEBDAV_SCAN_CONCURRENCY
            && !queue.is_empty()
            && !limit_reached(tracks.len(), limit)
        {
            let Some(current_url) = queue.pop_front() else {
                break;
            };
            let current_url_text = current_url.as_str().to_string();

            if !seen.insert(current_url_text) {
                diagnostics.duplicate_directory_count += 1;
                continue;
            }

            let client = client.clone();
            let connection = connection.clone();

            in_flight.push(async move {
                let result = webdav_propfind(&client, &connection, &current_url, "1").await;
                (current_url, result)
            });
        }

        if in_flight.is_empty() {
            break;
        }

        let Some((current_url, result)) = in_flight.next().await else {
            break;
        };
        let current_url_text = current_url.as_str().to_string();
        let result = match result {
            Ok(result) => result,
            Err(error) => {
                diagnostics.failed_request_count += 1;
                diagnostics.total_ms = elapsed_ms(started_at);
                debug_external_sources_log(
                    "webdav_scan_error",
                    json!({
                        "url": current_url_text,
                        "error": error,
                        "diagnostics": diagnostics,
                    }),
                );
                return Err(error);
            }
        };

        diagnostics.request_count += 1;
        diagnostics.directories_scanned += 1;
        diagnostics.entries_seen += result.entries.len();
        remember_webdav_request_profile(&mut diagnostics, result.diagnostics.clone());

        if result.diagnostics.duration_ms >= WEBDAV_SLOW_REQUEST_MS {
            debug_external_sources_log(
                "webdav_propfind_slow",
                json!({
                    "url": result.diagnostics.url,
                    "durationMs": result.diagnostics.duration_ms,
                    "entryCount": result.diagnostics.entry_count,
                    "byteCount": result.diagnostics.byte_count,
                }),
            );
        }

        for entry in result.entries {
            let mut entry_url = resolve_webdav_entry_url(&root_url, &entry.href)?;

            if entry.is_collection {
                entry_url = with_directory_trailing_slash(entry_url);
            }

            let entry_url_text = entry_url.as_str().to_string();

            if entry_url_text == current_url_text {
                continue;
            }

            if !same_url_scope(&root_url, &entry_url) {
                continue;
            }

            if entry.is_collection {
                diagnostics.collections_seen += 1;

                if scheduled.insert(entry_url_text) {
                    diagnostics.directories_queued += 1;
                    queue.push_back(entry_url);
                } else {
                    diagnostics.duplicate_directory_count += 1;
                }

                continue;
            }

            diagnostics.files_seen += 1;
            let file_name = if entry.display_name.is_empty() {
                basename_from_url(&entry_url)
            } else {
                entry.display_name.clone()
            };

            if !is_audio_resource(&file_name, &entry.content_type) {
                diagnostics.non_audio_files_skipped += 1;
                continue;
            }

            diagnostics.audio_files_seen += 1;
            let inferred = infer_webdav_track_metadata(&root_url, &entry_url, &file_name);
            let format = extension_from_name(&file_name);

            tracks.push(json!({
                "remoteId": entry_url_text,
                "title": inferred.title.if_empty(|| sanitize_track_title(&file_name)),
                "artist": inferred.artist,
                "albumArtist": inferred.album_artist,
                "album": inferred.album,
                "trackNumber": inferred.track_number,
                "fileName": file_name,
                "fileSize": entry.content_length,
                "size": entry.content_length,
                "format": format,
                "mimeType": entry.content_type,
                "importedAt": optional_json_text(&entry.modified_at),
                "source": {
                    "kind": "webdav",
                    "provider": PROVIDER_WEBDAV,
                    "remoteId": entry_url_text,
                    "path": entry_url_text,
                    "originPath": entry_url_text,
                    "etag": entry.etag,
                    "contentType": entry.content_type,
                    "persistUrl": false,
                },
            }));

            if limit_reached(tracks.len(), limit) {
                break;
            }
        }

        if diagnostics
            .directories_scanned
            .is_multiple_of(WEBDAV_PROGRESS_LOG_INTERVAL_DIRECTORIES)
            || limit_reached(tracks.len(), limit)
        {
            debug_external_sources_log(
                "webdav_scan_progress",
                json!({
                    "directoriesScanned": diagnostics.directories_scanned,
                    "directoriesQueued": diagnostics.directories_queued,
                    "pendingDirectories": queue.len(),
                    "inFlight": in_flight.len(),
                    "entriesSeen": diagnostics.entries_seen,
                    "audioFilesSeen": diagnostics.audio_files_seen,
                    "totalMs": elapsed_ms(started_at),
                }),
            );
        }
    }

    tracks.sort_by(|left, right| {
        value_text(left, "remoteId")
            .unwrap_or_default()
            .cmp(&value_text(right, "remoteId").unwrap_or_default())
    });
    diagnostics.total_ms = elapsed_ms(started_at);
    diagnostics.truncated = limit_reached(tracks.len(), limit);

    debug_external_sources_log(
        "webdav_scan_complete",
        json!({
            "trackCount": tracks.len(),
            "diagnostics": diagnostics.clone(),
        }),
    );

    Ok(WebDavTrackListResult {
        tracks,
        diagnostics,
    })
}

fn parse_webdav_multistatus(xml: &str) -> Result<Vec<WebDavEntry>, String> {
    let mut reader = Reader::from_str(xml);
    reader.config_mut().trim_text(true);

    let mut entries = Vec::new();
    let mut current: Option<WebDavEntry> = None;
    let mut active_field = String::new();

    loop {
        match reader.read_event() {
            Ok(Event::Start(event)) => {
                let name = local_xml_name(event.name().as_ref());

                if name == "response" {
                    current = Some(WebDavEntry::default());
                } else if matches!(
                    name.as_str(),
                    "href"
                        | "displayname"
                        | "getcontentlength"
                        | "getcontenttype"
                        | "getetag"
                        | "getlastmodified"
                ) {
                    active_field = name;
                } else if name == "collection" {
                    if let Some(entry) = current.as_mut() {
                        entry.is_collection = true;
                    }
                }
            }
            Ok(Event::Text(event)) => {
                let text = String::from_utf8_lossy(event.as_ref()).trim().to_string();

                if text.is_empty() {
                    continue;
                }

                if let Some(entry) = current.as_mut() {
                    match active_field.as_str() {
                        "href" => entry.href = text,
                        "displayname" => entry.display_name = text,
                        "getcontentlength" => {
                            entry.content_length = text.parse::<u64>().unwrap_or(0);
                        }
                        "getcontenttype" => entry.content_type = text,
                        "getetag" => entry.etag = text,
                        "getlastmodified" => entry.modified_at = text,
                        _ => {}
                    }
                }
            }
            Ok(Event::End(event)) => {
                let name = local_xml_name(event.name().as_ref());

                if name == "response" {
                    if let Some(entry) = current.take() {
                        entries.push(entry);
                    }
                }

                active_field.clear();
            }
            Ok(Event::Eof) => break,
            Err(error) => return Err(format!("Failed to parse WebDAV response XML: {error}")),
            _ => {}
        }
    }

    Ok(entries)
}

fn resolve_webdav_entry_url(root_url: &Url, href: &str) -> Result<Url, String> {
    root_url
        .join(href)
        .or_else(|_| Url::parse(href))
        .map_err(|error| format!("Failed to resolve WebDAV entry URL: {error}"))
}

fn remember_webdav_request_profile(
    diagnostics: &mut ExternalTrackListDiagnostics,
    profile: ExternalRequestDiagnostics,
) {
    if profile.duration_ms < WEBDAV_SLOW_REQUEST_MS
        && diagnostics.slow_requests.len() >= WEBDAV_RETAINED_REQUEST_PROFILES
    {
        return;
    }

    diagnostics.slow_requests.push(profile);
    diagnostics
        .slow_requests
        .sort_by_key(|profile| std::cmp::Reverse(profile.duration_ms));
    diagnostics
        .slow_requests
        .truncate(WEBDAV_RETAINED_REQUEST_PROFILES);
}

fn debug_external_sources_log(event: &str, payload: Value) {
    #[cfg(debug_assertions)]
    {
        eprintln!("[OFPlayer external_sources] {event} {payload}");
    }

    #[cfg(not(debug_assertions))]
    {
        let _ = event;
        let _ = payload;
    }
}

async fn subsonic_request(
    connection: &ExternalLibraryConnection,
    method: &str,
    params: &[(&str, &str)],
) -> Result<Value, String> {
    let client = http_client()?;
    let url = build_subsonic_url(connection, method, params)?;
    let response = client
        .get(url)
        .send()
        .await
        .map_err(|error| format!("Subsonic request failed: {error}"))?;
    let status = response.status();

    if !status.is_success() {
        return Err(format!("Subsonic request failed with HTTP {status}."));
    }

    let payload = response
        .json::<Value>()
        .await
        .map_err(|error| format!("Failed to decode Subsonic response JSON: {error}"))?;
    let body = payload
        .get("subsonic-response")
        .or_else(|| payload.get("subsonicResponse"))
        .unwrap_or(&payload)
        .clone();

    if body.get("status").and_then(Value::as_str) == Some("failed") {
        let message = body
            .get("error")
            .and_then(|error| error.get("message"))
            .and_then(Value::as_str)
            .unwrap_or("Subsonic request failed.");
        return Err(String::from(message));
    }

    Ok(body)
}

fn parse_playback_source_metadata(source: &Value, track: &Value) -> Option<Value> {
    let path = value_text(source, "path")?;

    if path_is_probably_url(&path) {
        return None;
    }

    let file_name = value_text(track, "fileName")
        .or_else(|| {
            PathBuf::from(&path)
                .file_name()
                .map(|value| value.to_string_lossy().to_string())
        })
        .filter(|value| !value.trim().is_empty());

    parse_audio_metadata(ParseAudioMetadataRequest { path, file_name })
        .ok()
        .and_then(|metadata| serde_json::to_value(metadata).ok())
}

fn cleanup_transient_playback_source(source: &Value, cache_dir: &Path) {
    let is_transient = value_text(source, "kind").as_deref() == Some("external-temp")
        || source
            .get("transient")
            .and_then(Value::as_bool)
            .unwrap_or(false)
        || source
            .get("deleteOnRelease")
            .and_then(Value::as_bool)
            .unwrap_or(false);

    if !is_transient {
        return;
    }

    if let Some(path) = value_text(source, "path") {
        if can_cleanup_transient_playback_path(&path, cache_dir) {
            let _ = fs::remove_file(path);
        }
    }
}

fn can_cleanup_transient_playback_path(path: &str, cache_dir: &Path) -> bool {
    let path = Path::new(path);
    let Ok(path) = path.canonicalize() else {
        return false;
    };
    let Ok(cache_dir) = cache_dir.canonicalize() else {
        return false;
    };

    path.is_file() && path.starts_with(cache_dir)
}

fn resolve_webdav_remote_track_url(track: &Value) -> Option<String> {
    [
        track_source_text(track, "originPath"),
        track_source_text(track, "remoteId"),
        track_source_text(track, "url"),
        track_source_text(track, "path"),
    ]
    .into_iter()
    .flatten()
    .find(|value| path_is_probably_url(value))
}

fn path_is_probably_url(value: &str) -> bool {
    Url::parse(value)
        .map(|url| matches!(url.scheme(), "http" | "https"))
        .unwrap_or(false)
}

async fn download_external_track_to_transient_file(
    connection: &ExternalLibraryConnection,
    provider: &str,
    remote_id: &str,
    url: &str,
    track: &Value,
    cache_dir: &Path,
) -> Result<String, String> {
    let provider_cache_dir = cache_dir.join(provider);
    fs::create_dir_all(&provider_cache_dir).map_err(|error| {
        format!(
            "Failed to prepare external playback staging directory '{}': {error}",
            provider_cache_dir.display()
        )
    })?;
    clear_stale_transient_downloads(&provider_cache_dir);
    clear_stale_external_cache_locks(&provider_cache_dir);

    let extension = resolve_cache_extension(track, url);
    let cache_extension = if extension.is_empty() {
        "audio"
    } else {
        extension.as_str()
    };
    let cache_key = stable_cache_key(&format!("{provider}:{remote_id}:{url}"));
    let cache_name = format!("external-cache-{cache_key}.{cache_extension}");
    let cache_path = provider_cache_dir.join(cache_name);

    if is_reusable_external_cache_file(&cache_path) {
        return Ok(cache_path.to_string_lossy().to_string());
    }

    let lock_path = provider_cache_dir.join(format!("external-cache-{cache_key}.lock"));
    let cache_lock = acquire_external_cache_lock(&lock_path);

    if cache_lock.is_none() && wait_for_external_cache_file(&cache_path) {
        return Ok(cache_path.to_string_lossy().to_string());
    }

    let client = http_client()?;
    let mut request = client.get(url);

    if provider == PROVIDER_WEBDAV {
        if let Some(auth) = connection.auth.as_ref() {
            if auth_has_basic_credentials(auth) {
                request = request.basic_auth(
                    auth.username.clone().unwrap_or_default(),
                    Some(auth.password.clone().unwrap_or_default()),
                );
            }
        }
    }

    let response = request
        .send()
        .await
        .map_err(|error| format!("External playback download failed: {error}"))?;
    let status = response.status();

    if !status.is_success() {
        return Err(format!(
            "External playback download failed with HTTP {status}."
        ));
    }

    if response
        .content_length()
        .is_some_and(|length| length > EXTERNAL_PLAYBACK_CACHE_MAX_BYTES)
    {
        return Err(format!(
            "External playback download is larger than the {} MB safety limit.",
            EXTERNAL_PLAYBACK_CACHE_MAX_BYTES / 1024 / 1024
        ));
    }

    let temp_path = provider_cache_dir.join(format!(
        "external-cache-{cache_key}-{}-{}.{}.part",
        std::process::id(),
        current_unix_nanos(),
        cache_extension
    ));
    let mut file = fs::File::create(&temp_path).map_err(|error| {
        format!(
            "Failed to create external playback staging file '{}': {error}",
            temp_path.display()
        )
    })?;
    let mut downloaded_bytes = 0u64;
    let mut response = response;

    while let Some(chunk) = response
        .chunk()
        .await
        .map_err(|error| format!("Failed to read external playback download: {error}"))?
    {
        let chunk_len = u64::try_from(chunk.len()).unwrap_or(u64::MAX);
        downloaded_bytes = downloaded_bytes.saturating_add(chunk_len);

        if downloaded_bytes > EXTERNAL_PLAYBACK_CACHE_MAX_BYTES {
            drop(file);
            let _ = fs::remove_file(&temp_path);
            return Err(format!(
                "External playback download is larger than the {} MB safety limit.",
                EXTERNAL_PLAYBACK_CACHE_MAX_BYTES / 1024 / 1024
            ));
        }

        file.write_all(&chunk).map_err(|error| {
            format!(
                "Failed to write external playback staging file '{}': {error}",
                temp_path.display()
            )
        })?;
    }

    file.flush().map_err(|error| {
        format!(
            "Failed to flush external playback staging file '{}': {error}",
            temp_path.display()
        )
    })?;
    drop(file);

    if is_reusable_external_cache_file(&cache_path) {
        let _ = fs::remove_file(&temp_path);
        return Ok(cache_path.to_string_lossy().to_string());
    }

    if let Err(error) = fs::rename(&temp_path, &cache_path) {
        let _ = fs::remove_file(&temp_path);

        if is_reusable_external_cache_file(&cache_path) {
            return Ok(cache_path.to_string_lossy().to_string());
        }

        return Err(format!(
            "Failed to finalize external playback staging file '{}': {error}",
            cache_path.display()
        ));
    }

    Ok(cache_path.to_string_lossy().to_string())
}

fn clear_stale_transient_downloads(path: &Path) {
    let Ok(entries) = fs::read_dir(path) else {
        return;
    };

    for entry in entries.flatten() {
        let entry_path = entry.path();
        let file_name = entry.file_name().to_string_lossy().to_string();
        let is_transient = file_name.starts_with("transient-") || file_name.ends_with(".part");

        if !is_transient {
            continue;
        }

        let Ok(metadata) = entry.metadata() else {
            continue;
        };

        if !metadata.is_file() {
            continue;
        }

        let is_stale = metadata
            .modified()
            .ok()
            .and_then(|modified| modified.elapsed().ok())
            .is_some_and(|age| age >= EXTERNAL_TRANSIENT_DOWNLOAD_STALE_AFTER);

        if is_stale {
            let _ = fs::remove_file(entry_path);
        }
    }
}

struct ExternalCacheLock {
    path: PathBuf,
    _file: fs::File,
}

impl Drop for ExternalCacheLock {
    fn drop(&mut self) {
        let _ = fs::remove_file(&self.path);
    }
}

fn acquire_external_cache_lock(path: &Path) -> Option<ExternalCacheLock> {
    fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)
        .ok()
        .map(|file| ExternalCacheLock {
            path: path.to_path_buf(),
            _file: file,
        })
}

fn wait_for_external_cache_file(path: &Path) -> bool {
    let started_at = Instant::now();

    while started_at.elapsed() < EXTERNAL_PLAYBACK_CACHE_LOCK_WAIT {
        if is_reusable_external_cache_file(path) {
            return true;
        }

        std::thread::sleep(EXTERNAL_PLAYBACK_CACHE_LOCK_POLL);
    }

    is_reusable_external_cache_file(path)
}

fn is_reusable_external_cache_file(path: &Path) -> bool {
    let Ok(metadata) = fs::metadata(path) else {
        return false;
    };

    metadata.is_file() && metadata.len() > 0 && metadata.len() <= EXTERNAL_PLAYBACK_CACHE_MAX_BYTES
}

fn clear_stale_external_cache_locks(path: &Path) {
    let Ok(entries) = fs::read_dir(path) else {
        return;
    };

    for entry in entries.flatten() {
        let entry_path = entry.path();
        let file_name = entry.file_name().to_string_lossy().to_string();

        if !file_name.starts_with("external-cache-") || !file_name.ends_with(".lock") {
            continue;
        }

        let Ok(metadata) = entry.metadata() else {
            continue;
        };

        if !metadata.is_file() {
            continue;
        }

        let is_stale = metadata
            .modified()
            .ok()
            .and_then(|modified| modified.elapsed().ok())
            .is_some_and(|age| age >= EXTERNAL_PLAYBACK_CACHE_LOCK_STALE_AFTER);

        if is_stale {
            let _ = fs::remove_file(entry_path);
        }
    }
}

fn current_unix_nanos() -> u128 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| duration.as_nanos())
        .unwrap_or_default()
}

fn stable_cache_key(value: &str) -> String {
    let mut hasher = DefaultHasher::new();
    value.hash(&mut hasher);
    format!("{:016x}", hasher.finish())
}

fn resolve_cache_extension(track: &Value, url: &str) -> String {
    let format = value_text(track, "format")
        .or_else(|| value_text(track, "fileName").map(|name| extension_from_name(&name)))
        .or_else(|| {
            Url::parse(url)
                .ok()
                .map(|parsed_url| extension_from_name(parsed_url.path()))
        })
        .unwrap_or_default()
        .trim_start_matches('.')
        .to_ascii_lowercase();

    if format.is_empty()
        || format.len() > 8
        || !format.chars().all(|value| value.is_ascii_alphanumeric())
    {
        String::from("audio")
    } else {
        format
    }
}

fn limit_reached(count: usize, limit: Option<usize>) -> bool {
    match limit {
        Some(limit) => count >= limit,
        None => false,
    }
}

fn elapsed_ms(started_at: Instant) -> u64 {
    let millis = started_at.elapsed().as_millis();
    millis.min(u128::from(u64::MAX)) as u64
}

fn infer_webdav_track_metadata(
    root_url: &Url,
    entry_url: &Url,
    file_name: &str,
) -> InferredRemoteMetadata {
    let directories = relative_webdav_directories(root_url, entry_url);
    let album = directories.last().cloned().unwrap_or_default();
    let folder_artist = directories
        .len()
        .checked_sub(2)
        .and_then(|index| directories.get(index))
        .cloned()
        .unwrap_or_default();
    let mut metadata = infer_metadata_from_file_name(file_name, folder_artist.is_empty());

    if metadata.album.is_empty() {
        metadata.album = album;
    }

    if metadata.album_artist.is_empty() {
        metadata.album_artist = folder_artist.clone();
    }

    if metadata.artist.is_empty() {
        metadata.artist = folder_artist;
    }

    metadata
}

fn infer_metadata_from_file_name(
    file_name: &str,
    allow_artist_split: bool,
) -> InferredRemoteMetadata {
    let stem = file_name
        .rsplit_once('.')
        .map(|(stem, _)| stem)
        .unwrap_or(file_name)
        .trim();
    let (track_number, title_source) = split_leading_track_number(stem);
    let mut metadata = InferredRemoteMetadata {
        title: sanitize_track_title(&title_source),
        track_number,
        ..InferredRemoteMetadata::default()
    };
    let parts = title_source
        .split(" - ")
        .map(str::trim)
        .filter(|part| !part.is_empty())
        .collect::<Vec<_>>();

    if allow_artist_split && parts.len() >= 2 {
        metadata.artist = parts[0].to_string();
        metadata.title = parts[1..].join(" - ");
    }

    metadata
}

fn split_leading_track_number(value: &str) -> (u32, String) {
    let trimmed = value.trim();
    let digit_count = trimmed
        .bytes()
        .take_while(|byte| byte.is_ascii_digit())
        .count();

    if digit_count == 0 || digit_count > 3 {
        return (0, trimmed.to_string());
    }

    let number = trimmed[..digit_count].parse::<u32>().unwrap_or_default();

    if number == 0 {
        return (0, trimmed.to_string());
    }

    let rest = trimmed[digit_count..]
        .trim_start_matches(|value: char| {
            value.is_whitespace() || matches!(value, '-' | '.' | '_' | ')' | ']')
        })
        .trim();

    if rest.is_empty() {
        (0, trimmed.to_string())
    } else {
        (number, rest.to_string())
    }
}

fn relative_webdav_directories(root_url: &Url, entry_url: &Url) -> Vec<String> {
    let root_segments = decoded_url_path_segments(root_url);
    let mut entry_segments = decoded_url_path_segments(entry_url);

    if !entry_segments.is_empty() {
        entry_segments.pop();
    }

    if !root_segments.is_empty() && entry_segments.starts_with(root_segments.as_slice()) {
        entry_segments = entry_segments[root_segments.len()..].to_vec();
    }

    entry_segments
        .into_iter()
        .filter(|segment| !segment.trim().is_empty())
        .collect()
}

fn decoded_url_path_segments(url: &Url) -> Vec<String> {
    url.path_segments()
        .map(|segments| {
            segments
                .filter(|segment| !segment.is_empty())
                .map(percent_decode_path_segment)
                .collect()
        })
        .unwrap_or_default()
}

fn percent_decode_path_segment(segment: &str) -> String {
    let bytes = segment.as_bytes();
    let mut decoded = Vec::with_capacity(bytes.len());
    let mut index = 0usize;

    while index < bytes.len() {
        if bytes[index] == b'%' && index + 2 < bytes.len() {
            if let (Some(high), Some(low)) =
                (hex_value(bytes[index + 1]), hex_value(bytes[index + 2]))
            {
                decoded.push(high * 16 + low);
                index += 3;
                continue;
            }
        }

        decoded.push(bytes[index]);
        index += 1;
    }

    String::from_utf8_lossy(&decoded).trim().to_string()
}

async fn subsonic_list_libraries(
    connection: &ExternalLibraryConnection,
) -> Result<Vec<ExternalRemoteLibrary>, String> {
    let body = subsonic_request(connection, "getMusicFolders", &[]).await?;
    let folders = json_array(body.pointer("/musicFolders/musicFolder"));

    if folders.is_empty() {
        return Ok(vec![ExternalRemoteLibrary {
            id: normalized_text(connection.root_path.as_deref())
                .unwrap_or_else(|| String::from("default")),
            name: normalized_text(connection.name.as_deref())
                .unwrap_or_else(|| String::from("Subsonic")),
            root_path: normalized_text(connection.root_path.as_deref()).unwrap_or_default(),
        }]);
    }

    Ok(folders
        .into_iter()
        .map(|folder| {
            let id = json_text(&folder, "id");
            ExternalRemoteLibrary {
                id: id.clone(),
                name: json_text(&folder, "name")
                    .trim()
                    .to_string()
                    .if_empty(|| id.clone()),
                root_path: id,
            }
        })
        .collect())
}

async fn subsonic_list_tracks(
    connection: &ExternalLibraryConnection,
    limit: Option<usize>,
) -> Result<Vec<Value>, String> {
    let track_limit = limit;
    let page_size = 100usize;
    let mut albums = Vec::new();
    let mut offset = 0usize;

    loop {
        let size_text = page_size.to_string();
        let offset_text = offset.to_string();
        let mut params = vec![
            ("type", "alphabeticalByName"),
            ("size", size_text.as_str()),
            ("offset", offset_text.as_str()),
        ];
        let music_folder_id = normalized_text(connection.root_path.as_deref());

        if let Some(music_folder_id) = music_folder_id.as_deref() {
            params.push(("musicFolderId", music_folder_id));
        }

        let body = subsonic_request(connection, "getAlbumList2", &params).await?;
        let page = json_array(body.pointer("/albumList2/album"));

        if page.is_empty() {
            break;
        }

        offset += page.len();
        albums.extend(page);

        if !offset.is_multiple_of(page_size) {
            break;
        }
    }

    let mut tracks = Vec::new();

    for album in albums {
        if limit_reached(tracks.len(), track_limit) {
            break;
        }

        let album_id = json_text(&album, "id");

        if album_id.is_empty() {
            continue;
        }

        let body = subsonic_request(connection, "getAlbum", &[("id", album_id.as_str())]).await?;
        let songs = json_array(body.pointer("/album/song"));
        let track_total = songs.len() as u64;
        let disc_total = songs
            .iter()
            .map(|song| json_u64(song, "discNumber"))
            .max()
            .unwrap_or_default();

        for song in songs {
            if limit_reached(tracks.len(), track_limit) {
                break;
            }

            let remote_id = json_text(&song, "id");

            if remote_id.is_empty() {
                continue;
            }

            let path = json_text(&song, "path");
            let suffix = json_text(&song, "suffix");
            let file_name = basename_from_path(&path).if_empty(|| {
                let title = json_text(&song, "title").if_empty(|| remote_id.clone());
                if suffix.is_empty() {
                    title
                } else {
                    format!("{title}.{suffix}")
                }
            });

            tracks.push(json!({
                "remoteId": remote_id,
                "title": json_text(&song, "title").if_empty(|| sanitize_track_title(&file_name)),
                "artist": json_text(&song, "artist"),
                "albumArtist": json_text(&song, "albumArtist").if_empty(|| json_text(&album, "artist")),
                "album": json_text(&song, "album")
                    .if_empty(|| json_text(&album, "name"))
                    .if_empty(|| json_text(&album, "title")),
                "genre": json_text(&song, "genre").if_empty(|| json_text(&album, "genre")),
                "year": first_nonzero_u64(&[json_u64(&song, "year"), json_u64(&album, "year")]),
                "trackNumber": json_u64(&song, "track"),
                "trackTotal": track_total,
                "discNumber": json_u64(&song, "discNumber"),
                "discTotal": disc_total,
                "fileName": file_name,
                "fileSize": json_u64(&song, "size"),
                "size": json_u64(&song, "size"),
                "duration": json_f64(&song, "duration"),
                "format": suffix.if_empty(|| extension_from_name(&path)),
                "bitrate": subsonic_bitrate_bps(&song),
                "sampleRate": json_u64_any(&song, &["samplingRate", "sampleRate"]),
                "bitDepth": json_u64(&song, "bitDepth"),
                "mimeType": json_text(&song, "contentType"),
                "importedAt": optional_json_text(&json_text(&song, "created")),
                "source": {
                    "kind": "subsonic",
                    "provider": PROVIDER_SUBSONIC,
                    "remoteId": remote_id,
                    "path": path,
                    "originPath": path,
                    "albumId": album_id,
                    "coverArt": json_text(&song, "coverArt").if_empty(|| json_text(&album, "coverArt")),
                    "contentType": json_text(&song, "contentType"),
                    "persistUrl": false,
                },
            }));
        }
    }

    Ok(tracks)
}

fn build_subsonic_url(
    connection: &ExternalLibraryConnection,
    method: &str,
    params: &[(&str, &str)],
) -> Result<String, String> {
    let endpoint = normalize_endpoint(&connection.endpoint)?;
    let mut url = endpoint
        .join(&format!("rest/{method}.view"))
        .map_err(|error| format!("Failed to build Subsonic API URL: {error}"))?;
    let auth = connection.auth.as_ref().cloned().unwrap_or_default();

    {
        let mut pairs = url.query_pairs_mut();
        pairs.append_pair("u", auth.username.as_deref().unwrap_or_default());
        pairs.append_pair("v", SUBSONIC_API_VERSION);
        pairs.append_pair("c", SUBSONIC_CLIENT_NAME);
        pairs.append_pair("f", "json");

        if let (Some(token), Some(salt)) = (
            normalized_text(auth.token.as_deref()),
            normalized_text(auth.salt.as_deref()),
        ) {
            pairs.append_pair("t", &token);
            pairs.append_pair("s", &salt);
        } else if let Some(password) = normalized_text(auth.password.as_deref()) {
            pairs.append_pair("p", &format!("enc:{}", hex_encode(password.as_bytes())));
        }

        for (key, value) in params {
            pairs.append_pair(key, value);
        }
    }

    Ok(url.to_string())
}

fn resolve_webdav_root_url(connection: &ExternalLibraryConnection) -> Result<Url, String> {
    let endpoint = normalize_endpoint(&connection.endpoint)?;
    let root_path = normalized_text(connection.root_path.as_deref()).unwrap_or_default();

    if root_path.is_empty() {
        return Ok(with_directory_trailing_slash(endpoint));
    }

    endpoint
        .join(root_path.trim_start_matches('/'))
        .map(with_directory_trailing_slash)
        .map_err(|error| format!("Failed to resolve WebDAV root URL: {error}"))
}

fn normalize_endpoint(endpoint: &str) -> Result<Url, String> {
    let endpoint = endpoint.trim();

    if endpoint.is_empty() {
        return Err(String::from("External source endpoint is required."));
    }

    let endpoint = if endpoint.ends_with('/') {
        endpoint.to_string()
    } else {
        format!("{endpoint}/")
    };

    let endpoint = Url::parse(&endpoint)
        .map_err(|error| format!("Invalid external source endpoint: {error}"))?;

    if !matches!(endpoint.scheme(), "http" | "https") {
        return Err(String::from(
            "External source endpoint must use http or https.",
        ));
    }

    Ok(endpoint)
}

fn same_url_origin(left: &Url, right: &Url) -> bool {
    left.scheme() == right.scheme()
        && left.domain() == right.domain()
        && left.port_or_known_default() == right.port_or_known_default()
}

fn same_url_scope(root: &Url, candidate: &Url) -> bool {
    if !same_url_origin(root, candidate) {
        return false;
    }

    let root_path = root.path().trim_end_matches('/');

    if root_path.is_empty() || root_path == "/" {
        return true;
    }

    let candidate_path = candidate.path();
    candidate_path == root_path
        || candidate_path
            .strip_prefix(root_path)
            .is_some_and(|rest| rest.starts_with('/'))
}

fn with_directory_trailing_slash(mut url: Url) -> Url {
    if !url.path().ends_with('/') {
        let path = format!("{}/", url.path());
        url.set_path(&path);
    }

    url
}

fn auth_has_basic_credentials(auth: &ExternalLibraryAuth) -> bool {
    normalized_text(auth.username.as_deref()).is_some()
        || normalized_text(auth.password.as_deref()).is_some()
}

fn local_xml_name(name: &[u8]) -> String {
    String::from_utf8_lossy(name)
        .rsplit(':')
        .next()
        .unwrap_or_default()
        .to_ascii_lowercase()
}

fn json_array(value: Option<&Value>) -> Vec<Value> {
    match value {
        Some(Value::Array(items)) => items.clone(),
        Some(value) if !value.is_null() => vec![value.clone()],
        _ => Vec::new(),
    }
}

fn json_text(value: &Value, field: &str) -> String {
    value
        .get(field)
        .and_then(|value| {
            value
                .as_str()
                .map(str::to_string)
                .or_else(|| value.as_u64().map(|number| number.to_string()))
                .or_else(|| value.as_i64().map(|number| number.to_string()))
        })
        .unwrap_or_default()
}

fn value_text(value: &Value, field: &str) -> Option<String> {
    normalized_text(value.get(field).and_then(Value::as_str))
}

fn track_source_text(track: &Value, field: &str) -> Option<String> {
    normalized_text(
        track
            .get("source")
            .and_then(|source| source.get(field))
            .and_then(Value::as_str),
    )
}

fn json_u64(value: &Value, field: &str) -> u64 {
    value
        .get(field)
        .and_then(|value| {
            value
                .as_u64()
                .or_else(|| value.as_str().and_then(|text| text.parse::<u64>().ok()))
        })
        .unwrap_or(0)
}

fn json_u64_any(value: &Value, fields: &[&str]) -> u64 {
    fields
        .iter()
        .map(|field| json_u64(value, field))
        .find(|value| *value > 0)
        .unwrap_or_default()
}

fn subsonic_bitrate_bps(song: &Value) -> u64 {
    json_u64(song, "bitRate").saturating_mul(1_000)
}

fn first_nonzero_u64(values: &[u64]) -> u64 {
    values
        .iter()
        .copied()
        .find(|value| *value > 0)
        .unwrap_or_default()
}

fn json_f64(value: &Value, field: &str) -> f64 {
    value
        .get(field)
        .and_then(|value| {
            value
                .as_f64()
                .or_else(|| value.as_str().and_then(|text| text.parse::<f64>().ok()))
        })
        .filter(|value| value.is_finite() && *value >= 0.0)
        .unwrap_or(0.0)
}

fn optional_json_text(value: &str) -> Value {
    if value.trim().is_empty() {
        Value::Null
    } else {
        Value::String(value.trim().to_string())
    }
}

fn normalized_text(value: Option<&str>) -> Option<String> {
    value
        .map(str::trim)
        .filter(|text| !text.is_empty())
        .map(String::from)
}

fn basename_from_url(url: &Url) -> String {
    decoded_url_path_segments(url)
        .last()
        .cloned()
        .unwrap_or_else(|| basename_from_path(url.path()))
}

fn basename_from_path(path: &str) -> String {
    let normalized_path = path.replace('\\', "/");
    normalized_path
        .rsplit('/')
        .find(|segment| !segment.is_empty())
        .unwrap_or_default()
        .to_string()
}

fn extension_from_name(file_name: &str) -> String {
    file_name
        .rsplit('.')
        .next()
        .filter(|extension| extension != &file_name)
        .unwrap_or_default()
        .to_ascii_lowercase()
}

fn is_audio_resource(file_name: &str, content_type: &str) -> bool {
    audio_formats::is_supported_audio_content_type(content_type)
        || audio_formats::is_supported_audio_extension(&extension_from_name(file_name))
}

fn sanitize_track_title(file_name: &str) -> String {
    let stem = file_name
        .rsplit_once('.')
        .map(|(stem, _)| stem)
        .unwrap_or(file_name)
        .trim();

    if stem.is_empty() {
        String::from("Untitled")
    } else {
        stem.to_string()
    }
}

fn hex_value(byte: u8) -> Option<u8> {
    match byte {
        b'0'..=b'9' => Some(byte - b'0'),
        b'a'..=b'f' => Some(byte - b'a' + 10),
        b'A'..=b'F' => Some(byte - b'A' + 10),
        _ => None,
    }
}

fn hex_encode(bytes: &[u8]) -> String {
    bytes
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect::<Vec<_>>()
        .join("")
}

trait IfEmpty {
    fn if_empty<F>(self, fallback: F) -> String
    where
        F: FnOnce() -> String;
}

impl IfEmpty for String {
    fn if_empty<F>(self, fallback: F) -> String
    where
        F: FnOnce() -> String,
    {
        if self.trim().is_empty() {
            fallback()
        } else {
            self
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io;

    #[cfg(unix)]
    fn try_symlink_file(original: &Path, link: &Path) -> io::Result<()> {
        std::os::unix::fs::symlink(original, link)
    }

    #[cfg(windows)]
    fn try_symlink_file(original: &Path, link: &Path) -> io::Result<()> {
        std::os::windows::fs::symlink_file(original, link)
    }

    #[test]
    fn webdav_remote_resolution_prefers_origin_path_over_cached_path() {
        let track = json!({
            "source": {
                "path": "C:\\Users\\demo\\AppData\\Local\\OFPlayer\\cache\\track.flac",
                "originPath": "https://dav.example.test/music/track.flac",
                "remoteId": "https://dav.example.test/music/track.flac",
            }
        });

        assert_eq!(
            resolve_webdav_remote_track_url(&track).as_deref(),
            Some("https://dav.example.test/music/track.flac")
        );
    }

    #[test]
    fn webdav_remote_resolution_ignores_legacy_cached_path_when_origin_exists() {
        let track = json!({
            "source": {
                "kind": "external-cache",
                "path": "C:\\Users\\demo\\AppData\\Local\\OFPlayer\\cache\\external-sources\\webdav\\cached.mp3",
                "originPath": "https://dav.example.test/music/track.mp3",
                "remoteId": "https://dav.example.test/music/track.mp3",
            }
        });

        assert_eq!(
            resolve_webdav_remote_track_url(&track).as_deref(),
            Some("https://dav.example.test/music/track.mp3")
        );
    }

    #[test]
    fn cleanup_keeps_reusable_external_cache_sources() {
        let cache_dir = std::env::temp_dir().join(format!(
            "ofplayer-external-cache-test-{}",
            current_unix_nanos()
        ));
        fs::create_dir_all(&cache_dir).unwrap();
        let cache_path = cache_dir.join("external-cache-test.flac");
        fs::write(&cache_path, b"cached-audio").unwrap();
        let source = json!({
            "kind": "external-cache",
            "path": cache_path.to_string_lossy(),
            "transient": false,
            "deleteOnRelease": false,
        });

        cleanup_transient_playback_source(&source, &cache_dir);

        assert!(cache_path.exists());
        let _ = fs::remove_dir_all(cache_dir);
    }

    #[test]
    fn cleanup_removes_transient_sources_inside_cache_dir() {
        let cache_dir = std::env::temp_dir().join(format!(
            "ofplayer-external-transient-test-{}",
            current_unix_nanos()
        ));
        fs::create_dir_all(&cache_dir).unwrap();
        let cache_path = cache_dir.join("external-cache-test.flac");
        fs::write(&cache_path, b"cached-audio").unwrap();
        let source = json!({
            "kind": "external-temp",
            "path": cache_path.to_string_lossy(),
            "transient": true,
            "deleteOnRelease": true,
        });

        cleanup_transient_playback_source(&source, &cache_dir);

        assert!(!cache_path.exists());
        let _ = fs::remove_dir_all(cache_dir);
    }

    #[test]
    fn cleanup_refuses_transient_sources_outside_cache_dir() {
        let cache_dir = std::env::temp_dir().join(format!(
            "ofplayer-external-cache-root-test-{}",
            current_unix_nanos()
        ));
        let outside_dir = std::env::temp_dir().join(format!(
            "ofplayer-external-cache-outside-test-{}",
            current_unix_nanos()
        ));
        fs::create_dir_all(&cache_dir).unwrap();
        fs::create_dir_all(&outside_dir).unwrap();
        let outside_path = outside_dir.join("external-cache-test.flac");
        fs::write(&outside_path, b"cached-audio").unwrap();
        let source = json!({
            "kind": "external-temp",
            "path": outside_path.to_string_lossy(),
            "transient": true,
            "deleteOnRelease": true,
        });

        cleanup_transient_playback_source(&source, &cache_dir);

        assert!(outside_path.exists());
        let _ = fs::remove_dir_all(cache_dir);
        let _ = fs::remove_dir_all(outside_dir);
    }

    #[test]
    fn cleanup_refuses_symlink_escape_when_supported() {
        let cache_dir = std::env::temp_dir().join(format!(
            "ofplayer-external-cache-symlink-root-test-{}",
            current_unix_nanos()
        ));
        let outside_dir = std::env::temp_dir().join(format!(
            "ofplayer-external-cache-symlink-outside-test-{}",
            current_unix_nanos()
        ));
        fs::create_dir_all(&cache_dir).unwrap();
        fs::create_dir_all(&outside_dir).unwrap();
        let outside_path = outside_dir.join("external-cache-test.flac");
        let link_path = cache_dir.join("external-cache-link.flac");
        fs::write(&outside_path, b"cached-audio").unwrap();

        if try_symlink_file(&outside_path, &link_path).is_err() {
            let _ = fs::remove_dir_all(cache_dir);
            let _ = fs::remove_dir_all(outside_dir);
            return;
        }

        let source = json!({
            "kind": "external-temp",
            "path": link_path.to_string_lossy(),
            "transient": true,
            "deleteOnRelease": true,
        });

        cleanup_transient_playback_source(&source, &cache_dir);

        assert!(outside_path.exists());
        let _ = fs::remove_dir_all(cache_dir);
        let _ = fs::remove_dir_all(outside_dir);
    }

    #[test]
    fn normalize_endpoint_rejects_non_http_schemes() {
        assert!(normalize_endpoint("file:///tmp/music").is_err());
        assert!(normalize_endpoint("ftp://example.test/music").is_err());
        assert!(normalize_endpoint("https://example.test/music").is_ok());
    }

    #[test]
    fn same_url_origin_requires_scheme_host_and_port() {
        let root = Url::parse("https://example.test:8443/music/").unwrap();

        assert!(same_url_origin(
            &root,
            &Url::parse("https://example.test:8443/other/track.mp3").unwrap()
        ));
        assert!(!same_url_origin(
            &root,
            &Url::parse("http://example.test:8443/music/track.mp3").unwrap()
        ));
        assert!(!same_url_origin(
            &root,
            &Url::parse("https://example.test/music/track.mp3").unwrap()
        ));
        assert!(!same_url_origin(
            &root,
            &Url::parse("https://other.example.test:8443/music/track.mp3").unwrap()
        ));
    }

    #[test]
    fn webdav_root_url_is_normalized_as_directory() {
        let connection = ExternalLibraryConnection {
            provider: String::from(PROVIDER_WEBDAV),
            name: None,
            endpoint: String::from("https://example.test/dav"),
            root_path: Some(String::from("/music")),
            auth: None,
        };

        assert_eq!(
            resolve_webdav_root_url(&connection).unwrap().as_str(),
            "https://example.test/dav/music/"
        );
    }

    #[test]
    fn same_url_scope_rejects_sibling_paths() {
        let root = Url::parse("https://example.test/dav/music/").unwrap();

        assert!(same_url_scope(
            &root,
            &Url::parse("https://example.test/dav/music/album/track.mp3").unwrap()
        ));
        assert!(same_url_scope(
            &root,
            &Url::parse("https://example.test/dav/music/").unwrap()
        ));
        assert!(!same_url_scope(
            &root,
            &Url::parse("https://example.test/dav/music-other/track.mp3").unwrap()
        ));
        assert!(!same_url_scope(
            &root,
            &Url::parse("https://example.test/dav/other/track.mp3").unwrap()
        ));
    }

    #[test]
    fn subsonic_bitrate_is_normalized_from_kbps_to_bps() {
        assert_eq!(subsonic_bitrate_bps(&json!({ "bitRate": 1530 })), 1_530_000);
        assert_eq!(
            subsonic_bitrate_bps(&json!({ "bitRate": "1470" })),
            1_470_000
        );
        assert_eq!(subsonic_bitrate_bps(&json!({})), 0);
    }
}
