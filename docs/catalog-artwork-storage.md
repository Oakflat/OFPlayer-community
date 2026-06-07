# Catalog Artwork Storage Guardrail

Date: 2026-05-14

This document records a real performance defect and the engineering rule that
prevents it from returning.

## Incident

OFPlayer stored embedded album artwork as base64 data URLs inside
`tracks.payload_json`.

That made normal catalog operations look harmless while they were actually
moving image blobs through SQLite, Rust JSON, Tauri IPC, JavaScript
normalization, Vue state, diagnostics, and playback preparation.

The failure mode was especially misleading because the app only had a small
number of tracks. In the observed local database:

- `tracks`: 68 rows.
- `SUM(LENGTH(payload_json))`: about 251.42 MB.
- `SUM(LENGTH(json_extract(payload_json, '$.artwork')))`: about 251.36 MB.
- Largest single embedded artwork string: about 4.7 MB.
- WebDAV sync and playback felt slow even when the real operation should have
  been simple indexing.

The performance problem was not SQLite indexes. The database was doing
pointless work by carrying media blobs in hot catalog rows.

## Root Cause

The catalog model mixed two very different kinds of data:

- Control-plane metadata: title, artist, album, duration, source path, ids,
  ordering, favorite state, and query projections.
- Media assets: large artwork images encoded as base64 strings.

Control-plane data is used constantly. Media assets are only needed by a small
set of visual surfaces, usually for the current track or one album cover.

Putting artwork inside `tracks.payload_json` made every catalog read pay the
cost of image transport.

## Current Rule

`tracks.payload_json` must never contain heavyweight media payloads.

Forbidden in `tracks.payload_json`:

- Embedded `artwork` data URLs.
- Full image blobs.
- Audio blobs.
- Full lyrics payloads.
- Remote metadata responses with large nested payloads.

Allowed in `tracks.payload_json`:

- Small track metadata.
- Source references.
- Stable ids and ordering fields.
- Small booleans, numbers, and strings needed for catalog behavior.

Artwork belongs in the artwork storage path:

- `track_artwork` stores the track id and a lightweight pointer.
- `track-artwork/` under OFPlayer local data stores decoded image files.
- `content_hash` deduplicates repeated album art.
- `byte_length` records logical image size.
- Frontend rendering uses `convertFileSrc()` for local artwork paths.
- Tauri asset protocol scope is limited to
  `$LOCALDATA/ofplayer/track-artwork/**`.

## Fixed Shape

The fixed architecture separates catalog records from artwork assets:

```text
tracks.payload_json
  -> small metadata only

track_artwork
  -> track_id
  -> artwork_path
  -> mime_type
  -> content_hash
  -> byte_length

%LOCALAPPDATA%\ofplayer\track-artwork\
  -> deduplicated image files
```

Observed after migration:

- `SUM(LENGTH(tracks.payload_json))`: about 0.06 MB.
- SQLite database file: about 708 KB after migration and vacuum.
- `track_artwork`: 57 logical artwork records.
- Distinct artwork files: 5.
- Logical artwork size: about 188.52 MB.
- Physical artwork asset size after dedupe: about 5.25 MB.

This is the intended shape: the catalog remains small, and artwork cost scales
with actual image assets rather than with every catalog query.

## WebDAV Rule

WebDAV sync must be index-first.

During sync:

- List remote audio files.
- Persist source references, inferred metadata, and lightweight fields.
- Do not download every remote track just to hydrate metadata or artwork.
- Do not insert remote embedded artwork into track payloads.

During playback or explicit hydration:

- Resolve or reuse the external playback cache file.
- Parse metadata from the local cached audio file.
- If embedded artwork is found, pass it to the backend artwork store.
- Store decoded artwork as a local asset file.
- Return a renderable local asset URL to the current track.

This keeps remote libraries fast to connect and makes artwork hydration
incremental.

## Regression Checks

Run these SQLite checks against the desktop database:

```sql
SELECT COUNT(*) AS tracks,
       ROUND(SUM(LENGTH(payload_json)) / 1024.0 / 1024.0, 2) AS payload_mb,
       ROUND(MAX(LENGTH(payload_json)) / 1024.0, 2) AS max_payload_kb
FROM tracks;

SELECT COUNT(*) AS artwork_rows,
       COUNT(NULLIF(artwork_path, '')) AS file_rows,
       COUNT(DISTINCT content_hash) AS distinct_files,
       ROUND(SUM(LENGTH(artwork_text)) / 1024.0 / 1024.0, 2) AS inline_artwork_mb,
       ROUND(SUM(byte_length) / 1024.0 / 1024.0, 2) AS logical_artwork_mb
FROM track_artwork;
```

Healthy expectations:

- `payload_mb` should stay small for normal libraries.
- `max_payload_kb` should not be measured in thousands because of artwork.
- `inline_artwork_mb` should normally be `0`.
- `file_rows` should match rows that have stored artwork.
- Large albums should increase `track-artwork/` size, not
  `tracks.payload_json`.

Storage analysis should show catalog database and track artwork assets as
separate buckets.

## Code Review Checklist

Reject changes that:

- Add `artwork` base64 back into catalog snapshots, bootstrap payloads, or
  hot query results.
- Serialize remote metadata responses wholesale into `tracks.payload_json`.
- Call WebDAV metadata hydration for every track during library sync.
- Return large artwork data URLs to long-lived Vue catalog state.
- Expand Tauri asset scope beyond the specific artwork asset directory without
  a reason.
- Treat database file size as the only metric while hiding duplicated artwork
  in text columns.

Accept changes that:

- Use projection columns for list/query work.
- Load a single track with artwork only when a visual surface needs it.
- Store decoded artwork files by content hash.
- Return local asset URLs for rendering.
- Keep WebDAV sync index-first and playback hydration incremental.

## Engineering Rule

The catalog is an index. Artwork is an asset.

Do not put assets inside hot catalog rows.
