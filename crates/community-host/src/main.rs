mod session;
use ofplayer_community_core::app_paths;
use serde_json::{json, Value};
use std::{
    io::{self, BufRead, Read, Write},
    path::PathBuf,
    sync::mpsc,
    time::{Duration, Instant},
};

const MAX_LINE: u64 = 1024 * 1024;
fn output(value: Value) -> Result<(), String> {
    let stdout = io::stdout();
    let mut writer = stdout.lock();
    serde_json::to_writer(&mut writer, &value).map_err(|e| e.to_string())?;
    writer
        .write_all(b"\n")
        .and_then(|_| writer.flush())
        .map_err(|e| e.to_string())
}
fn run() -> Result<(), String> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args == ["--version"] {
        eprintln!("OFPlayer Community native host (development)");
        return Ok(());
    }
    if args.len() == 3 && args[0] == "--migrate-tauri" {
        return output(ofplayer_community_core::migration::migrate(
            std::path::Path::new(&args[1]),
            std::path::Path::new(&args[2]),
        )?);
    }
    if args.len() != 2 || args[0] != "--data-root" {
        return Err("Usage: ofplayer-community-host --data-root <directory>".into());
    }
    let root = PathBuf::from(&args[1]);
    if !root.is_absolute() {
        return Err("Data root must be absolute".into());
    }
    // Never let a development host select either old Tauri or commercial WinUI roots.
    let text = root
        .to_string_lossy()
        .replace('/', "\\")
        .trim_end_matches('\\')
        .to_lowercase();
    if let Some(local) = std::env::var_os("LOCALAPPDATA") {
        for directory in ["ofplayer", "ofplayer-winui", "OFPlayer.Video"] {
            let reserved = PathBuf::from(&local)
                .join(directory)
                .to_string_lossy()
                .replace('/', "\\")
                .to_lowercase();
            if text == reserved {
                return Err("Reserved existing application data root".into());
            }
        }
    }
    app_paths::set_data_root_override(&root)?;
    let _root_lock = app_paths::acquire_data_root_lock(&root)?;
    let mut session = session::Session::open()?;
    let session_id = uuid::Uuid::new_v4().to_string();
    let (sender, receiver) = mpsc::sync_channel::<Result<Value, String>>(64);
    std::thread::spawn(move || {
        let stdin = io::stdin();
        let mut reader = stdin.lock();
        loop {
            let mut bytes = Vec::new();
            let read = reader
                .by_ref()
                .take(MAX_LINE + 1)
                .read_until(b'\n', &mut bytes);
            let message = match read {
                Ok(0) => break,
                Ok(_) if bytes.len() as u64 > MAX_LINE => Err("request-too-large".into()),
                Ok(_) => serde_json::from_slice(&bytes).map_err(|_| "invalid-json".into()),
                Err(_) => Err("stdin-read-failed".into()),
            };
            let failed = message.is_err();
            if sender.send(message).is_err() || failed {
                break;
            }
        }
    });
    let mut sequence = 0u64;
    let (import_sender, import_receiver) = mpsc::channel::<(Value, Result<Value, String>, bool)>();
    let mut importing = false;
    let mut network_jobs = 0usize;
    let (progress_sender, progress_receiver) = mpsc::channel::<Value>();
    let mut last_event = Instant::now();
    let mut last_persist = Instant::now();
    loop {
        match receiver.recv_timeout(Duration::from_millis(50)) {
            Ok(Ok(request)) => {
                let id = request.get("id").cloned().unwrap_or(Value::Null);
                let command = request.get("command").and_then(Value::as_str).unwrap_or("");
                if matches!(
                    command,
                    "import.files"
                        | "desktop_library_import_files"
                        | "desktop_library_scan_import"
                        | "desktop_playlist_import_m3u"
                        | "desktop_catalog_refresh_browse_metadata"
                ) && request.get("protocolVersion").and_then(Value::as_u64) == Some(1)
                {
                    if importing {
                        output(
                            json!({"protocolVersion":1,"id":id,"ok":false,"error":{"code":"busy","message":"An import is already in progress"}}),
                        )?;
                    } else {
                        importing = true;
                        session::frontend::IMPORT_CANCELLED
                            .store(false, std::sync::atomic::Ordering::Relaxed);
                        let command = command.to_string();
                        let progress = progress_sender.clone();
                        let done = import_sender.clone();
                        let args = request.get("args").cloned().unwrap_or(json!({}));
                        std::thread::spawn(move || {
                            let result = if command == "import.files" {
                                session::import_files(args)
                            } else {
                                session::frontend::import_job(&command, args, |v| {
                                    let _ = progress.send(v);
                                })
                            };
                            let _ = done.send((id, result, true));
                        });
                    }
                    continue;
                }
                if matches!(
                    command,
                    "external_library_test_connection"
                        | "external_library_list_libraries"
                        | "external_library_list_tracks"
                        | "external_library_resolve_playback_source"
                ) && request.get("protocolVersion").and_then(Value::as_u64) == Some(1)
                {
                    if network_jobs >= 4 {
                        output(
                            json!({"protocolVersion":1,"id":id,"ok":false,"error":{"code":"busy","message":"Too many network requests"}}),
                        )?;
                    } else {
                        network_jobs += 1;
                        let done = import_sender.clone();
                        let command = command.to_string();
                        let args = request.get("args").cloned().unwrap_or(json!({}));
                        std::thread::spawn(move || {
                            let result = session::frontend::network_job(&command, args);
                            let _ = done.send((id, result, false));
                        });
                    }
                    continue;
                }
                let result = if request.get("protocolVersion").and_then(Value::as_u64) != Some(1) {
                    Err("unsupported-protocol-version".into())
                } else if matches!(command, "host_handshake" | "handshake") {
                    Ok(
                        json!({"protocolVersion":1,"edition":"community","hostVersion":"0.0.0-dev","sessionId":session_id,"pid":std::process::id(),"dataRoot":root,"capabilities":["local.catalog","local.playback","local.lyrics","local.video.preferences"]}),
                    )
                } else {
                    session.command(
                        command,
                        request.get("args").cloned().unwrap_or_else(|| json!({})),
                    )
                };
                match result {
                    Ok(data) => output(json!({"protocolVersion":1,"id":id,"ok":true,"data":data}))?,
                    Err(message) => output(
                        json!({"protocolVersion":1,"id":id,"ok":false,"error":{"code":if message=="unknown-command" {"unknown-command"} else {"command-failed"},"message":message}}),
                    )?,
                }
                if command == "host_shutdown" {
                    break;
                }
            }
            Ok(Err(error)) => return Err(error),
            Err(mpsc::RecvTimeoutError::Disconnected) => break,
            Err(mpsc::RecvTimeoutError::Timeout) => (),
        }
        if let Ok((id, result, refresh)) = import_receiver.try_recv() {
            if refresh {
                importing = false;
            } else {
                network_jobs = network_jobs.saturating_sub(1);
            }
            // The worker has its own SQLite connection; replace the hot read cache
            // before exposing its completion to the UI, preserving the live player.
            let result = result.and_then(|data| {
                if refresh {
                    session.import_completed()?;
                }
                Ok(data)
            });
            match result {
                Ok(data) => output(json!({"protocolVersion":1,"id":id,"ok":true,"data":data}))?,
                Err(message) => output(
                    json!({"protocolVersion":1,"id":id,"ok":false,"error":{"code":"command-failed","message":message}}),
                )?,
            }
        }
        while let Ok(data) = progress_receiver.try_recv() {
            sequence += 1;
            output(
                json!({"event":"import.progress","sessionId":session_id,"seq":sequence,"data":data}),
            )?;
        }
        session.tick()?;
        for data in session.drain_watch() {
            sequence += 1;
            output(
                json!({"event":"storage.watch-changed","sessionId":session_id,"seq":sequence,"data":data}),
            )?;
        }
        if last_event.elapsed() >= Duration::from_millis(200) {
            sequence += 1;
            output(
                json!({"event":"playback.state","sessionId":session_id,"seq":sequence,"data":session.snapshot()?}),
            )?;
            last_event = Instant::now();
        }
        if last_persist.elapsed() >= Duration::from_secs(3) {
            session.persist()?;
            last_persist = Instant::now();
        }
    }
    session.shutdown()
}
fn main() {
    if let Err(error) = run() {
        eprintln!("[community-host] {error}");
        std::process::exit(1);
    }
}
