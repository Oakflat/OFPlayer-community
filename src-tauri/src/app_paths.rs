use std::{
    fs::{self, OpenOptions},
    io::{ErrorKind, Write},
    path::{Path, PathBuf},
};

pub const APP_IDENTIFIER: &str = "org.ofplayer.community";

const APP_DATA_DIR_NAME: &str = "ofplayer";
const CACHE_DIR_NAME: &str = "cache";
const DIAGNOSTICS_DIR_NAME: &str = "diagnostics";

pub fn data_dir() -> Result<PathBuf, String> {
    ensure_writable_directory(&local_app_data_root()?.join(APP_DATA_DIR_NAME))
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
