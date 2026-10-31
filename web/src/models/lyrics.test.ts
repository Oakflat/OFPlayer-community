import assert from 'node:assert/strict'
import test from 'node:test'
import { createLyricPlayerLines, findActiveLyricPlayerLineIndex, lyricWordProgress, toLyricPlayerTimeMs } from './lyrics.ts'

test('TTML words retain whitespace, order and millisecond timing', () => {
  assert.equal(toLyricPlayerTimeMs(2.6), 2600)
  const lines = createLyricPlayerLines({ status: 'resolved', kind: 'synced', source: 'sidecar-ttml', lines: [{
    text: 'Hello world', startTime: 1, endTime: 3,
    translatedLyric: '你好，世界', romanLyric: 'ni hao',
    words: [
      { text: 'Hello ', startTime: 1, endTime: 1.75 },
      { text: 'world', startTime: 1.75, endTime: 3 },
    ],
  }] })
  assert.deepEqual(lines[0].words, [
    { word: 'Hello ', startTime: 1000, endTime: 1750 },
    { word: 'world', startTime: 1750, endTime: 3000 },
  ])
  assert.equal(lines[0].translatedLyric, '你好，世界')
  assert.equal(lines[0].romanLyric, 'ni hao')
  assert.equal(lines[0].hasWordTiming, true)
  assert.equal(lyricWordProgress(lines[0].words[1], 2375), 0.5)
  const playbackSeconds = 2.6
  assert.equal(findActiveLyricPlayerLineIndex(lines, toLyricPlayerTimeMs(playbackSeconds)), 0)
  assert.equal(lyricWordProgress(lines[0].words[1], toLyricPlayerTimeMs(playbackSeconds)), 0.68)
  assert.equal(lyricWordProgress(lines[0].words[1], 500), 0)
  assert.equal(lyricWordProgress(lines[0].words[1], 4000), 1)
})

test('same-time background and duet voices remain separate from the lead', () => {
  const lines = createLyricPlayerLines({ status: 'resolved', kind: 'synced', lines: [
    { text: 'Lead', startTime: 5, endTime: 7, agent: 'v1', words: [{ text: 'Lead', startTime: 5, endTime: 7 }] },
    { text: '(Echo)', startTime: 5, endTime: 7, isBG: true, words: [{ text: '(Echo)', startTime: 5, endTime: 7 }] },
    { text: 'Duet', startTime: 5, endTime: 7, isDuet: true, agent: 'v2' },
  ] })
  assert.equal(lines.length, 3)
  assert.deepEqual(lines.map((line) => line.endTime), [7000, 7000, 7000])
  assert.equal(lines[0].translatedLyric, '')
  assert.equal(findActiveLyricPlayerLineIndex(lines, 5500), 0)
})

test('ordinary bilingual LRC still groups by line and falls back to one word', () => {
  const lines = createLyricPlayerLines({ status: 'resolved', kind: 'synced', lines: [
    { text: 'Original', startTime: 10 },
    { text: '译文', startTime: 10 },
  ] })
  assert.equal(lines.length, 1)
  assert.equal(lines[0].translatedLyric, '译文')
  assert.deepEqual(lines[0].words, [{ word: 'Original', startTime: 10000, endTime: 15000 }])
  assert.equal(lines[0].hasWordTiming, false)
})

test('line-mode TTML never infers bilingual text from equal timestamps', () => {
  const lines = createLyricPlayerLines({ status: 'resolved', kind: 'synced', source: 'sidecar-ttml', lines: [
    { text: 'Singer one', startTime: 10, endTime: 12 },
    { text: 'Singer two', startTime: 10, endTime: 12 },
  ] })
  assert.equal(lines.length, 2)
  assert.deepEqual(lines.map((line) => line.translatedLyric), ['', ''])
  assert.deepEqual(lines.map((line) => line.endTime), [12000, 12000])
})

test('TTML keeps short and overlapping ends while lead retains focus over later BG', () => {
  const lines = createLyricPlayerLines({ status: 'resolved', kind: 'synced', source: 'sidecar-ttml', lines: [
    { text: 'Lead', startTime: 1, endTime: 3 },
    { text: '(BG)', startTime: 1.5, endTime: 2, isBG: true },
    { text: 'Tick', startTime: 1.6, endTime: 1.65, isBG: true },
  ] })
  assert.deepEqual(lines.map((line) => line.endTime), [3000, 2000, 1650])
  assert.equal(findActiveLyricPlayerLineIndex(lines, 1550), 0)
  assert.equal(findActiveLyricPlayerLineIndex(lines, 1750), 0)
  assert.equal(findActiveLyricPlayerLineIndex(lines, 2200), 0)
  assert.equal(findActiveLyricPlayerLineIndex(lines, 3200), -1)
})

test('duet is secondary to simultaneous lead and TTML gap has no active line', () => {
  const lines = createLyricPlayerLines({ status: 'resolved', kind: 'synced', source: 'sidecar-ttml', lines: [
    { text: 'Lead', startTime: 1, endTime: 3 },
    { text: 'Duet', startTime: 1.5, endTime: 2, isDuet: true },
    { text: 'Next', startTime: 4, endTime: 5 },
  ] })
  assert.equal(findActiveLyricPlayerLineIndex(lines, 1750), 0)
  assert.equal(findActiveLyricPlayerLineIndex(lines, 3500), -1)
  assert.equal(findActiveLyricPlayerLineIndex(lines, 5500), -1)
})

test('partial timed spans fall back to the complete text instead of dropping untimed fragments', () => {
  const [line] = createLyricPlayerLines({ status: 'resolved', kind: 'synced', source: 'sidecar-ttml', lines: [{
    text: 'Hello world!', startTime: 1, endTime: 3,
    words: [{ text: 'Hello ', startTime: 1, endTime: 2 }],
  }] })
  assert.equal(line.hasWordTiming, false)
  assert.equal(line.words.map((word) => word.word).join(''), 'Hello world!')
})
