import test from 'node:test'
import assert from 'node:assert/strict'
import { formatCommandError, normalizeCommandError } from './errorNormalizer.ts'

test('normalizeCommandError keeps structured Rust command errors intact', () => {
  const normalized = normalizeCommandError({
    code: 'metadata_read_failed',
    message: 'Failed to read audio metadata.',
    source: 'failed to fill whole buffer',
    path: '\\\\?\\E:\\OFPlayer\\本地歌曲\\许之谦 - 其实.wav',
    fileName: '许之谦 - 其实.wav',
    recoverable: true,
  })

  assert.equal(normalized.code, 'metadata_read_failed')
  assert.equal(normalized.message, 'Failed to read audio metadata.')
  assert.equal(normalized.source, 'failed to fill whole buffer')
  assert.equal(normalized.recoverable, true)
})

test('formatCommandError handles string and serialized object rejections', () => {
  assert.equal(formatCommandError('plain failure'), 'plain failure')
  assert.equal(
    formatCommandError('{"message":"Structured failure","source":"inner reason"}'),
    'Structured failure inner reason',
  )
})
