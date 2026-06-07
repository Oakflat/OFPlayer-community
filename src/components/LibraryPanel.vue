<script setup lang="ts">
import { Plus, MoreHorizontal, Cloud, Disc2, Disc3, FolderOpen, HardDriveDownload, Heart, History, Library, ListMusic, Settings2, Users } from 'lucide-vue-next'
import { computed, ref, type Component } from 'vue'
import { useI18n } from '../composables/useI18n'
import { parseCollectionRef } from '../models/collection'
import { SUPPORTED_AUDIO_ACCEPT } from '../models/track'
import MenuDropdown, { type MenuDropdownItem } from './MenuDropdown.vue'
import DialogModal from './DialogModal.vue'

type ImportMode = 'browser-file-input' | 'native-dialog' | string
type ExternalLibrarySyncPhase = 'idle' | 'syncing' | 'ready' | 'error' | string
type DialogType =
  | 'create-library'
  | 'rename-library'
  | 'delete-library'
  | 'create-playlist'
  | 'rename-playlist'
  | 'delete-playlist'

interface SidebarItem {
  key: string
  id?: string
  label: string
  count?: number
  isDefault?: boolean
  isExternal?: boolean
  playlistKind?: string
  systemKey?: string
}

interface ExternalLibrarySyncStatus {
  active?: boolean
  phase?: ExternalLibrarySyncPhase
  remoteTotal?: number
  error?: string
}

interface LicenseFeatureLimits {
  canCreateLibrary?: boolean
  canConnectLibrary?: boolean
  canCreatePlaylist?: boolean
  libraryLimit?: number | null
  playlistLimit?: number | null
}

interface LibraryPanelProps {
  libraries?: SidebarItem[]
  playlists?: SidebarItem[]
  smartCollections?: SidebarItem[]
  activeLibrary?: string
  activeCollection?: string
  importMode?: ImportMode
  externalLibrarySyncStatus?: ExternalLibrarySyncStatus
  licenseFeatureLimits?: LicenseFeatureLimits
}

interface DialogState {
  isOpen: boolean
  type: DialogType | null
  title: string
  message: string
  inputValue: string
  inputLabel: string
  showInput: boolean
  isDanger: boolean
  onConfirm: (value: string) => void
}

const props = withDefaults(defineProps<LibraryPanelProps>(), {
  libraries: () => [],
  playlists: () => [],
  smartCollections: () => [],
  activeLibrary: 'local',
  activeCollection: 'all-tracks',
  importMode: 'browser-file-input',
  externalLibrarySyncStatus: () => ({
    active: false,
    phase: 'idle',
    remoteTotal: 0,
    error: '',
  }),
  licenseFeatureLimits: () => ({
    canCreateLibrary: true,
    canConnectLibrary: true,
    canCreatePlaylist: true,
    libraryLimit: null,
    playlistLimit: null,
  }),
})

const emit = defineEmits<{
  'request-import': []
  'request-folder-import': []
  'import-files': [files: File[]]
  'open-external-library': []
  'sync-external-library': [libraryKey: string]
  'set-active-library': [libraryKey: string]
  'set-active-collection': [collectionKey: string]
  'open-settings': []
  'create-library': [name: string]
  'rename-library': [libraryKey: string, name: string]
  'delete-library': [libraryKey: string]
  'create-playlist': [name: string]
  'rename-playlist': [playlistId: string, name: string]
  'delete-playlist': [playlistId: string]
}>()

const { t } = useI18n()

const iconByKey: Record<string, Component> = {
  'all-tracks': ListMusic,
  'recent-imports': HardDriveDownload,
  'all-plays': History,
  'all-favorites': Heart,
  'current-queue': Disc3,
  'albums': Disc2,
  'artists': Users,
}

