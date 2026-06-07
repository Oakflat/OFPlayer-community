# ASIO and DSD playback notes

## Scope

This slice adds the app-side plumbing for ASIO output selection and DSF/DFF import/playback. It intentionally does not add SACD ISO handling, DST decompression, DRM bypass, or copyright-specific workflows.

## DSD

- Import accepts `.dsf` and `.dff` from native dialogs, folder scans, and shared Rust format checks.
- Metadata reads DSF/DFF container timing through `dsd-reader`; unsupported or damaged containers still fall back to filename metadata like other recoverable metadata failures.
- Playback uses a first-pass DSD-to-PCM `f32` source for the existing Rodio mixer path. It reports the source as `dsd64-to-pcm-f32`, `dsd128-to-pcm-f32`, etc. DSD seeking is not enabled yet; a seek or resume into the middle starts from the beginning with a warning.

## ASIO

ASIO is behind the Cargo feature `asio-output` so the normal desktop build stays independent from the Steinberg ASIO SDK and bindgen environment.

Validated feature check on this machine:

```powershell
$env:LIBCLANG_PATH='C:\Program Files\Huawei\DevEco Studio\sdk\default\openharmony\native\llvm\bin'
$env:BINDGEN_EXTRA_CLANG_ARGS='--target=x86_64-pc-windows-msvc -D_AMD64_=1 -D_M_AMD64=100 -D_WIN64=1 -DWIN32=1 -D_WINDOWS=1 -fms-compatibility -fms-extensions -I"C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um" -I"C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\shared" -I"C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\ucrt" -I"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207\include"'
cargo check --features asio-output
```

`asio-sys` can auto-download the ASIO SDK into a temp directory. For repeatable release builds, set `CPAL_ASIO_DIR` to a stable local ASIO SDK path and keep the `LIBCLANG_PATH`/`BINDGEN_EXTRA_CLANG_ARGS` values in the signing/build environment.
