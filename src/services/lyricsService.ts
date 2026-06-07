import { createLyricsSnapshotModel, findActiveLyricLineIndex } from '../models/lyrics'
import type { LyricsSnapshotModel } from '../models/lyrics'
import type { TrackModel } from '../models/track'

type ServiceRecord = Record<string, any>

interface LyricsServiceOptions {
  dataService?: ServiceRecord
  getLyricsDirectories?: () => string[]
}

export function createLyricsService({ dataService, getLyricsDirectories }: LyricsServiceOptions = {}) {
  async function resolveForTrack(
    track: Partial<TrackModel> | null | undefined,
    { positionSeconds = null }: { positionSeconds?: number | null } = {},
  ) {
    const audioPath = track?.source?.path
    const originPath = track?.source?.originPath
    const lyricsDirectories =
      typeof getLyricsDirectories === 'function' ? getLyricsDirectories() : []

    if (typeof audioPath !== 'string' || audioPath.length === 0 || !dataService?.lyrics) {
      return createLyricsSnapshotModel({
        trackId: track?.id ?? null,
        audioPath: audioPath ?? '',
        status: 'missing',
      })
    }

    return dataService.lyrics.resolveTrack({
      trackId: track?.id ?? null,
      audioPath,
      originPath,
      title: track?.title ?? track?.displayTitle ?? '',
      artist: track?.artist || track?.albumArtist || '',
      album: track?.album ?? '',
      fileName: track?.fileName ?? '',
      lyricsPath: track?.lyricsPath ?? '',
      lyricsDirectories,
      positionSeconds,
    })
  }

  return {
    resolveForTrack,
    findActiveLineIndex(lyrics: LyricsSnapshotModel, seconds: number) {
      return findActiveLyricLineIndex(lyrics, seconds)
    },
  }
}
