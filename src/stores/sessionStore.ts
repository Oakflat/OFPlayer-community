import { computed, reactive, ref } from 'vue'
import { createSessionModel } from '../models/session'

type SessionState = ReturnType<typeof createSessionModel>

type SessionStoreOptions = {
  dataService?: any
  initialSession?: Record<string, any> | null
}

function normalizeRevision(value: unknown, fallback = 0): number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0 ? value : fallback
}

function applySessionState(state: SessionState, nextState: SessionState): void {
  state.id = nextState.id
  state.startedAt = nextState.startedAt
  state.lastInteractedAt = nextState.lastInteractedAt
  state.currentTrackId = nextState.currentTrackId
  state.queueTrackIds = [...nextState.queueTrackIds]
  state.playbackStatus = nextState.playbackStatus
  state.currentTime = nextState.currentTime
  state.duration = nextState.duration
}

export function createSessionStore({ dataService, initialSession }: SessionStoreOptions = {}) {
  const state = reactive(createSessionModel(initialSession ?? undefined))
  const revision = ref(0)

  const currentTrackId = computed(() => state.currentTrackId)
  const queueTrackIds = computed(() => state.queueTrackIds)
  const playbackStatus = computed(() => state.playbackStatus)
  const currentTime = computed(() => state.currentTime)
  const duration = computed(() => state.duration)
  const currentTrackIndex = computed(() => {
    if (!state.currentTrackId) {
      return -1
    }

    return state.queueTrackIds.findIndex((trackId) => trackId === state.currentTrackId)
  })

  function setRevision(nextRevision: unknown) {
    revision.value = normalizeRevision(nextRevision, revision.value)
    return revision.value
  }

  function applySnapshot(
    snapshot: Record<string, any> | null | undefined,
    { revision: nextRevision = null }: { revision?: number | null } = {},
  ) {
    applySessionState(state, createSessionModel(snapshot ?? undefined))

    if (nextRevision !== null) {
      setRevision(nextRevision)
    } else {
      revision.value += 1
    }

    return state
  }

  async function hydrate(
    preloadedSession: Record<string, any> | null = null,
    { revision: nextRevision = null }: { revision?: number | null } = {},
  ) {
    const persistedSession = preloadedSession ?? await dataService.session.loadSnapshot()
    return applySnapshot(persistedSession, { revision: nextRevision })
  }

  async function setQueue(trackIds: unknown[]) {
    const snapshot = await dataService.playbackSession.setQueue(trackIds)
    applySnapshot(snapshot)
    return state.queueTrackIds
  }

  function getNextTrackId() {
    if (state.queueTrackIds.length === 0) {
      return null
    }

    if (currentTrackIndex.value === -1) {
      return state.queueTrackIds[0]
    }

    return state.queueTrackIds[(currentTrackIndex.value + 1) % state.queueTrackIds.length]
  }

  function getPreviousTrackId() {
    if (state.queueTrackIds.length === 0) {
      return null
    }

    if (currentTrackIndex.value === -1) {
      return state.queueTrackIds[0]
    }

    return state.queueTrackIds[
      (currentTrackIndex.value - 1 + state.queueTrackIds.length) % state.queueTrackIds.length
    ]
  }

  return {
    state,
    revision,
    setRevision,
    currentTrackId,
    currentTrackIndex,
    queueTrackIds,
    playbackStatus,
    currentTime,
    duration,
    applySnapshot,
    hydrate,
    setQueue,
    getNextTrackId,
    getPreviousTrackId,
  }
}
