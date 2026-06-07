# Startup Bootstrap Data Path Defect

## Summary

This issue is classified as a startup data-path defect.

It was not a renderer paint/layout problem. The renderer looked slow because the
startup bootstrap command was transferring and normalizing a very large catalog
payload before the app could become visually ready.

The defect pattern was:

- Persist full track payloads in SQLite, including embedded artwork data URLs.
- Cache the full catalog again in `app_state.snapshot.catalog`.
- Return that full catalog through `desktop_state_load_bootstrap`.
- Normalize all tracks in the renderer during startup.

With only 66 tracks, this produced about 316 MB of track JSON, almost entirely
from `artwork` base64 strings. `snapshot.catalog` duplicated another roughly
316 MB. Cold start then moved the full media-library payload through SQLite,
Rust JSON, Tauri IPC, JavaScript deserialization, and Vue state hydration.

This is a product-quality defect because it scales with user library size and
media artwork size, not with the actual first-screen requirements.

## User Impact

Observed before the fix:

- Splash hid by timeout after about 3.2 seconds.
- Full app startup took about 6.3 seconds.
- `desktop_state_load_bootstrap` round trip took about 5.3 seconds.
- Renderer invoke/deserialize overhead was about 2.5 seconds.
- Renderer heap jumped by hundreds of MB.
- Rust process memory jumped by about 320 MB during bootstrap.

Observed after the fix:

- Splash hid on `visual_ready` in about 355 ms.
- Full app startup took about 706 ms.
- `desktop_state_load_bootstrap` round trip took about 242 ms.
- Renderer invoke/deserialize overhead dropped to about 31 ms.
- Renderer heap startup delta dropped to about 7 MB.
- Rust backend memory delta dropped to about 2.6 MB.

## Root Cause

The bootstrap contract grew beyond its job.

Bootstrap should load only the state needed to render and operate the first
screen:

- Preferences.
- Session.
- Recent history.
- Libraries and playlists.
- Navigation summary and counts.
- Enough cache/projection data for the first track query to be fast.

It should not load the full media catalog, embedded artwork, or every track row.

The expensive payload was hidden by the name `catalog`, which made it look like
normal app state. In practice, it contained heavyweight user media metadata,
including base64 artwork.

## Fixed Behavior

The fixed startup path is:

- `desktop_state_load_bootstrap` returns a lightweight catalog shell.
- `catalog.tracks` is omitted during startup.
- `catalogTracksIncluded` is reported as `false`.
- `catalogTrackCount` is reported as `0`.
- The Rust track-query cache is warmed from projection columns, not from full
  track JSON.
- The old `app_state.snapshot.catalog` cache is deleted on initialization.
- Individual tracks are loaded lazily when playback or metadata actions need
  the full track object.
- Album/artist browser views load the full catalog only after the first screen is
  ready and only when that browser view is active.

Expected diagnostics after the fix:

- `bootstrapRoundTripMs` should normally stay below 500 ms for a small library.
- `bootstrapInvokeOverheadMs` should normally stay below 100 ms.
- `catalogTracksIncluded` should be `false`.
- `catalogTrackCount` should be `0`.
- `trackCacheEntries` should match the number of known tracks.
- `snapshot.catalog` should not exist in `app_state`.

## Guardrails

Do not add heavyweight fields to bootstrap payloads.

These fields should not be part of startup bootstrap:

- `artwork`
- large `lyrics` payloads
- file blobs
- base64 strings
- full track lists
- full external-library browse results
- full remote metadata responses

Prefer:

- Row projections for list/query work.
- Counts and summaries for navigation.
- IDs for queues and selections.
- Lazy single-record fetches for current track or selected track.
- Post-visual-ready hydration for expensive browser views.

If a feature requires full track data, it must be opt-in and off the critical
startup path.

For the stricter catalog-level artwork rule, see
[`catalog-artwork-storage.md`](catalog-artwork-storage.md). Artwork base64 must
not return to `tracks.payload_json`; it belongs in `track_artwork` plus the
local `track-artwork/` asset directory.

## Diagnostic Checks

Use the installed diagnostics log:

```text
C:\Users\<user>\AppData\Local\OFPlayer\diagnostics\ofplayer-diagnostics.ndjson
```

Important startup events:

- `session_started`
- `tauri_setup`
- `bootstrap_state_loaded`
- `bootstrap_snapshot`
- `bootstrap_stores_hydrated`
- `bootstrap_active_track_hydrated`
- `bootstrap_state_ready`
- `startup_splash_hidden`
- `app_startup`

Important fields:

- `bootstrapLoadMs`
- `bootstrapRoundTripMs`
- `bootstrapBackendMs`
- `bootstrapInvokeOverheadMs`
- `catalogTracksIncluded`
- `catalogTrackCount`
- `trackCacheEntries`
- `rendererResources.delta.jsHeapUsedBytes`
- `bootstrapDiagnostics.process.delta.privateBytes`

SQLite check for the removed cache:

```sql
select key, length(value_json)
from app_state
order by length(value_json) desc
limit 10;

select count(*)
from app_state
where key = 'snapshot.catalog';
```

The second query should return `0`.

## Regression Smells

Treat any of these as a likely regression:

- `catalogTracksIncluded` becomes `true` in normal startup.
- `catalogTrackCount` is nonzero in normal startup.
- `bootstrapInvokeOverheadMs` grows with library size.
- Renderer heap jumps by tens or hundreds of MB before visual ready.
- `snapshot.catalog` returns to `app_state`.
- Small libraries show multi-second bootstrap times.
- A startup change requires parsing `tracks.payload_json` for every track.

## Engineering Rule

Startup is a control-plane path, not a media-data path.

The first screen should move identifiers, preferences, counters, summaries, and
small projections. Full media records, embedded artwork, and browse-only data
must be loaded lazily or after `visual_ready`.

