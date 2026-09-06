import { mkdir, writeFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import { musicIconAttributes, musicIconPaths } from '../src/components/icons/musicIconPaths.ts'

const output = new URL('../src/assets/music-icons/', import.meta.url)
await mkdir(output, { recursive: true })
const attributes = (value: object) => Object.entries(value).map(([key, val]) => `${key}="${val}"`).join(' ')
for (const [name, paths] of Object.entries(musicIconPaths)) {
  const svg = `<svg ${attributes(musicIconAttributes)}>\n${paths.map(path => `  <path ${attributes(path)}/>`).join('\n')}\n</svg>\n`
  await writeFile(new URL(`${name}.svg`, output), svg)
}
console.log(`Exported ${Object.keys(musicIconPaths).length} SVGs to ${fileURLToPath(output)}`)

// A portable vector contact sheet, with the same geometry at real UI sizes.
const entries = Object.entries(musicIconPaths)
const symbols = entries.map(([name, paths]) => `<symbol id="${name}" viewBox="0 0 24 24"><g fill="none" stroke="currentColor" stroke-width="1.65" stroke-linecap="round" stroke-linejoin="round">${paths.map(path => `<path ${attributes(path)}/>`).join('')}</g></symbol>`).join('\n')
const cells = entries.map(([name], index) => {
  const x = 40 + (index % 5) * 224
  const y = 124 + Math.floor(index / 5) * 166
  return `<g transform="translate(${x} ${y})"><use href="#${name}" x="80" width="64" height="64"/><text x="112" y="90" text-anchor="middle">${name}</text><use href="#${name}" x="60" y="112" width="16" height="16"/><use href="#${name}" x="100" y="110" width="20" height="20"/><use href="#${name}" x="140" y="108" width="24" height="24" color="#ed7d47"/></g>`
}).join('\n')
await writeFile(new URL('overview.svg', output), `<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="820" viewBox="0 0 1200 820" role="img" aria-labelledby="title desc">
<title id="title">OFPlayer music icon set</title><desc id="desc">12 user-supplied icons and 8 musical glyphs shown at 64, 16, 20, and 24 pixels.</desc>
<defs>${symbols}</defs><rect width="1200" height="820" rx="24" fill="#faf9fc"/>
<g color="#625d69" fill="#625d69" font-family="system-ui, sans-serif" font-size="13">
<text x="48" y="52" font-size="26" font-weight="600">OFPlayer / Music icons</text>
<text x="48" y="80">User contours · 24-unit grid · currentColor · 64 / 16 / 20 / 24 px</text>
${cells}</g></svg>\n`)
