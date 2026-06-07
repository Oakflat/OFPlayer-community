import test from 'node:test'
import assert from 'node:assert/strict'
import {
  filterTracksForAlbumBrowserSearch,
  groupTracksByAlbum,
  groupTracksByArtist,
  mergeAlbumBrowserSearchGroupMetadata,
} from './albumViewService.ts'

const browserTracks = [
  {
    id: 'enter-pharloom',
    title: 'Enter Pharloom',
    album: 'Hollow Knight: Silksong (Original Soundtrack)',
    albumArtist: 'Christopher Larkin',
    artist: 'Christopher Larkin',
    artwork: 'asset://silksong-cover.png',
    trackNumber: 1,
    duration: 125,
  },
  {
    id: 'strive',
    title: 'Strive',
    album: 'Hollow Knight: Silksong (Original Soundtrack)',
    albumArtist: 'Christopher Larkin',
    artist: 'Christopher Larkin',
    trackNumber: 3,
    duration: 106,
  },
  {
    id: 'distant-village',
    title: 'Distant Village',
    album: 'Other Album',
    albumArtist: 'Another Artist',
    artist: 'Another Artist',
    trackNumber: 1,
    duration: 180,
  },
]

test('album browser search filters tracks before album grouping', () => {
  const filteredTracks = filterTracksForAlbumBrowserSearch(browserTracks, 'str')
  const groups = groupTracksByAlbum(filteredTracks)

  assert.deepEqual(filteredTracks.map((track) => track.id), ['strive'])
  assert.equal(groups.length, 1)
  assert.equal(groups[0].albumName, 'Hollow Knight: Silksong (Original Soundtrack)')
  assert.equal(groups[0].trackCount, 1)
  assert.deepEqual(groups[0].tracks.map((track) => track.id), ['strive'])
})

test('album browser search preserves cover metadata from the full album', () => {
  const fullGroups = groupTracksByAlbum(browserTracks)
  const filteredGroups = groupTracksByAlbum(filterTracksForAlbumBrowserSearch(browserTracks, 'str'))
  const mergedGroups = mergeAlbumBrowserSearchGroupMetadata(filteredGroups, fullGroups)

  assert.equal(filteredGroups[0].coverUrl, null)
  assert.equal(mergedGroups[0].coverUrl, 'asset://silksong-cover.png')
  assert.equal(mergedGroups[0].trackCount, 1)
  assert.deepEqual(mergedGroups[0].tracks.map((track) => track.id), ['strive'])
})

test('album browser search keeps full albums when album metadata matches', () => {
  const filteredTracks = filterTracksForAlbumBrowserSearch(browserTracks, 'silksong')
  const groups = groupTracksByAlbum(filteredTracks)

  assert.deepEqual(filteredTracks.map((track) => track.id), ['enter-pharloom', 'strive'])
  assert.equal(groups.length, 1)
  assert.equal(groups[0].trackCount, 2)
})

test('artist browser search filters artist album children to matching tracks', () => {
  const filteredTracks = filterTracksForAlbumBrowserSearch(browserTracks, 'str')
  const groups = groupTracksByArtist(filteredTracks)

  assert.equal(groups.length, 1)
  assert.equal(groups[0].artistName, 'Christopher Larkin')
  assert.equal(groups[0].trackCount, 1)
  assert.equal(groups[0].albumCount, 1)
  assert.deepEqual(groups[0].tracks.map((track) => track.id), ['strive'])
  assert.deepEqual(groups[0].albums[0].tracks.map((track) => track.id), ['strive'])
})

test('artist browser search preserves artist and child album cover metadata', () => {
  const fullGroups = groupTracksByArtist(browserTracks)
  const filteredGroups = groupTracksByArtist(filterTracksForAlbumBrowserSearch(browserTracks, 'str'))
  const mergedGroups = mergeAlbumBrowserSearchGroupMetadata(filteredGroups, fullGroups)

  assert.equal(filteredGroups[0].coverUrl, null)
  assert.equal(filteredGroups[0].albums[0].coverUrl, null)
  assert.equal(mergedGroups[0].coverUrl, 'asset://silksong-cover.png')
  assert.equal(mergedGroups[0].albums[0].coverUrl, 'asset://silksong-cover.png')
  assert.equal(mergedGroups[0].trackCount, 1)
  assert.deepEqual(mergedGroups[0].albums[0].tracks.map((track) => track.id), ['strive'])
})
