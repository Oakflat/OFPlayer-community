//! Mainline WinUI's local command contract, backed by Community-owned state.
//! No account, license, pairing, streaming, Together or hosted-cloud commands.
use super::*;
use ofplayer_community_core::{app_paths, external_sources, session_ops::QueueEdit};
use std::sync::atomic::{AtomicBool, Ordering};
pub static IMPORT_CANCELLED: AtomicBool = AtomicBool::new(false);

impl Session {
    pub fn frontend_command(&mut self, command: &str, args: Value) -> Result<Value, String> {
        let alias = match command {
            "desktop_catalog_query_collection_tracks" => "catalog.query",
            "desktop_catalog_resolve_navigation" => "catalog.navigation",
            "desktop_library_create" => "library.create",
            "desktop_library_rename" => "library.rename",
            "desktop_library_delete" => "library.delete",
            "desktop_playlist_create" => "playlist.create",
            "desktop_playlist_rename" => "playlist.rename",
            "desktop_playlist_delete" => "playlist.delete",
            "desktop_playlist_add_track" => "playlist.add",
            "desktop_playlist_remove_track" => "playlist.remove",
            "desktop_track_set_favorite" => "track.favorite",
            "desktop_tracks_delete_from_library" => "track.delete",
            "playback_session_select_track" => "session.select",
            "playback_session_set_queue" => "session.queue",
            "playback_session_play_current" => "playback.play",
            "playback_session_pause" | "playback_pause_loaded" => "playback.pause",
            "playback_seek" | "playback_seek_loaded" => "playback.seek",
            "playback_set_volume" => "playback.volume",
            "playback_list_output_devices" => "output.devices",
            "playback_set_output_device" => "output.select",
            _ => "",
        };
        if !alias.is_empty() {
            return self.command(alias, args);
        }
        match command {
            "desktop_catalog_load_structure" => self.state.load_catalog_structure(),
            "desktop_catalog_get_track" => encode(self.state.get_track(
                text(&args, "trackId")?,
                args["includeArtwork"].as_bool().unwrap_or(true),
            )?),
            "desktop_catalog_group_summary" => self.state.frontend_groups(
                args["activeLibrary"].as_str(),
                text(&args, "groupKind")?,
                args["artistFilter"].as_str(),
            ),
            "desktop_catalog_query_group_tracks" => {
                encode(self.state.frontend_group_tracks(&decode(args)?)?)
            }
            "desktop_catalog_update_track" => encode(
                self.state
                    .update_track(text(&args, "trackId")?, &args["patch"])?,
            ),
            "desktop_catalog_local_paths" => {
                let ids: Vec<String> = decode(args["trackIds"].clone())?;
                let mut paths = Vec::new();
                for id in ids {
                    if let Some(t) = self.state.get_track(&id, false)? {
                        if let Some(p) = t["source"]["path"].as_str() {
                            paths.push(json!({"id":id,"path":p}));
                        }
                    }
                }
                Ok(json!(paths))
            }
            "desktop_catalog_relink_local_track" => {
                let id = text(&args, "trackId")?;
                let path = local_file(text(&args, "path")?)?;
                let mut track = self.state.get_track(id, false)?.ok_or("Track not found")?;
                track["source"]["path"] = json!(path.to_string_lossy());
                track["source"]["originalPath"] = json!(path.to_string_lossy());
                encode(self.state.update_track(id, &track)?)
            }
            "desktop_library_confirm_missing_cleanup" => {
                let scan = self
                    .state
                    .frontend_load_json("community.missingScan")?
                    .ok_or("Missing scan expired")?;
                if scan["id"] != args["missingScanId"] || scan["libraryId"] != args["libraryId"] {
                    return Err("Missing scan changed".into());
                }
                self.state
                    .frontend_save_json("community.missingScan", &Value::Null)?;
                let requested: Vec<String> = decode(args["trackIds"].clone())?;
                let mut ids = Vec::new();
                let mut skipped = Vec::new();
                for id in requested {
                    let candidate = scan["tracks"]
                        .as_array()
                        .and_then(|v| v.iter().find(|t| t["trackId"].as_str() == Some(&id)));
                    let current = self.state.get_track(&id, false)?;
                    if let (Some(old), Some(track)) = (candidate, current) {
                        let path = track["source"]["path"].as_str().unwrap_or("");
                        if track["libraryId"] == scan["libraryId"]
                            && old["sourcePath"].as_str() == Some(path)
                            && matches!(std::fs::metadata(path),Err(e) if e.kind()==std::io::ErrorKind::NotFound)
                        {
                            ids.push(id);
                            continue;
                        }
                    }
                    skipped.push(id);
                }
                let result = self
                    .state
                    .delete_tracks_from_library(&TrackBatchDeleteRequest { track_ids: ids })?;
                self.reconcile()?;
                let mut value = encode(result)?;
                value["skippedTrackIds"] = json!(skipped);
                value["session"] = encode(self.state.load_session_snapshot()?)?;
                Ok(value)
            }
            "desktop_library_reorder" => encode(self.state.reorder_libraries(&decode(args)?)?),
            "desktop_playlist_reorder" => encode(self.state.reorder_playlists(&decode(args)?)?),
            "desktop_playlist_reorder_tracks" => {
                encode(self.state.reorder_playlist_tracks(&decode(args)?)?)
            }
            "desktop_preferences_load" => {
                Ok(json!({"preferences":self.state.load_preferences()?.unwrap_or(json!({}))}))
            }
            "desktop_preferences_save" => self.command("preferences.patch", args["patch"].clone()),
            "desktop_history_load_stats" => encode(self.state.load_listening_stats(&decode(
                args.get("request").cloned().unwrap_or(json!({})),
            )?)?),
            "desktop_storage_analyze" => encode(self.state.analyze_storage_usage()?),
            "desktop_storage_collect_garbage" => encode(self.state.collect_storage_garbage()?),
            "desktop_state_reset_all_data" => {
                self.player.reset();
                encode(self.state.reset_all_data()?)
            }
            "playback_snapshot" => encode(self.player.snapshot()),
            "playback_session_snapshot" => encode(self.state.load_session_snapshot()?),
            "playback_session_sync_catalog" => {
                let saved = self.state.sync_session_with_catalog()?;
                if let Some(id) = saved.current_track_id {
                    if self.player.snapshot().active_track_id.is_none()
                        && self.select(&id, None, false, saved.current_time).is_ok()
                    {
                        return self.snapshot();
                    }
                }
                self.snapshot()
            }
            "playback_session_edit_queue" => {
                let action = QueueEdit::parse(text(&args, "action")?)?;
                let ids: Vec<String> = decode(args["trackIds"].clone())?;
                let revision = args["expectedQueueRevision"]
                    .as_u64()
                    .ok_or("Missing queue revision")?;
                let (session, edited) = self.state.frontend_edit_queue(
                    action,
                    &ids,
                    revision,
                    args["expectedTrackId"].as_str(),
                )?;
                Ok(json!({"session":session,"editedTrackIds":edited}))
            }
            "playback_recover_output" => encode(self.player.recover_output()?),
            "playback_play_loaded" => encode(self.player.play()?),
            "playback_play_external" => {
                let path = local_file(text(&args, "path")?)?;
                self.player.load_track(decode(json!({"trackId":"external","path":path.to_string_lossy(),"autoplay":true,"deleteOnRelease":false,"media":{"title":args["title"],"artist":args["artist"]}}))?)?;
                self.external_track = Some(
                    json!({"id":"external","title":args["title"],"artist":args["artist"],"source":{"path":path.to_string_lossy()}}),
                );
                self.music_suspended = false;
                self.snapshot()
            }
            "lyrics_resolve_track" => {
                let id = text(&args, "trackId")?;
                let track = if id == "external" {
                    self.external_track.clone()
                } else {
                    self.state.get_track(id, false)?
                }
                .ok_or("Track not found")?;
                let prefs = self.state.load_preferences()?.unwrap_or(json!({}));
                let request = decode(
                    json!({"trackId":id,"audioPath":track["source"]["path"],"originPath":track["source"]["originalPath"],"title":track["title"],"artist":track["artist"],"album":track["album"],"fileName":track["fileName"],"lyricsPath":track["lyricsPath"],"lyricsDirectories":prefs["lyricsScanDirectories"],"positionSeconds":args["positionSeconds"]}),
                )?;
                encode(lyrics::resolve_track_lyrics(request)?)
            }
            "desktop_external_library_load_connections" => Ok(self
                .state
                .frontend_load_json("externalLibraries.connections")?
                .unwrap_or(json!([]))),
            "desktop_external_library_put_connection" => {
                let value = &args["value"];
                let id = text(value, "id")?;
                if !matches!(
                    text(value, "provider")?,
                    "webdav" | "subsonic" | "navidrome"
                ) {
                    return Err("Unsupported provider".into());
                }
                let mut list = self
                    .state
                    .frontend_load_json("externalLibraries.connections")?
                    .unwrap_or(json!([]));
                let entries = list.as_array_mut().ok_or("Invalid saved connections")?;
                entries.retain(|v| v["id"].as_str() != Some(id));
                entries.push(value.clone());
                self.state
                    .frontend_save_json("externalLibraries.connections", &list)?;
                Ok(value.clone())
            }
            "desktop_external_library_delete_connection" => {
                let id = text(&args, "connectionId")?;
                let mut list = self
                    .state
                    .frontend_load_json("externalLibraries.connections")?
                    .unwrap_or(json!([]));
                list.as_array_mut()
                    .ok_or("Invalid saved connections")?
                    .retain(|v| v["id"].as_str() != Some(id));
                self.state
                    .frontend_save_json("externalLibraries.connections", &list)?;
                Ok(list)
            }
            "playback_get_audio_engine" => Ok(
                json!({"activeEngine":"default","preferredEngine":"default","engines":[{"id":"default","name":"Windows default","available":true}],"error":null}),
            ),
            "playback_set_audio_engine" => {
                if args["engine"].as_str() != Some("default") {
                    return Err("Audio engine unavailable in this build".into());
                }
                self.frontend_command("playback_get_audio_engine", json!({}))
            }
            "storage_configure_watch" => {
                let sender = self.watch_sender.clone();
                encode(self.watch.configure_with_sink(
                    std::sync::Arc::new(move |kind, paths| {
                        let _ = sender.send(json!({"kind":kind,"paths":paths}));
                    }),
                    decode(args["request"].clone())?,
                )?)
            }
            "desktop_import_cancel" => {
                IMPORT_CANCELLED.store(true, Ordering::Relaxed);
                Ok(json!({"cancelRequested":true}))
            }
            "diagnostics_log_status" => Ok(json!({"enabled":false,"queued":0})),
            "diagnostics_log_event" => Ok(json!({"recorded":false})),
            _ => Err("unknown-command".into()),
        }
    }
}

