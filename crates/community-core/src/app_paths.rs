use std::{
    fs::{self, File, OpenOptions},
    io::{ErrorKind, Write},
    path::{Path, PathBuf},
    sync::OnceLock,
};

pub const APP_IDENTIFIER: &str = "org.ofplayer.community";

const APP_DATA_DIR_NAME: &str = "ofplayer-community";
const CACHE_DIR_NAME: &str = "cache";
const DIAGNOSTICS_DIR_NAME: &str = "diagnostics";
const DATA_ROOT_LOCK_FILE_NAME: &str = ".ofplayer-data-root.lock";

/// 进程级数据根覆盖（原生宿主 W1.1）。未设置时保持原 Tauri 正式路径行为不变。
static DATA_ROOT_OVERRIDE: OnceLock<PathBuf> = OnceLock::new();

/// 正式数据根路径（只计算，不创建目录）。用于宿主拒绝对正式根的默认写入。
pub fn formal_data_dir() -> Result<PathBuf, String> {
    Ok(local_app_data_root()?.join(APP_DATA_DIR_NAME))
}

/// 设置显式数据根覆盖。全进程仅允许设置一次，重复设置直接报错。
pub fn set_data_root_override(root: &Path) -> Result<(), String> {
    if DATA_ROOT_OVERRIDE.get().is_some() {
        return Err(String::from(
            "The OFPlayer data root override has already been set for this process.",
        ));
    }

    DATA_ROOT_OVERRIDE
        .set(root.to_path_buf())
        .map_err(|_| String::from("Failed to set the OFPlayer data root override."))
}

pub fn data_root_override() -> Option<&'static Path> {
    DATA_ROOT_OVERRIDE.get().map(PathBuf::as_path)
}

/// 判断给定路径是否指向正式数据根（规范化后比较，Windows 下大小写不敏感）。
pub fn path_is_formal_data_root(path: &Path) -> bool {
    let Ok(formal) = formal_data_dir() else {
        return false;
    };

    paths_refer_to_same_location(path, &formal)
}

fn paths_refer_to_same_location(left: &Path, right: &Path) -> bool {
    let left = left.canonicalize().unwrap_or_else(|_| left.to_path_buf());
    let right = right.canonicalize().unwrap_or_else(|_| right.to_path_buf());

    normalize_path_text(&left).eq_ignore_ascii_case(&normalize_path_text(&right))
}

fn normalize_path_text(path: &Path) -> String {
    let mut text = path.to_string_lossy().replace('/', "\\");

    while text.len() > 3 && text.ends_with('\\') {
        text.pop();
    }

    text
}

pub fn data_dir() -> Result<PathBuf, String> {
    match data_root_override() {
        Some(root) => ensure_writable_directory(root),
        None => ensure_writable_directory(&formal_data_dir()?),
    }
}

/// 跨进程数据根独占锁。持锁期间第二实例打开同一数据根会收到共享冲突并明确拒绝；
/// 进程异常退出时由操作系统回收句柄，锁自动释放，不存在需要清理的陈旧锁文件。
pub struct DataRootLock {
    _file: File,
    pub path: PathBuf,
}

pub fn acquire_data_root_lock(data_root: &Path) -> Result<DataRootLock, String> {
    let root = ensure_writable_directory(data_root)?;
    let lock_path = root.join(DATA_ROOT_LOCK_FILE_NAME);
    let mut options = OpenOptions::new();
    options.read(true).write(true).create(true).truncate(false);

    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        // 共享模式 0：任何其他进程都无法同时打开该文件，打开失败即为“已有实例持有”。
        options.share_mode(0);
    }

    let mut file = options.open(&lock_path).map_err(|error| {
        if error.raw_os_error() == Some(32) {
            format!(
                "The OFPlayer data root is already locked by another process: '{}'.",
                root.display()
            )
        } else {
            format!(
                "Failed to open the OFPlayer data root lock '{}': {error}",
                lock_path.display()
            )
        }
    })?;

    let _ = file.set_len(0);
    let _ = writeln!(file, "pid={}", std::process::id());
    let _ = file.sync_all();

    Ok(DataRootLock {
        _file: file,
        path: lock_path,
    })
}

pub fn cache_dir() -> Result<PathBuf, String> {
    ensure_writable_directory(&data_dir()?.join(CACHE_DIR_NAME))
}

pub fn cache_subdir(name: &str) -> Result<PathBuf, String> {
    ensure_writable_directory(&cache_dir()?.join(name))
}

pub fn diagnostics_dir() -> Result<PathBuf, String> {
    ensure_writable_directory(&data_dir()?.join(DIAGNOSTICS_DIR_NAME))
}

pub fn state_dir() -> Result<PathBuf, String> {
    data_dir()
}

pub fn webview_data_dir(_label: &str) -> Result<PathBuf, String> {
    data_dir()
}

pub fn prepare_runtime_data_dirs() -> Result<(), String> {
    let _ = data_dir()?;
    let _ = cache_dir()?;
    let _ = diagnostics_dir()?;
    Ok(())
}

fn local_app_data_root() -> Result<PathBuf, String> {
    #[cfg(windows)]
    {
        std::env::var_os("LOCALAPPDATA")
            .map(PathBuf::from)
            .ok_or_else(|| String::from("Failed to resolve LOCALAPPDATA for OFPlayer."))
    }

    #[cfg(target_os = "macos")]
    {
        std::env::var_os("HOME")
            .map(PathBuf::from)
            .map(|home| home.join("Library").join("Application Support"))
            .ok_or_else(|| String::from("Failed to resolve HOME for OFPlayer app data."))
    }

    #[cfg(all(not(windows), not(target_os = "macos")))]
    {
        if let Some(path) = std::env::var_os("XDG_DATA_HOME").map(PathBuf::from) {
            return Ok(path);
        }

        std::env::var_os("HOME")
            .map(PathBuf::from)
            .map(|home| home.join(".local").join("share"))
            .ok_or_else(|| String::from("Failed to resolve HOME for OFPlayer app data."))
    }
}

pub fn ensure_writable_directory(directory: &Path) -> Result<PathBuf, String> {
    fs::create_dir_all(directory).map_err(|error| {
        format!(
            "Failed to create the OFPlayer data directory '{}': {error}",
            directory.display()
        )
    })?;

    let probe_path = directory.join(".ofplayer-write-test");
    match OpenOptions::new()
        .create(true)
        .write(true)
        .truncate(true)
        .open(&probe_path)
    {
        Ok(mut file) => {
            file.write_all(b"ok").map_err(|error| {
                format!(
                    "Failed to write the OFPlayer data probe '{}': {error}",
                    probe_path.display()
                )
            })?;
            drop(file);
            match fs::remove_file(&probe_path) {
                Ok(()) => {}
                Err(error) if error.kind() == ErrorKind::NotFound => {}
                Err(error) => {
                    return Err(format!(
                        "Failed to remove the OFPlayer data probe '{}': {error}",
                        probe_path.display()
                    ))
                }
            }
            Ok(directory.to_path_buf())
        }
        Err(error) => Err(format!(
            "Failed to open the OFPlayer data probe '{}': {error}",
            probe_path.display()
        )),
    }
}