// CN: 菜单状态
// EN: Menu state
const openMenuKey = ref<string | null>(null)
const menuAnchorEl = ref<HTMLElement | null>(null)
const dialogState = ref<DialogState>({
  isOpen: false,
  type: null,
  title: '',
  message: '',
  inputValue: '',
  inputLabel: '',
  showInput: false,
  isDanger: false,
  onConfirm: () => {},
})
const usesNativeImportPicker = computed(() => props.importMode === 'native-dialog')
const externalLibrarySyncMessage = computed(() => {
  const status = props.externalLibrarySyncStatus ?? {}

  if (status.phase === 'syncing' || status.active === true) {
    return t('sidebar.externalSync.syncing')
  }

  if (status.phase === 'ready') {
    return t('sidebar.externalSync.ready', { count: status.remoteTotal ?? 0 })
  }

  if (status.phase === 'error') {
    return status.error || t('sidebar.externalSync.error')
  }

  return ''
})
const externalLibrarySyncTone = computed(() => {
  const phase = props.externalLibrarySyncStatus?.phase

  if (phase === 'error') {
    return 'is-error'
  }

  if (phase === 'ready') {
    return 'is-ready'
  }

  return 'is-syncing'
})
const canCreateLibrary = computed(() => props.licenseFeatureLimits?.canCreateLibrary !== false)
const canConnectLibrary = computed(() => props.licenseFeatureLimits?.canConnectLibrary !== false)
const canCreatePlaylist = computed(() => props.licenseFeatureLimits?.canCreatePlaylist !== false)
const libraryLimitMessage = computed(() =>
  t('sidebar.limits.libraryReached', {
    limit: props.licenseFeatureLimits?.libraryLimit ?? 3,
  }),
)
const playlistLimitMessage = computed(() =>
  t('sidebar.limits.playlistReached', {
    limit: props.licenseFeatureLimits?.playlistLimit ?? 10,
  }),
)
const libraryActionsMenuItems = computed<MenuDropdownItem[]>(() => [
  {
    key: 'create-library',
    label: t('sidebar.dialogs.newLibraryTitle'),
    disabled: !canCreateLibrary.value,
  },
  {
    key: 'connect-external-library',
    label: t('sidebar.connectExternalLibrary'),
    disabled: !canConnectLibrary.value,
  },
])

function resolveItemIcon(item: SidebarItem) {
  if (item?.isExternal) {
    return Cloud
  }

  if (item?.isDefault) {
    return Library
  }

  const iconKey = item?.systemKey ?? item?.id ?? item?.key
  return iconByKey[iconKey] ?? ListMusic
}

function handleImport(event: Event) {
  const input = event.target as HTMLInputElement | null
  const files = Array.from(input?.files ?? [])

  if (files.length > 0) {
    emit('import-files', files)
  }

  if (input) {
    input.value = ''
  }
}

function handleRequestImport() {
  emit('request-import')
}

function handleRequestFolderImport() {
  emit('request-folder-import')
}

// CN: 判断是否为默认库（不可删除）
// EN: Check if it's the default library (cannot be deleted)
function isDefaultLibrary(item: SidebarItem) {
  return Boolean(item?.isDefault)
}

// CN: 判断是否为系统歌单（不可删除、不可重命名）
// EN: Check if it's a system playlist (cannot be deleted or renamed)
function isSystemCollection(item: SidebarItem) {
  return item?.playlistKind === 'system' || Boolean(item?.systemKey)
}

// CN: 获取库菜单项
// EN: Get library menu items
function getLibraryMenuItems(item: SidebarItem): MenuDropdownItem[] {
  const items: MenuDropdownItem[] = []

  if (item?.isExternal) {
    items.push({ key: 'sync', label: t('sidebar.actions.syncLibrary') })
  }

  items.push({ key: 'rename', label: t('sidebar.actions.rename') })

  if (!isDefaultLibrary(item)) {
    items.push({ key: 'delete', label: t('sidebar.actions.deleteLibrary') })
  } else {
    items.push({ key: 'delete', label: t('sidebar.actions.deleteLibrary'), disabled: true })
  }

  return items
}

