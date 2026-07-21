<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { HardDrive, ListMusic, X } from 'lucide-vue-next'
import { useI18n } from '../composables/useI18n'

interface OrderedEntity {
  id: string
  order?: number
  createdAt?: string
}

interface PlaylistDialogTrack {
  id?: string
  libraryId?: string
  displayTitle?: string
  title?: string
  artist?: string
  albumArtist?: string
}

interface PlaylistDialogLibrary extends OrderedEntity {
  name?: string
  isDefault?: boolean
}

interface PlaylistDialogPlaylist extends OrderedEntity {
  name?: string
  kind?: string
  libraryId?: string
}

type AvailablePlaylistDialogLibrary = PlaylistDialogLibrary & {
  disabled: boolean
}

interface TrackPlaylistDialogProps {
  isOpen?: boolean
  track?: PlaylistDialogTrack | null
  libraries?: PlaylistDialogLibrary[]
  playlists?: PlaylistDialogPlaylist[]
  preferredLibraryId?: string | null
  preferredPlaylistId?: string | null
}

const props = withDefaults(defineProps<TrackPlaylistDialogProps>(), {
  isOpen: false,
  track: null,
  libraries: () => [],
  playlists: () => [],
  preferredLibraryId: null,
  preferredPlaylistId: null,
})

const emit = defineEmits<{
  close: []
  confirm: [payload: { trackId: string; libraryId: string | null; playlistId: string }]
}>()

const { t } = useI18n()
const selectedLibraryId = ref<string | null>(null)
const selectedPlaylistId = ref<string | null>(null)
const dialogTitleId = 'track-playlist-dialog-title'

function sortByOrder<T extends OrderedEntity>(items: T[] = []) {
  return [...items].sort((left, right) => {
    const orderDiff = (left?.order ?? 0) - (right?.order ?? 0)

    if (orderDiff !== 0) {
      return orderDiff
    }

    return String(left?.createdAt ?? left?.id ?? '').localeCompare(String(right?.createdAt ?? right?.id ?? ''))
  })
}

function resolveTrackTitle(track: PlaylistDialogTrack | null | undefined) {
  return track?.displayTitle || track?.title || t('player.untitled')
}

function resolveTrackArtist(track: PlaylistDialogTrack | null | undefined) {
  return track?.artist || track?.albumArtist || t('track.unknownArtist')
}

function resolveLibraryLabel(library: AvailablePlaylistDialogLibrary | PlaylistDialogLibrary) {
  if (library?.name) {
    return library.name
  }

  if (library?.isDefault) {
    return t('sidebar.libraries.local')
  }

  return t('sidebar.librarySection')
}

function resolvePlaylistLabel(playlist: PlaylistDialogPlaylist) {
  return playlist?.name || t('sidebar.playlistSection')
}

const sourceLibraryId = computed(() => props.track?.libraryId ?? props.preferredLibraryId ?? null)
const sortedLibraries = computed(() => sortByOrder(props.libraries))

const availableLibraries = computed(() =>
  sortedLibraries.value.map((library) => ({
    ...library,
    disabled: Boolean(sourceLibraryId.value) && library.id !== sourceLibraryId.value,
  })),
)

const availablePlaylists = computed(() =>
  sortByOrder(
    props.playlists.filter(
      (playlist) => playlist.kind === 'user' && playlist.libraryId === selectedLibraryId.value,
    ),
  ),
)

const showsLibraryHint = computed(() => {
  return availableLibraries.value.some((library) => library.disabled)
})

const isCrossLibrarySelection = computed(() => {
  return Boolean(
    sourceLibraryId.value &&
      selectedLibraryId.value &&
      sourceLibraryId.value !== selectedLibraryId.value,
  )
})

const isConfirmDisabled = computed(() => {
  return !props.track?.id || !selectedPlaylistId.value || isCrossLibrarySelection.value
})

function syncSelection() {
  const fallbackLibraryId =
    (sourceLibraryId.value &&
      sortedLibraries.value.some((library) => library.id === sourceLibraryId.value) &&
      sourceLibraryId.value) ||
    props.preferredLibraryId ||
    sortedLibraries.value[0]?.id ||
    null

  selectedLibraryId.value = fallbackLibraryId

  const visiblePlaylists = sortByOrder(
    props.playlists.filter(
      (playlist) => playlist.kind === 'user' && playlist.libraryId === selectedLibraryId.value,
    ),
  )

  const preferredPlaylist = visiblePlaylists.find((playlist) => playlist.id === props.preferredPlaylistId)
  selectedPlaylistId.value = preferredPlaylist?.id ?? visiblePlaylists[0]?.id ?? null
}

function handleCancel() {
  emit('close')
}

function handleConfirm() {
  const trackId = props.track?.id
  const playlistId = selectedPlaylistId.value

  if (isConfirmDisabled.value || !trackId || !playlistId) {
    return
  }

  emit('confirm', {
    trackId,
    libraryId: selectedLibraryId.value,
    playlistId,
  })
  emit('close')
}

