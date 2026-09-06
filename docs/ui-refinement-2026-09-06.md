# Community UI refinement · 2026-09-06

This source update brings the desktop UI refinements into the community baseline. The application version remains 26.0.4; this update does not publish a new installer or replace the existing release tag.

- Use the supplied grouped SVG contours for 12 navigation/action icons and 8 music symbols for track navigation. Icons inherit interface colors and remain decorative inside labeled controls.
- Add track-group previews and bounded Bezier scrolling for long lists, with immediate jumps when reduced motion is enabled.
- Improve English settings/sidebar wrapping and format dates/numbers using the selected UI language.
- Coordinate startup motion, remove icon spins, cap list stagger delays, and apply separate enter/exit curves. Menus use 220/140ms, dialogs 320/180ms, and immersive view 520/260ms.
- Suppress animation delays as well as durations for app/system reduced-motion preferences. Preserve menu position until exit completes and let nested modal animation finish before teardown.
- Read the desktop dev port from Tauri configuration (15573), share the HMR port, and use 15583 for independent fixture previews.

## Maintenance

Motion tokens are at the top of `src/style.css`. JavaScript rail scrolling in `src/components/trackNavRailModel.ts` matches `cubic-bezier(0.22, 1, 0.36, 1)`; update its numerical tests alongside curve changes. Icon sources, group mapping and regeneration instructions live in `src/assets/music-icons/README.md`.

Run `npm run dev:ui` and open `/scripts/ui-preview.html` for language/theme/motion controls, settings, album views, immersive playback layout and a Navigation scrolling fixture. `/scripts/music-icons.html` shows the standalone icon set. These pages do not enter the production build or call the desktop backend.

## Community boundary

The patch retains the community package identity, dependencies, licenses and local-only diagnostics. It does not add the private account/entitlement service, official updater, Store packaging, Hub implementation or remote diagnostics upload.

## Validation

Local validation on Windows passed:

- `npm run build` (including TypeScript checks).
- `npm run test:unit`: 73 tests.
- `npm run check:tauri-commands`: 82 commands synchronized.
- `cargo check --locked`, `cargo fmt --all -- --check`, and `cargo clippy --locked --all-targets -- -D warnings`.
- `cargo test --locked`: 67 tests.

The strict Rust gate also removed an unused artwork lookup left behind by the community split and replaced a manual listening-duration bound with `clamp`. No lint suppression was added. GitHub CI runs independently on the pushed commit.
