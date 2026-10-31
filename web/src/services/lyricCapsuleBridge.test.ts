import assert from 'node:assert/strict'
import test from 'node:test'
import { createLyricCapsuleSnapshot, findCapsuleTimelineLine } from './lyricCapsuleBridge.ts'

test('capsule keeps TTML voice metadata and converts word seconds to milliseconds', () => {
  const snapshot = createLyricCapsuleSnapshot({ lyricSource: 'sidecar-ttml', lyricTimeline: [{
    index: 4, text: 'A B', startMs: 1000, endMs: 2500,
    words: [{ text: 'A ', startTime: 1, endTime: 1.5 }, { text: 'B', startTime: 1.5, endTime: 2.5 }],
    isBG: true, isDuet: true, agent: 'v2',
  }] })
  assert.deepEqual(snapshot.lyricTimeline[0].words, [
    { text: 'A ', startMs: 1000, endMs: 1500 },
    { text: 'B', startMs: 1500, endMs: 2500 },
  ])
  assert.equal(snapshot.lyricTimeline[0].isBG, true)
  assert.equal(snapshot.lyricTimeline[0].isDuet, true)
  assert.equal(snapshot.lyricTimeline[0].agent, 'v2')
  assert.equal(snapshot.lyricSource, 'sidecar-ttml')
})

test('capsule TTML gap is inactive and later BG does not steal lead focus', () => {
  const lines = createLyricCapsuleSnapshot({ lyricTimeline: [
    { index: 0, text: 'Lead', startMs: 1000, endMs: 3000 },
    { index: 1, text: '(BG)', startMs: 1500, endMs: 2000, isBG: true },
    { index: 2, text: 'Next', startMs: 4000, endMs: 5000 },
  ] }).lyricTimeline
  assert.equal(findCapsuleTimelineLine(lines, 1750, 'sidecar-ttml')?.text, 'Lead')
  assert.equal(findCapsuleTimelineLine(lines, 3500, 'sidecar-ttml'), null)
  assert.equal(findCapsuleTimelineLine(lines, 5500, 'sidecar-ttml'), null)
  assert.equal(findCapsuleTimelineLine(lines, 5500, 'sidecar-lrc')?.text, 'Next')
})

test('capsule timeline keeps each line translation on one line for the second row', () => {
  const lines = createLyricCapsuleSnapshot({ lyricTimeline: [
    { index: 0, text: 'Hello', startMs: 0, translatedLyric: '你好\n世界' },
    { index: 1, text: 'Again', startMs: 1000, translatedLyric: null },
  ] }).lyricTimeline
  assert.equal(lines[0].translation, '你好 世界')
  assert.equal(lines[1].translation, '')
})

test('normalizing a capsule snapshot again keeps word timings and translations', () => {
  const once = createLyricCapsuleSnapshot({ lyricTimeline: [{
    index: 0, text: 'A B', startMs: 1000, endMs: 2500, translatedLyric: '甲 乙',
    words: [{ text: 'A ', startTime: 1, endTime: 1.5 }, { text: 'B', startTime: 1.5, endTime: 2.5 }, { text: 'C', startTime: null, endTime: null }],
  }] })
  const twice = createLyricCapsuleSnapshot({ ...once, lyricTimeline: once.lyricTimeline })
  assert.deepEqual(twice.lyricTimeline, once.lyricTimeline)
  assert.equal(twice.lyricTimeline[0].words.length, 2)
  assert.equal(twice.lyricTimeline[0].translation, '甲 乙')
})