watch(
  () => [props.isOpen, props.track?.id, props.preferredLibraryId, props.preferredPlaylistId, props.libraries.length, props.playlists.length] as const,
  ([isOpen]) => {
    if (!isOpen) {
      selectedLibraryId.value = null
      selectedPlaylistId.value = null
      return
    }

    syncSelection()
  },
  { immediate: true },
)

watch(selectedLibraryId, (libraryId) => {
  if (!props.isOpen || !libraryId) {
    return
  }

  const visiblePlaylists = availablePlaylists.value

  if (visiblePlaylists.some((playlist) => playlist.id === selectedPlaylistId.value)) {
    return
  }

  selectedPlaylistId.value = visiblePlaylists.find((playlist) => playlist.id === props.preferredPlaylistId)?.id ?? visiblePlaylists[0]?.id ?? null
})
</script>

<template>
  <Teleport to="body">
    <Transition name="dialog">
      <div v-if="isOpen" class="dialog-backdrop">
        <div class="dialog-modal playlist-dialog" role="dialog" :aria-labelledby="dialogTitleId">
          <header class="dialog-header">
            <div class="playlist-dialog-head">
              <h2 :id="dialogTitleId" class="dialog-title">
                {{ t('sidebar.dialogs.addToPlaylistTitle') }}
              </h2>
              <div v-if="track" class="playlist-dialog-track">
                <span>{{ t('sidebar.dialogs.addToPlaylistTrackLabel') }}</span>
                <strong>{{ resolveTrackTitle(track) }}</strong>
                <small>{{ resolveTrackArtist(track) }}</small>
              </div>
            </div>
            <button
              type="button"
              class="dialog-close"
              :aria-label="t('settings.close')"
              @click="handleCancel"
            >
              <X aria-hidden="true" />
            </button>
          </header>

          <div class="dialog-body playlist-dialog-body">
            <section class="playlist-dialog-section">
              <span class="dialog-input-label">{{ t('sidebar.dialogs.addToPlaylistLibraryLabel') }}</span>
              <div class="playlist-dialog-grid playlist-dialog-grid--libraries">
                <button
                  v-for="library in availableLibraries"
                  :key="library.id"
                  type="button"
                  class="playlist-dialog-option"
                  :class="{ 'is-active': library.id === selectedLibraryId, 'is-disabled': library.disabled }"
                  :disabled="library.disabled"
                  @click="selectedLibraryId = library.id"
                >
                  <HardDrive aria-hidden="true" />
                  <strong>{{ resolveLibraryLabel(library) }}</strong>
                </button>
              </div>
              <p v-if="showsLibraryHint" class="playlist-dialog-hint">
                {{ t('sidebar.dialogs.addToPlaylistLibraryHint') }}
              </p>
            </section>

            <section class="playlist-dialog-section">
              <span class="dialog-input-label">{{ t('sidebar.dialogs.addToPlaylistPlaylistLabel') }}</span>
              <div v-if="availablePlaylists.length > 0" class="playlist-dialog-grid">
                <button
                  v-for="playlist in availablePlaylists"
                  :key="playlist.id"
                  type="button"
                  class="playlist-dialog-option"
                  :class="{ 'is-active': playlist.id === selectedPlaylistId }"
                  @click="selectedPlaylistId = playlist.id"
                >
                  <ListMusic aria-hidden="true" />
                  <strong>{{ resolvePlaylistLabel(playlist) }}</strong>
                </button>
              </div>
              <p v-else class="dialog-message playlist-dialog-empty">
                {{ t('sidebar.dialogs.addToPlaylistNoPlaylist') }}
              </p>
            </section>
          </div>

          <footer class="dialog-footer">
            <button type="button" class="dialog-button dialog-button-cancel" @click="handleCancel">
              {{ t('sidebar.dialogs.cancel') }}
            </button>
            <button
              type="button"
              class="dialog-button dialog-button-confirm"
              :disabled="isConfirmDisabled"
              @click="handleConfirm"
            >
              {{ t('sidebar.dialogs.addToPlaylistConfirm') }}
            </button>
          </footer>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.dialog-backdrop {
  position: fixed;
  inset: 0;
  z-index: 60;
  display: grid;
  place-items: center;
  padding: var(--space-6);
  background: var(--dialog-backdrop);
  backdrop-filter: var(--dialog-backdrop-filter);
  -webkit-backdrop-filter: var(--dialog-backdrop-filter);
}

.dialog-modal {
  width: min(520px, calc(100vw - 48px));
  padding: var(--space-6);
  border-radius: var(--radius-2xl);
  border: 1px solid var(--line);
  background: var(--surface-modal);
  box-shadow: var(--shadow-lg);
}

.dialog-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-4);
  margin-bottom: var(--space-5);
}

.dialog-title {
  margin: 0;
  font-size: var(--font-size-xl);
  font-weight: var(--font-weight-semibold);
  color: var(--ink);
  line-height: var(--line-height-tight);
}

