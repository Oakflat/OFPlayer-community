import test from 'node:test'
import assert from 'node:assert/strict'
import {
  createLyricPlayerLines,
  findActiveLyricLineIndex,
  findActiveLyricPlayerLineIndex,
  findUpcomingLyricPlayerLineIndex,
  toLyricPlayerTimeMs,
} from './lyrics.ts'

test('createLyricPlayerLines renders translated lyric as a secondary line', () => {
  const lines = createLyricPlayerLines({
    status: 'resolved',
    kind: 'synced',
    lines: [
      {
        index: 0,
        text: 'Hello world',
        translatedLyric: '你好，世界',
        startTime: 1,
        endTime: 4,
      },
    ],
  })

  assert.equal(lines.length, 1)
  assert.equal(lines[0].words[0].word, 'Hello world')
  assert.equal(lines[0].translatedLyric, '你好，世界')
  assert.equal(lines[0].isBilingual, true)
})

test('createLyricPlayerLines groups same-timestamp bilingual LRC entries', () => {
  const lines = createLyricPlayerLines({
    status: 'resolved',
    kind: 'synced',
    lines: [
      { index: 0, text: 'Hello world', startTime: 1, endTime: 1 },
      { index: 1, text: '你好，世界', startTime: 1, endTime: 4 },
      { index: 2, text: 'Next line', startTime: 4, endTime: 8 },
    ],
  })

  assert.equal(lines.length, 2)
  assert.equal(lines[0].words[0].word, 'Hello world')
  assert.equal(lines[0].translatedLyric, '你好，世界')
  assert.equal(lines[0].endTime, 4000)
  assert.equal(lines[1].words[0].word, 'Next line')
})

test('toLyricPlayerTimeMs does not round playback into a future lyric line', () => {
  assert.equal(toLyricPlayerTimeMs(1.9996), 1999)
  assert.equal(toLyricPlayerTimeMs(2), 2000)
  assert.equal(toLyricPlayerTimeMs(-1), 0)
})

test('findActiveLyricPlayerLineIndex follows grouped display lines instead of raw lyric indexes', () => {
  const lyrics = {
    status: 'resolved',
    kind: 'synced',
    lines: [
      { index: 0, text: 'Hello world', startTime: 1, endTime: 1.04 },
      { index: 1, text: '你好，世界', startTime: 1.04, endTime: 4 },
      { index: 2, text: 'Next line', startTime: 4, endTime: 8 },
    ],
  }
  const lines = createLyricPlayerLines(lyrics)

  assert.equal(findActiveLyricLineIndex(lyrics, 1.05), 1)
  assert.equal(findActiveLyricPlayerLineIndex(lines, toLyricPlayerTimeMs(1.05)), 0)
  assert.equal(findActiveLyricPlayerLineIndex(lines, toLyricPlayerTimeMs(3.9996)), 0)
  assert.equal(findActiveLyricPlayerLineIndex(lines, toLyricPlayerTimeMs(4)), 1)
})

test('findUpcomingLyricPlayerLineIndex finds the next display line before playback reaches it', () => {
  const lines = createLyricPlayerLines({
    status: 'resolved',
    kind: 'synced',
    lines: [
      { index: 0, text: 'First line', startTime: 3, endTime: 5 },
      { index: 1, text: 'Second line', startTime: 5, endTime: 8 },
    ],
  })

  assert.equal(findUpcomingLyricPlayerLineIndex(lines, toLyricPlayerTimeMs(0.5)), 0)
  assert.equal(findUpcomingLyricPlayerLineIndex(lines, toLyricPlayerTimeMs(3)), 1)
  assert.equal(findUpcomingLyricPlayerLineIndex(lines, toLyricPlayerTimeMs(8)), -1)
})