// CN: 获取歌单菜单项
// EN: Get playlist menu items
function getPlaylistMenuItems(item: SidebarItem): MenuDropdownItem[] {
  if (isSystemCollection(item)) {
    return [
      { key: 'rename', label: t('sidebar.actions.rename'), disabled: true },
      { key: 'delete', label: t('sidebar.actions.deletePlaylist'), disabled: true },
    ]
  }

  return [
    { key: 'rename', label: t('sidebar.actions.rename') },
    { key: 'delete', label: t('sidebar.actions.deletePlaylist') },
  ]
}

// CN: 菜单操作
// EN: Menu operations
function toggleMenu(key: string, event: MouseEvent) {
  if (openMenuKey.value === key) {
    closeMenu()
    return
  }

  openMenuKey.value = key
  menuAnchorEl.value = event.currentTarget instanceof HTMLElement ? event.currentTarget : null
}

function closeMenu() {
  openMenuKey.value = null
  menuAnchorEl.value = null
}

function handleLibraryMenuSelect(item: SidebarItem, action: MenuDropdownItem) {
  closeMenu()

  if (action.key === 'rename') {
    openRenameLibraryDialog(item)
  } else if (action.key === 'delete') {
    openDeleteLibraryDialog(item)
  } else if (action.key === 'sync') {
    emit('sync-external-library', item.key)
  }
}

function handleLibraryActionsSelect(action: MenuDropdownItem) {
  closeMenu()

  if (action.disabled) {
    return
  }

  if (action.key === 'create-library') {
    openCreateLibraryDialog()
  } else if (action.key === 'connect-external-library') {
    emit('open-external-library')
  }
}

function handlePlaylistMenuSelect(item: SidebarItem, action: MenuDropdownItem) {
  closeMenu()

  if (action.key === 'rename') {
    openRenamePlaylistDialog(item)
  } else if (action.key === 'delete') {
    openDeletePlaylistDialog(item)
  }
}

// CN: 对话框操作
// EN: Dialog operations
function openCreateLibraryDialog() {
  if (!canCreateLibrary.value) {
    return
  }

  dialogState.value = {
    isOpen: true,
    type: 'create-library',
    title: t('sidebar.dialogs.newLibraryTitle'),
    message: '',
    inputValue: '',
    inputLabel: t('sidebar.dialogs.newLibraryInput'),
    showInput: true,
    isDanger: false,
    onConfirm: (name) => {
      if (name.trim()) {
        emit('create-library', name.trim())
      }
    },
  }
}

function openRenameLibraryDialog(item: SidebarItem) {
  dialogState.value = {
    isOpen: true,
    type: 'rename-library',
    title: t('sidebar.dialogs.renameLibraryTitle'),
    message: '',
    inputValue: item.label,
    inputLabel: '',
    showInput: true,
    isDanger: false,
    onConfirm: (name) => {
      if (name.trim()) {
        emit('rename-library', item.key, name.trim())
      }
    },
  }
}

function openDeleteLibraryDialog(item: SidebarItem) {
  dialogState.value = {
    isOpen: true,
    type: 'delete-library',
    title: t('sidebar.actions.deleteLibrary'),
    message: t('sidebar.dialogs.deleteLibraryConfirm'),
    inputValue: '',
    inputLabel: '',
    showInput: false,
    isDanger: true,
    onConfirm: () => {
      emit('delete-library', item.key)
    },
  }
}

function openCreatePlaylistDialog() {
  if (!canCreatePlaylist.value) {
    return
  }

  dialogState.value = {
    isOpen: true,
    type: 'create-playlist',
    title: t('sidebar.dialogs.newPlaylistTitle'),
    message: '',
    inputValue: '',
    inputLabel: t('sidebar.dialogs.newPlaylistInput'),
    showInput: true,
    isDanger: false,
    onConfirm: (name) => {
      if (name.trim()) {
        emit('create-playlist', name.trim())
      }
    },
  }
}

