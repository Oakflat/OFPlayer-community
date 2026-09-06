# OFPlayer music icons

12 navigation/action icons use the user's grouped SVG contours verbatim; the 8 existing musical notation glyphs remain in place. All use a 24×24 viewBox and `currentColor`. The supplied outlined shapes receive a 0.55-unit contour stroke after scaling, keeping their interior holes open. Musical glyphs retain their 1.65-unit strokes. `overview.svg` is a self-contained vector contact sheet of all 20 icons.

The original grouped artwork is preserved in `source/user-icons.svg`. `source/user-icon-groups.json` maps its top-level elements and measured bounds to the 12 icon names (the queue uses four adjacent paths). Run `./scripts/import-user-music-icons.ps1` in PowerShell to generate `userMusicIconPaths.ts` and `icon-adjusted.svg`. The latter preserves the original sheet layout and groups with a unified gray and a 2.5-source-unit contour stroke. If the source groups or geometry change, update the mapping and bounds before importing again.

`src/components/icons/musicIconPaths.ts` combines these imported paths with the musical glyphs. Vue renders the same paths through `MusicIcon.ts`; standalone SVG files are regenerated with `npm run icons:export`. Do not edit generated SVGs independently. The original Desktop file is not modified.

Run `npm run dev:ui` and open `http://localhost:15583/scripts/music-icons.html` to inspect all icons at 64, 16, 20, and 24 px on light/dark backgrounds. Each name links to its standalone SVG. `/scripts/ui-preview.html` shows the integrated sidebar. Preview pages are not release entry points.

Icons are decorative inside labeled controls (`aria-hidden="true"`, `focusable="false"`). Standalone consumers should supply an accessible name on the enclosing control, or a title/role when the SVG itself conveys information. Colors and active states come from the consuming interface.
