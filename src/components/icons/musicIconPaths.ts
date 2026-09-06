import { userMusicIconPaths } from './userMusicIconPaths.ts'

/** User-supplied navigation contours and the existing 24-unit musical glyphs. */
export interface MusicIconPath {
  d: string
  fill?: 'currentColor'
  stroke?: 'none' | 'currentColor'
  transform?: string
  'stroke-width'?: number
}
const line = (d: string): MusicIconPath => ({ d })
const solid = (d: string): MusicIconPath => ({ d, fill: 'currentColor', stroke: 'none' })

export const musicIconPaths = {
  'library': userMusicIconPaths['library'],
  'remote': userMusicIconPaths['remote'],
  'all-tracks': userMusicIconPaths['all-tracks'],
  'recent-imports': userMusicIconPaths['recent-imports'],
  'history': userMusicIconPaths['history'],
  'favorites': userMusicIconPaths['favorites'],
  'queue': userMusicIconPaths['queue'],
  'albums': userMusicIconPaths['albums'],
  'artists': userMusicIconPaths['artists'],
  'import-audio': userMusicIconPaths['import-audio'],
  'import-folder': userMusicIconPaths['import-folder'],
  'settings': userMusicIconPaths['settings'],
  'eighth-note': [line('M13 3v14 M13 3c0 4 7 3 5 9'), solid('M13 15.5c-2.4-.8-6 1-6 3 0 2.4 6 1.8 6-1.2Z')],
  'treble-clef': [line('M10 15c-3-3 1-7 4-5 4 2 3 7-1 8-5 1-8-4-5-8l6-6c2-3-1-4-2-1-1 3 0 8 1 12l1 5c.4 2-3 3-4 1')],
  'eighth-pair': [line('M9 17V5l11-2v12 M9 8l11-2'), solid('M9 15.5c-2-.7-5 .7-5 2.5 0 2 5 1.5 5-1Z M20 13.5c-2-.7-5 .7-5 2.5 0 2 5 1.5 5-1Z')],
  sharp: [line('M9 3v18 M16 3v18 M5 10l15-4 M5 17l15-4')],
  'quarter-note': [line('M15 3v14'), solid('M15 15.5c-2.4-.8-6 1-6 3 0 2.4 6 1.8 6-1.2Z')],
  'bass-clef': [line('M5 9c-1-6 9-7 9 0 0 5-5 8-9 10'), solid('M8 9a2 2 0 1 1-4 0 2 2 0 0 1 4 0 M20 7a1.2 1.2 0 1 1-2.4 0 1.2 1.2 0 0 1 2.4 0 M20 12a1.2 1.2 0 1 1-2.4 0 1.2 1.2 0 0 1 2.4 0')],
  'sixteenth-pair': [line('M9 17V4h11v13 M9 8h11'), solid('M9 15.5c-2-.7-5 .7-5 2.5 0 2 5 1.5 5-1Z M20 15.5c-2-.7-5 .7-5 2.5 0 2 5 1.5 5-1Z')],
  flat: [line('M9 3v18c2-1.5 8-4.5 7-8-.7-3-5-2-7 1')],
} satisfies Record<string, MusicIconPath[]>

export type MusicIconName = keyof typeof musicIconPaths
export const musicIconAttributes = {
  xmlns: 'http://www.w3.org/2000/svg', viewBox: '0 0 24 24', width: 24, height: 24,
  fill: 'none', stroke: 'currentColor', 'stroke-width': 1.65,
  'stroke-linecap': 'round', 'stroke-linejoin': 'round',
} as const