function openRenamePlaylistDialog(item: SidebarItem) {
  const parsed = parseCollectionRef(item.key)
  const playlistId = typeof parsed.value === 'string' ? parsed.value : ''

  if (!playlistId) {
    return
  }

  dialogState.value = {
    isOpen: true,
    type: 'rename-playlist',
    title: t('sidebar.dialogs.renamePlaylistTitle'),
    message: '',
    inputValue: item.label,
    inputLabel: '',
    showInput: true,
    isDanger: false,
    onConfirm: (name) => {
      if (name.trim()) {
        emit('rename-playlist', playlistId, name.trim())
      }
    },
  }
}

function openDeletePlaylistDialog(item: SidebarItem) {
  const parsed = parseCollectionRef(item.key)
  const playlistId = typeof parsed.value === 'string' ? parsed.value : ''

  if (!playlistId) {
    return
  }

  dialogState.value = {
    isOpen: true,
    type: 'delete-playlist',
    title: t('sidebar.actions.deletePlaylist'),
    message: t('sidebar.dialogs.deletePlaylistConfirm'),
    inputValue: '',
    inputLabel: '',
    showInput: false,
    isDanger: true,
    onConfirm: () => {
      emit('delete-playlist', playlistId)
    },
  }
}

function handleDialogConfirm(value: string) {
  dialogState.value.onConfirm(value)
  closeDialog()
}

function closeDialog() {
  dialogState.value.isOpen = false
}
</script>

