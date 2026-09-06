<script setup>
import { computed, ref, watchEffect } from 'vue'
import LibraryPanel from '../src/components/LibraryPanel.vue'
import PlayerPanel from '../src/components/PlayerPanel.vue'
import SettingsModal from '../src/components/SettingsModal.vue'
import ImmersivePlayerView from '../src/components/ImmersivePlayerView.vue'
import TrackNavRail from '../src/components/TrackNavRail.vue'
import { setLocalePreference, useI18n } from '../src/composables/useI18n'

const language = ref('en')
const theme = ref('mist')
const colorScheme = ref('light')
const motion = ref('full')
const previewMode = ref('workspace')
const immersiveOpen = ref(false)
const previewScroll = ref(null)
const previewScrollTop = ref(0)
const settingsOpen = ref(false)
const searchQuery = ref('')
const typeFilter = ref('all')
const sortOption = ref('recent')
const activeCollection = ref('view:all-tracks')
const { t } = useI18n()
watchEffect(() => {
  setLocalePreference(language.value)
  document.documentElement.dataset.theme = theme.value
  document.documentElement.dataset.effectiveColorScheme = colorScheme.value
  document.documentElement.dataset.motion = motion.value
})
const libraries = computed(() => [{ key: 'local', id: 'local', isDefault: true, label: t('sidebar.libraries.local'), count: 12480 }])
const playlists = computed(() => [{ key: 'view:all-tracks', systemKey: 'all-tracks', label: t('sidebar.playlists.allTracks'), count: 12480 }])
const smartCollections = computed(() => [
  ['recent-imports', 'recentImports'], ['all-plays', 'recentlyPlayed'],
  ['all-favorites', 'favorites'], ['current-queue', 'currentQueue'], ['albums', 'albums'], ['artists', 'artists'],
].map(([key, message]) => ({ key: `view:${key}`, systemKey: key, label: t(`sidebar.smartCollections.${message}`), count: 12345 })))
const tracks = Array.from({ length: 24 }, (_, index) => ({
  id: `fixture-${index}`, libraryId: 'local', title: index % 2 ? 'A Quiet Afternoon' : 'Somewhere Beyond the City Lights — Extended Instrumental Edition',
  displayTitle: index % 2 ? 'A Quiet Afternoon' : 'Somewhere Beyond the City Lights — Extended Instrumental Edition',
  artist: 'The Midnight Orchestra & Friends', album: 'Collected Recordings: The Complete Sessions',
  duration: 245, format: 'FLAC', fileName: `fixture-${index}.flac`, size: 32000000,
  source: { kind: 'local', path: `D:/Fixture/fixture-${index}.flac` },
}))
const currentTrack = ref(tracks[0])
const collection = computed(() => ({
  label: [...playlists.value, ...smartCollections.value].find(item => item.key === activeCollection.value)?.label,
  isBrowserView: ['view:albums', 'view:artists'].includes(activeCollection.value),
  browserKind: activeCollection.value === 'view:artists' ? 'artists' : 'albums',
}))
const outputDevices = [{ id: 'fixture-output', name: 'Speakers (High Definition USB Audio Device)', isDefault: true }]
const navTracks = Array.from({ length: 120 }, (_, index) => ({ ...tracks[index % tracks.length], id: `nav-${index}`, monogram: 'OF' }))
async function loadNavTracks(start, count) { return navTracks.slice(start, start + count) }
</script>

<template>
  <div class="preview-controls">
    <strong>UI preview · Fixture data · No desktop backend</strong>
    <label>Language <select v-model="language"><option value="en">English</option><option value="zh-CN">简体中文</option></select></label>
    <label>Theme <select v-model="theme"><option>mist</option><option>paper</option><option>material</option></select></label>
    <label>Color <select v-model="colorScheme"><option>light</option><option>dark</option></select></label>
    <label>Motion <select v-model="motion"><option>full</option><option>reduced</option></select></label>
    <label>Preview <select v-model="previewMode"><option value="workspace">Workspace</option><option value="navigation">Navigation</option></select></label>
    <button @click="immersiveOpen = true">Immersive preview</button>
  </div>
  <div v-if="previewMode === 'navigation'" class="preview-nav-stage">
    <div ref="previewScroll" class="preview-nav-scroll" @scroll="previewScrollTop = $event.target.scrollTop">
      <div v-for="(track, index) in navTracks" :key="track.id" class="preview-nav-row">{{ index + 1 }} · {{ track.title }}</div>
    </div>
    <TrackNavRail :total-count="navTracks.length" :scroll-top="previewScrollTop" :scroll-container="previewScroll" :load-group-tracks="loadNavTracks" />
  </div>
  <main v-else class="page-shell">
    <section class="player-shell">
      <LibraryPanel :libraries="libraries" :playlists="playlists" :smart-collections="smartCollections" :active-collection="activeCollection"
        import-mode="native-dialog" @set-active-collection="activeCollection = $event" @open-settings="settingsOpen = true" />
      <PlayerPanel :tracks="tracks" :current-track="currentTrack" :current-track-id="currentTrack.id" :has-any-tracks="true"
        :current-library="collection.isBrowserView ? libraries[0] : null"
        :current-collection="collection" :active-collection="activeCollection" :search-query="searchQuery" :sort-option="sortOption" :type-filter="typeFilter"
        :duration="245" :current-time="62" active-playback-output-device-name="Speakers (High Definition USB Audio Device)"
        @select-track="currentTrack = tracks.find(track => track.id === $event) || currentTrack"
        @set-search-query="searchQuery = $event" @set-sort-option="sortOption = $event.sortOption" @set-type-filter="typeFilter = $event" />
    </section>
  </main>
  <Transition name="immersive">
    <ImmersivePlayerView v-if="immersiveOpen" :current-track="currentTrack" :current-time="62" :duration="245" :windowed="true"
      @close="immersiveOpen = false" @play-next="currentTrack = tracks[(tracks.indexOf(currentTrack) + 1) % tracks.length]"
      @play-previous="currentTrack = tracks[(tracks.indexOf(currentTrack) + tracks.length - 1) % tracks.length]" />
  </Transition>
  <SettingsModal :is-open="settingsOpen" :language="language" :theme="theme" :color-scheme="colorScheme" :motion="motion"
    :playback-output-devices="outputDevices" last-scan-at="2026-09-06T08:00:00Z"
    :app-update-state="{ currentVersion: '26.0.4', lastCheckedAt: '2026-09-06T08:00:00Z' }"
    :telemetry-enabled="false" :can-manage-storage="true" storage-root="D:\Music\Collected Recordings and Extended Instrumental Sessions"
    @close="settingsOpen = false" @set-language="language = $event" @set-theme="theme = $event" @set-color-scheme="colorScheme = $event" @set-motion="motion = $event" />
</template>

<style scoped>
.preview-controls { display: flex; align-items: center; flex-wrap: wrap; gap: 16px; padding: 8px 16px; font: 13px system-ui; background: var(--surface-base); color: var(--ink); }
.preview-controls select { margin-left: 6px; color: inherit; background: var(--surface-elevated); }
.page-shell { height: calc(100dvh - 42px); min-height: 0; }
.preview-nav-stage { position: relative; height: calc(100dvh - 80px); background: var(--surface-base); }
.preview-nav-scroll { height: 100%; overflow-y: auto; }
.preview-nav-row { height: 76px; display: flex; align-items: center; padding-left: 64px; color: var(--ink); border-bottom: 1px solid var(--line-soft); }
</style>