pub fn network_job(command: &str, args: Value) -> Result<Value, String> {
    let rt = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(|e| e.to_string())?;
    let request = args.get("request").cloned().unwrap_or(args);
    rt.block_on(async {
        match command {
            "external_library_test_connection" => {
                encode(external_sources::test_connection(decode(request)?).await?)
            }
            "external_library_list_libraries" => {
                encode(external_sources::list_libraries(decode(request)?).await?)
            }
            "external_library_list_tracks" => {
                encode(external_sources::list_tracks(decode(request)?).await?)
            }
            "external_library_resolve_playback_source" => encode(
                external_sources::resolve_playback_source(
                    decode(request)?,
                    app_paths::cache_dir()?.join("external-playback"),
                )
                .await?,
            ),
            _ => Err("unknown-command".into()),
        }
    })
}

pub fn import_job(
    command: &str,
    mut args: Value,
    progress: impl Fn(Value),
) -> Result<Value, String> {
    let library = text(&args, "libraryId")?.to_string();
    let mut state = DesktopStateStore::default();
    state.initialize()?;
    let mut playlist_name = None;
    if command == "desktop_catalog_refresh_browse_metadata" {
        let catalog = state.load_catalog_snapshot(Some("none"))?;
        let mut updated = 0;
        for track in catalog
            .tracks
            .iter()
            .filter(|t| t["libraryId"].as_str() == Some(&library))
        {
            if IMPORT_CANCELLED.load(Ordering::Relaxed) {
                break;
            }
            if track["communityBrowseVersion"].as_u64() == Some(1) {
                continue;
            }
            let artist = track["artist"].as_str().unwrap_or("");
            let modified = track["source"]["path"].as_str().and_then(|p| {
                ofplayer_community_core::catalog_browse::modified_at_ms(Path::new(p))
            });
            state.update_track(text(track,"id")?,&json!({"artists":ofplayer_community_core::catalog_browse::artist_names([artist]),"modifiedAtMs":modified,"communityBrowseVersion":1}))?;
            updated += 1;
        }
        return Ok(
            json!({"updatedMetadataCount":updated,"importedTracks":[],"skippedCount":0,"skippedFiles":[],"cancelled":IMPORT_CANCELLED.load(Ordering::Relaxed)}),
        );
    }
    if command == "desktop_library_scan_import" {
        let scan = storage::scan_audio_files_with_progress(
            decode(json!({"directories":args["directories"]}))?,
            |_| {},
        )?;
        args["files"] = json!(scan
            .files
            .into_iter()
            .map(|f| json!({"sourcePath":f.path,"fileName":f.file_name}))
            .collect::<Vec<_>>());
    } else if command == "desktop_playlist_import_m3u" {
        let path = local_file(text(&args, "path")?)?;
        let parent = path.parent().ok_or("Playlist has no parent")?;
        let contents = std::fs::read_to_string(&path).map_err(|e| e.to_string())?;
        let files=contents.trim_start_matches('\u{feff}').lines().map(str::trim).filter(|s|!s.is_empty()&&!s.starts_with('#')).map(|s|{let p=Path::new(s);let p=if p.is_absolute(){p.to_path_buf()}else{parent.join(p)};json!({"sourcePath":p.to_string_lossy(),"fileName":p.file_name().map(|s|s.to_string_lossy())})}).collect::<Vec<_>>();
        args["files"] = json!(files);
        playlist_name = Some(
            path.file_stem()
                .unwrap_or_default()
                .to_string_lossy()
                .to_string(),
        );
    }
    let files: Vec<LibraryImportFileInput> = decode(args["files"].clone())?;
    if files.len() > 20000 {
        return Err("Too many files in one import".into());
    }
    let candidates = state.filter_library_import_candidates(&LibraryImportCandidatesRequest {
        library_id: library.clone(),
        files: files.clone(),
        respect_deleted_import_paths: args["respectDeletedImportPaths"].as_bool(),
    })?;
    let candidate_value = encode(candidates)?;
    let candidate_files = candidate_value.as_array().cloned().unwrap_or_default();
    let mut imported = Vec::new();
    let mut skipped = Vec::new();
    for (i, file) in candidate_files.iter().enumerate() {
        if IMPORT_CANCELLED.load(Ordering::Relaxed) {
            break;
        }
        match super::import_files(json!({"libraryId":library,"files":[file]})) {
            Ok(value) => {
                if let Some(items) = value
                    .as_array()
                    .or_else(|| value["importedTracks"].as_array())
                {
                    imported.extend(items.iter().cloned());
                }
            }
            Err(error) => skipped.push(json!({"fileName":file["fileName"],"reason":error})),
        }
        progress(
            json!({"phase":"importing","processed":i+1,"total":candidate_files.len(),"percent":((i+1)as f64/candidate_files.len().max(1)as f64)*100.0,"currentFile":file["fileName"]}),
        );
    }
    let mut result = json!({"importedTracks":imported,"skippedCount":skipped.len(),"skippedFiles":skipped,"cancelled":IMPORT_CANCELLED.load(Ordering::Relaxed)});
    if let Some(name) = playlist_name {
        let playlist = state.create_playlist(&decode(json!({"libraryId":library,"name":name}))?)?;
        let id = playlist
            .get("id")
            .or_else(|| playlist.get("playlist").and_then(|p| p.get("id")))
            .and_then(Value::as_str)
            .ok_or("Playlist creation returned no id")?;
        let catalog = state.load_catalog_snapshot(Some("none"))?;
        for file in &files {
            if let Some(track) = catalog.tracks.iter().find(|t| {
                t["source"]["path"].as_str() == Some(&file.source_path)
                    || t["source"]["originalPath"].as_str() == Some(&file.source_path)
            }) {
                state.add_track_to_playlist(&decode(
                    json!({"playlistId":id,"trackId":track["id"]}),
                )?)?;
            }
        }
        result["playlist"] = playlist;
    }
    if command == "desktop_library_scan_import" && !IMPORT_CANCELLED.load(Ordering::Relaxed) {
        let dirs: Vec<String> = decode(args["directories"].clone())?;
        let catalog = state.load_catalog_snapshot(Some("none"))?;
        let mut missing = Vec::new();
        for track in catalog
            .tracks
            .iter()
            .filter(|t| t["libraryId"].as_str() == Some(&library))
        {
            let Some(path) = track["source"]["path"].as_str() else {
                continue;
            };
            if !dirs.iter().any(|d| Path::new(path).starts_with(d)) {
                continue;
            }
            if !matches!(std::fs::metadata(path),Err(e) if e.kind()==std::io::ErrorKind::NotFound) {
                continue;
            }
            let id = text(track, "id")?;
            missing.push(json!({"trackId":id,"title":track["title"],"sourcePath":path,"playlistRelationCount":catalog.playlist_track_relations.iter().filter(|p|p["trackId"].as_str()==Some(id)).count()}));
        }
        let token = uuid::Uuid::new_v4().to_string();
        state.frontend_save_json(
            "community.missingScan",
            &json!({"id":token,"libraryId":library,"tracks":missing}),
        )?;
        result["missingScanId"] = json!(token);
        result["missingTotal"] = json!(missing.len());
        result["missingTracks"] = json!(missing);
    }
    Ok(result)
}