<template>
  <aside class="panel panel-sidebar">
    <div class="sidebar-nav-head">
      <p class="eyebrow">{{ t('sidebar.navigationEyebrow') }}</p>
      <h1>{{ t('sidebar.navigationTitle') }}</h1>
    </div>

    <div class="sidebar-nav-stack">
      <section class="sidebar-group sidebar-selector-panel" :aria-label="t('sidebar.librarySection')">
        <div class="sidebar-group-header">
          <p class="sidebar-group-label">{{ t('sidebar.librarySection') }}</p>
          <button
            type="button"
            class="sidebar-group-action"
            :aria-label="t('sidebar.actions.libraryActions')"
            :aria-expanded="openMenuKey === 'library-actions'"
            :title="canCreateLibrary || canConnectLibrary ? t('sidebar.actions.libraryActions') : libraryLimitMessage"
            @click="toggleMenu('library-actions', $event)"
          >
            <Plus aria-hidden="true" />
          </button>
          <MenuDropdown
            :is-open="openMenuKey === 'library-actions'"
            :anchor-el="openMenuKey === 'library-actions' ? menuAnchorEl : null"
            :items="libraryActionsMenuItems"
            @close="closeMenu"
            @select="handleLibraryActionsSelect"
          />
        </div>
        <TransitionGroup name="list" tag="div" class="sidebar-selector-list">
          <div
            v-for="item in libraries"
            :key="item.key"
            class="sidebar-selector-item-wrap"
            :class="{ 'is-menu-open': openMenuKey === `library-${item.key}`, 'has-menu': true }"
          >
            <button
              class="sidebar-selector-item"
              :class="{ 'is-active': item.key === activeLibrary }"
              type="button"
              :aria-pressed="item.key === activeLibrary"
              @click="emit('set-active-library', item.key)"
            >
              <component :is="resolveItemIcon(item)" class="sidebar-selector-icon" aria-hidden="true" />
              <span class="sidebar-selector-name">{{ item.label }}</span>
              <strong class="sidebar-selector-count">{{ item.count }}</strong>
            </button>
            <button
              type="button"
              class="sidebar-selector-more"
              :class="{ 'is-visible': openMenuKey === `library-${item.key}` }"
              :aria-label="t('sidebar.actions.more')"
              @click.stop="toggleMenu(`library-${item.key}`, $event)"
            >
              <MoreHorizontal aria-hidden="true" />
            </button>
            <MenuDropdown
              :is-open="openMenuKey === `library-${item.key}`"
              :anchor-el="openMenuKey === `library-${item.key}` ? menuAnchorEl : null"
              :items="getLibraryMenuItems(item)"
              @close="closeMenu"
              @select="(action) => handleLibraryMenuSelect(item, action)"
            />
          </div>
        </TransitionGroup>
        <div
          v-if="externalLibrarySyncMessage"
          class="sidebar-sync-status"
          :class="externalLibrarySyncTone"
        >
          <Cloud aria-hidden="true" />
          <span>{{ externalLibrarySyncMessage }}</span>
        </div>
      </section>

      <section class="sidebar-group sidebar-selector-panel" :aria-label="t('sidebar.playlistSection')">
        <div class="sidebar-group-header">
          <p class="sidebar-group-label">{{ t('sidebar.playlistSection') }}</p>
          <button
            type="button"
            class="sidebar-group-action"
            :aria-label="t('sidebar.actions.newPlaylist')"
            :title="canCreatePlaylist ? t('sidebar.actions.newPlaylist') : playlistLimitMessage"
            :disabled="!canCreatePlaylist"
            @click="openCreatePlaylistDialog"
          >
            <Plus aria-hidden="true" />
          </button>
        </div>
        <TransitionGroup name="list" tag="div" class="sidebar-selector-list">
          <div
            v-for="item in playlists"
            :key="item.key"
            class="sidebar-selector-item-wrap"
            :class="{
              'is-menu-open': openMenuKey === `playlist-${item.key}`,
              'has-menu': !isSystemCollection(item),
            }"
          >
            <button
              class="sidebar-selector-item"
              :class="{ 'is-active': item.key === activeCollection }"
              type="button"
              :aria-pressed="item.key === activeCollection"
              @click="emit('set-active-collection', item.key)"
            >
              <component :is="resolveItemIcon(item)" class="sidebar-selector-icon" aria-hidden="true" />
              <span class="sidebar-selector-name">{{ item.label }}</span>
              <strong class="sidebar-selector-count">{{ item.count }}</strong>
            </button>
            <button
              v-if="!isSystemCollection(item)"
              type="button"
              class="sidebar-selector-more"
              :class="{ 'is-visible': openMenuKey === `playlist-${item.key}` }"
              :aria-label="t('sidebar.actions.more')"
              @click.stop="toggleMenu(`playlist-${item.key}`, $event)"
            >
              <MoreHorizontal aria-hidden="true" />
            </button>
            <MenuDropdown
              v-if="!isSystemCollection(item)"
              :is-open="openMenuKey === `playlist-${item.key}`"
              :anchor-el="openMenuKey === `playlist-${item.key}` ? menuAnchorEl : null"
              :items="getPlaylistMenuItems(item)"
              @close="closeMenu"
              @select="(action) => handlePlaylistMenuSelect(item, action)"
            />
          </div>
        </TransitionGroup>
      </section>

      <section
        class="sidebar-group sidebar-selector-panel"
        :aria-label="t('sidebar.smartCollectionSection')"
      >
        <p class="sidebar-group-label">{{ t('sidebar.smartCollectionSection') }}</p>
        <TransitionGroup name="list" tag="div" class="sidebar-selector-list">
          <button
            v-for="item in smartCollections"
            :key="item.key"
            class="sidebar-selector-item"
            :class="{ 'is-active': item.key === activeCollection }"
            type="button"
            :aria-pressed="item.key === activeCollection"
            @click="emit('set-active-collection', item.key)"
          >
            <component :is="resolveItemIcon(item)" class="sidebar-selector-icon" aria-hidden="true" />
            <span class="sidebar-selector-name">{{ item.label }}</span>
            <strong class="sidebar-selector-count">{{ item.count }}</strong>
          </button>
        </TransitionGroup>
      </section>
    </div>

    <div class="sidebar-actions">
      <button
        v-if="usesNativeImportPicker"
        class="sidebar-action"
        type="button"
        @click="handleRequestImport"
      >
        <span class="sidebar-settings-icon-wrap" aria-hidden="true">
          <HardDriveDownload class="sidebar-settings-icon" />
        </span>
        <span class="sidebar-settings-name">{{ t('sidebar.importAudio') }}</span>
      </button>
      <button
        v-if="usesNativeImportPicker"
        class="sidebar-action"
        type="button"
        @click="handleRequestFolderImport"
      >
        <span class="sidebar-settings-icon-wrap" aria-hidden="true">
          <FolderOpen class="sidebar-settings-icon" />
        </span>
        <span class="sidebar-settings-name">{{ t('sidebar.importFolder') }}</span>
      </button>
      <label v-else class="sidebar-action" for="sidebar-audio-import">
        <span class="sidebar-settings-icon-wrap" aria-hidden="true">
          <HardDriveDownload class="sidebar-settings-icon" />
        </span>
        <span class="sidebar-settings-name">{{ t('sidebar.importAudio') }}</span>
      </label>
      <input
        v-if="!usesNativeImportPicker"
        id="sidebar-audio-import"
        class="sr-only"
        type="file"
        :accept="SUPPORTED_AUDIO_ACCEPT"
        multiple
        @change="handleImport"
      />
      <button class="sidebar-settings" type="button" @click="emit('open-settings')">
        <span class="sidebar-settings-icon-wrap" aria-hidden="true">
          <Settings2 class="sidebar-settings-icon" />
        </span>
        <span class="sidebar-settings-name">{{ t('sidebar.settings') }}</span>
      </button>
    </div>

    <DialogModal
      :is-open="dialogState.isOpen"
      :title="dialogState.title"
      :message="dialogState.message"
      :input-label="dialogState.inputLabel"
      :input-value="dialogState.inputValue"
      :show-input="dialogState.showInput"
      :is-danger="dialogState.isDanger"
      @close="closeDialog"
      @confirm="handleDialogConfirm"
    />
  </aside>
