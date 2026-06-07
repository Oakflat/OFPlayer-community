import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

const source = readFileSync(new URL('./LyricsPlayerView.vue', import.meta.url), 'utf8').replace(/\r\n/g, '\n')

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}

function cssRuleBody(selector: string): string {
  const match = source.match(new RegExp(`${escapeRegExp(selector)}\\s*\\{([^}]*)\\}`, 'm'))
  return match?.[1] ?? ''
}

test('LyricsPlayerView active styling does not change scroll-measured line height', () => {
  const activeBilingualBody = cssRuleBody('.lp-line.is-active.is-bilingual')
  const activeTranslationBody = cssRuleBody('.lp-line.is-active .lp-sub--translation')

  assert.doesNotMatch(
    activeBilingualBody,
    /\b(?:padding|margin|font-size|line-height|height|min-height|max-height)\b/,
  )
  assert.doesNotMatch(
    activeTranslationBody,
    /\b(?:padding|margin|font-size|line-height|height|min-height|max-height)\b/,
  )
})

test('LyricsPlayerView row transforms avoid vertical scaling during lyric state changes', () => {
  const rowTransformBodies = [...source.matchAll(/\.lp-line[^{]*\{[^}]*\btransform:\s*([^;]+);/g)]
    .map((match) => match[1])

  assert.ok(rowTransformBodies.length > 0)

  for (const transform of rowTransformBodies) {
    assert.doesNotMatch(transform, /\bscale\(/)
  }
})

test('LyricsPlayerView uses a transformed lyric stage instead of native scroll positioning', () => {
  const rootBody = cssRuleBody('.lp-root')
  const stageBody = cssRuleBody('.lp-stage')

  assert.match(source, /ref="stageRef"/)
  assert.match(rootBody, /overflow:\s*hidden/)
  assert.doesNotMatch(rootBody, /overflow-y:\s*auto/)
  assert.match(stageBody, /height:\s*100%/)
  assert.match(stageBody, /will-change:\s*transform/)
})

test('LyricsPlayerView gives first and last lyric enough viewport padding to center', () => {
  const rootBody = cssRuleBody('.lp-root')
  const topPadBody = cssRuleBody('.lp-pad--top')
  const bottomPadBody = cssRuleBody('.lp-pad--bottom')

  assert.match(source, /--lp-align-position/)
  assert.match(rootBody, /--lp-align-position:\s*40%/)
  assert.match(topPadBody, /height:\s*var\(--lp-align-position\)/)
  assert.match(bottomPadBody, /height:\s*var\(--lp-counter-align-position\)/)
  assert.doesNotMatch(topPadBody, /height:\s*38%/)
  assert.doesNotMatch(bottomPadBody, /height:\s*62%/)
})

test('LyricsPlayerView exposes a clear pending focus state before the first active lyric', () => {
  const pendingBody = cssRuleBody('.lp-line.is-pending')
  const pendingBlurBody = cssRuleBody('.lp--blur .lp-line.is-active,\n.lp--blur .lp-line.is-pending')

  assert.match(source, /'is-pending': activeIndex < 0 && dist\(idx\) === 0/)
  assert.match(pendingBody, /opacity:\s*0\.78/)
  assert.match(pendingBlurBody, /filter:\s*none/)
})
