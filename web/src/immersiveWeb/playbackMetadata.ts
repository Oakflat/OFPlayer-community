export type PlaybackTrackUpdate = {
  revision: number
  title: string
  artist: string
  artwork: string | null
}

// Metadata never carries a playback command or clock. A late cover may enrich
// the current track, including successive local originals sharing "external".
export function readPlaybackTrackUpdate(data: unknown, activeTrackId: string | null, lastRevision: number): PlaybackTrackUpdate | null {
  if (!data || typeof data !== 'object' || !activeTrackId) return null
  const packet = data as { trackId?: unknown; revision?: unknown; track?: unknown }
  if (packet.trackId !== activeTrackId || typeof packet.revision !== 'number'
    || !Number.isSafeInteger(packet.revision) || packet.revision <= lastRevision) return null
  if (packet.track === null) return { revision: packet.revision, title: '', artist: '', artwork: null }
  if (!packet.track || typeof packet.track !== 'object') return null
  const track = packet.track as { id?: unknown; displayTitle?: unknown; title?: unknown; artist?: unknown; artwork?: unknown }
  if (track.id !== activeTrackId) return null
  return {
    revision: packet.revision,
    title: typeof track.displayTitle === 'string' ? track.displayTitle : typeof track.title === 'string' ? track.title : '',
    artist: typeof track.artist === 'string' ? track.artist : '',
    artwork: typeof track.artwork === 'string' ? track.artwork : null,
  }
}