</template>

<style scoped>
.sidebar-group-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--space-2);
  padding: 0 0.375rem;
  margin-bottom: 0.25rem;
}

.sidebar-group-action {
  flex-shrink: 0;
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  padding: 0;
  border: none;
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--ink-muted);
  cursor: pointer;
  transition: all var(--transition-fast);
  opacity: 0;
}

.sidebar-group-header:hover .sidebar-group-action,
.sidebar-group-action:focus {
  opacity: 1;
}

.sidebar-group-action:hover:not(:disabled) {
  background: var(--state-layer-hover);
  color: var(--ink);
}

.sidebar-group-action:disabled {
  cursor: not-allowed;
  opacity: 0.38;
}

.sidebar-group-action svg {
  width: 16px;
  height: 16px;
}

.sidebar-sync-status {
  display: flex;
  align-items: flex-start;
  gap: var(--space-2);
  padding: 0.45rem 0.5rem 0.25rem;
  color: var(--ink-muted);
  font-size: var(--font-size-xs);
  line-height: 1.4;
}

.sidebar-sync-status svg {
  flex: 0 0 auto;
  width: 14px;
  height: 14px;
  margin-top: 0.1rem;
}

.sidebar-sync-status span {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}

.sidebar-sync-status.is-ready {
  color: var(--of-playing);
}

.sidebar-sync-status.is-error {
  color: var(--of-danger);
}

.sidebar-selector-item-wrap {
  position: relative;
}

.sidebar-selector-item-wrap.has-menu .sidebar-selector-item {
  padding-inline-end: 2.625rem;
}

.sidebar-selector-more {
  display: grid;
  place-items: center;
  position: absolute;
  top: 50%;
  right: 0.625rem;
  width: 24px;
  height: 24px;
  padding: 0;
  border: none;
  border-radius: var(--radius-sm);
  background: transparent;
  color: var(--ink-muted);
  cursor: pointer;
  transition: all var(--transition-fast);
  transform: translateY(-50%);
  opacity: 0;
  z-index: 3;
}

.sidebar-selector-item-wrap:hover .sidebar-selector-more,
.sidebar-selector-item-wrap.is-menu-open .sidebar-selector-more,
.sidebar-selector-more:focus,
.sidebar-selector-more.is-visible {
  opacity: 1;
}

.sidebar-selector-more:hover {
  background: var(--state-layer-hover);
  color: var(--ink);
}

.sidebar-selector-more svg {
  width: 16px;
  height: 16px;
}
</style>