.dialog-close {
  flex-shrink: 0;
  display: grid;
  place-items: center;
  width: 32px;
  height: 32px;
  padding: 0;
  border: 1px solid var(--line);
  border-radius: 50%;
  background: var(--control-soft);
  color: var(--ink-soft);
  cursor: pointer;
  transition: border-color var(--transition-fast), background-color var(--transition-fast), color var(--transition-fast);
}

.dialog-close:hover {
  border-color: var(--line-strong);
  background: var(--control-soft-hover);
  color: var(--ink);
}

.dialog-close svg {
  width: 16px;
  height: 16px;
}

.dialog-body {
  margin-bottom: var(--space-5);
}

.dialog-message {
  margin: 0;
  font-size: var(--font-size-base);
  color: var(--ink-muted);
  line-height: var(--line-height-relaxed);
}

.dialog-input-label {
  font-size: var(--font-size-sm);
  font-weight: var(--font-weight-medium);
  color: var(--ink-soft);
}

.dialog-footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: var(--space-3);
}

.dialog-button {
  padding: 0.625rem 1.25rem;
  border: 1px solid var(--line);
  border-radius: var(--radius-full);
  font-size: var(--font-size-sm);
  font-weight: var(--font-weight-semibold);
  cursor: pointer;
  transition: border-color var(--transition-fast), background-color var(--transition-fast), box-shadow var(--transition-fast), opacity var(--transition-fast);
}

.dialog-button:disabled {
  opacity: 0.45;
  cursor: not-allowed;
  box-shadow: none;
}

.dialog-button-cancel {
  background: var(--surface-soft);
  color: var(--ink);
}

.dialog-button-cancel:hover {
  border-color: var(--line-strong);
  background: var(--surface-soft-hover);
}

.dialog-button-confirm {
  background: var(--button-primary-bg);
  border-color: var(--button-primary-border);
  color: var(--button-primary-ink);
}

.dialog-button-confirm:hover:not(:disabled) {
  box-shadow: var(--shadow-md);
}

.playlist-dialog-head {
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
  min-width: 0;
}

.playlist-dialog-track {
  display: flex;
  flex-direction: column;
  gap: 0.125rem;
  min-width: 0;
}

.playlist-dialog-track span {
  font-size: var(--font-size-xs);
  font-weight: var(--font-weight-semibold);
  color: var(--ink-muted);
}

.playlist-dialog-track strong,
.playlist-dialog-track small {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.playlist-dialog-track strong {
  font-size: var(--font-size-base);
  font-weight: var(--font-weight-semibold);
  color: var(--ink);
}

.playlist-dialog-track small {
  font-size: var(--font-size-sm);
  color: var(--ink-muted);
}

.playlist-dialog-body {
  display: grid;
  gap: var(--space-5);
}

.playlist-dialog-section {
  display: grid;
  gap: var(--space-3);
}

.playlist-dialog-grid {
  display: grid;
  gap: var(--space-3);
  grid-template-columns: repeat(auto-fit, minmax(168px, 1fr));
}

.playlist-dialog-grid--libraries {
  grid-template-columns: repeat(auto-fit, minmax(148px, 1fr));
}

.playlist-dialog-option {
  display: flex;
  align-items: center;
  gap: var(--space-3);
  min-height: 52px;
  padding: 0.875rem 1rem;
  border: 1px solid var(--line-soft);
  border-radius: var(--radius-lg);
  background: var(--surface-soft);
  text-align: left;
  transition: border-color var(--transition-normal), background-color var(--transition-normal), box-shadow var(--transition-normal), opacity var(--transition-normal);
}

.playlist-dialog-option:hover:not(:disabled) {
  border-color: var(--line);
  background: var(--surface-soft-hover);
}

.playlist-dialog-option.is-active {
  border-color: var(--line-strong);
  background: var(--surface-soft-active);
  box-shadow: var(--shadow-sm);
}

.playlist-dialog-option.is-disabled {
  opacity: 0.42;
  cursor: not-allowed;
}

.playlist-dialog-option svg {
  width: 16px;
  height: 16px;
  color: var(--ink-muted);
  flex: 0 0 auto;
}

.playlist-dialog-option strong {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: var(--font-size-base);
  font-weight: var(--font-weight-semibold);
  color: var(--ink);
}

.playlist-dialog-hint,
.playlist-dialog-empty {
  color: var(--ink-muted);
}

.dialog-enter-active,
.dialog-leave-active {
  transition: opacity var(--transition-normal);
}

.dialog-enter-active .dialog-modal,
.dialog-leave-active .dialog-modal {
  transition: opacity var(--transition-normal), transform var(--transition-normal);
}

.dialog-enter-from,
.dialog-leave-to {
  opacity: 0;
}

.dialog-enter-from .dialog-modal,
.dialog-leave-to .dialog-modal {
  opacity: 0;
  transform: translateY(12px) scale(0.98);
}

@media (max-width: 640px) {
  .dialog-backdrop {
    padding: var(--space-4);
  }

  .dialog-modal {
    width: calc(100vw - 24px);
    padding: var(--space-5);
    border-radius: var(--radius-xl);
  }

  .playlist-dialog-grid,
  .playlist-dialog-grid--libraries {
    grid-template-columns: 1fr;
  }
}
</style>
