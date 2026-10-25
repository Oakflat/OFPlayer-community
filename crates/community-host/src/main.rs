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
    let (import_sender, import_receiver) = mpsc::channel::<(Value, Result<Value, String>)>();
    let mut importing = false;
    let mut last_event = Instant::now();
    let mut last_persist = Instant::now();
    loop {
        match receiver.recv_timeout(Duration::from_millis(50)) {
            Ok(Ok(request)) => {
                let id = request.get("id").cloned().unwrap_or(Value::Null);
                let command = request.get("command").and_then(Value::as_str).unwrap_or("");
                if command == "import.files"
                    && request.get("protocolVersion").and_then(Value::as_u64) == Some(1)
                {
                    if importing {
                        output(
                            json!({"protocolVersion":1,"id":id,"ok":false,"error":{"code":"busy","message":"An import is already in progress"}}),
                        )?;
                    } else {
                        importing = true;
                        let done = import_sender.clone();
                        let args = request.get("args").cloned().unwrap_or(json!({}));
                        std::thread::spawn(move || {
                            let result = session::import_files(args);
                            let _ = done.send((id, result));
                        });
                    }
                    continue;
                }
                let result = if request.get("protocolVersion").and_then(Value::as_u64) != Some(1) {
                    Err("unsupported-protocol-version".into())
                } else if command == "host_handshake" {
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
        if let Ok((id, result)) = import_receiver.try_recv() {
            importing = false;
            // The worker has its own SQLite connection; replace the hot read cache
            // before exposing its completion to the UI, preserving the live player.
            let result = result.and_then(|data| {
                session.import_completed()?;
                Ok(data)
            });
            match result {
                Ok(data) => output(json!({"protocolVersion":1,"id":id,"ok":true,"data":data}))?,
                Err(message) => output(
                    json!({"protocolVersion":1,"id":id,"ok":false,"error":{"code":"command-failed","message":message}}),
                )?,
            }
        }
        session.tick()?;
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
