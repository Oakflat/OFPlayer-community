<script setup lang="ts">
import { BarChart3, CalendarDays, Clock, Disc3, Flame, Headphones, Music2, RefreshCw, TrendingUp, X } from 'lucide-vue-next'
import { computed, ref, watch } from 'vue'
import { useI18n } from '../composables/useI18n'

interface ListeningStatsSummary {
  totalSeconds?: number
  playCount?: number
  trackCount?: number
  albumCount?: number
  activeDays?: number
  peakDay?: string | null
  peakDaySeconds?: number
  longestStreakDays?: number
}

interface ListeningStatsDailyBucket {
  date: string
  seconds: number
  playCount?: number
}

interface ListeningStatsTrackRank {
  trackId: string
  title: string
  artist: string
  album: string
  albumArtist: string
  artwork?: string
  duration: number
  listenSeconds: number
  playCount: number
}

interface ListeningStatsAlbumGroup {
  key: string
  album: string
  albumArtist: string
  artwork?: string
  listenSeconds: number
  playCount: number
  trackCount: number
  tracks: ListeningStatsTrackRank[]
}

interface ListeningStatsSnapshot {
  generatedAt: string
  libraryId: string | null
  days: number
  summary: ListeningStatsSummary
  daily: ListeningStatsDailyBucket[]
  topTracks: ListeningStatsTrackRank[]
  albumGroups: ListeningStatsAlbumGroup[]
}

interface ListeningStatsRequest {
  libraryId?: string | null
  days?: number
  trackLimit?: number
  albumLimit?: number
  albumTrackLimit?: number
  timezoneOffsetMinutes?: number
}

type ListeningStatsLoader = (request: ListeningStatsRequest) => Promise<ListeningStatsSnapshot>

interface HeatmapCell {
  key: string
  date: string
  seconds: number
  level: number
  empty: boolean
  title: string
}

interface TrackRankRow extends ListeningStatsTrackRank {
  artworkUrl: string
}

interface AlbumGroupRow extends ListeningStatsAlbumGroup {
  artworkUrl: string
}

const props = withDefaults(defineProps<{
  isOpen?: boolean
  libraryId?: string | null
  libraryName?: string
  revision?: string | number
  loadStats: ListeningStatsLoader
}>(), {
  isOpen: false,
  libraryId: null,
  libraryName: '',
  revision: 0,
})

const emit = defineEmits<{
  close: []
  'select-track': [trackId: string]
}>()

const { locale, t } = useI18n()
const selectedDays = ref(365)
const isLoading = ref(false)
const errorMessage = ref('')
const stats = ref<ListeningStatsSnapshot>(createEmptyStats(selectedDays.value))
const failedArtworkUrls = ref(new Set<string>())
let loadRequestId = 0

const rangeOptions = computed(() => [
  { key: 'year', value: 365, label: t('listeningStats.ranges.year') },
  { key: 'half-year', value: 180, label: t('listeningStats.ranges.halfYear') },
  { key: 'quarter', value: 90, label: t('listeningStats.ranges.quarter') },
])

const summary = computed(() => ({
  totalSeconds: normalizeNumber(stats.value.summary?.totalSeconds),
  playCount: normalizeInteger(stats.value.summary?.playCount),
  trackCount: normalizeInteger(stats.value.summary?.trackCount),
  albumCount: normalizeInteger(stats.value.summary?.albumCount),
  activeDays: normalizeInteger(stats.value.summary?.activeDays),
  peakDay: stats.value.summary?.peakDay ?? null,
  peakDaySeconds: normalizeNumber(stats.value.summary?.peakDaySeconds),
  longestStreakDays: normalizeInteger(stats.value.summary?.longestStreakDays),
}))

const hasStats = computed(() => summary.value.totalSeconds > 0 || summary.value.playCount > 0)
const libraryLabel = computed(() => props.libraryName || t('player.library'))
const subtitle = computed(() =>
  t('listeningStats.subtitle', {
    library: libraryLabel.value,
    days: stats.value.days || selectedDays.value,
  }),
)
const generatedAtLabel = computed(() => {
  if (!stats.value.generatedAt) {
    return ''
  }

  return t('listeningStats.generatedAt', {
    time: formatDateTime(stats.value.generatedAt),
  })
})

const heroMetrics = computed(() => [
  {
    key: 'plays',
    icon: Music2,
    label: t('listeningStats.metrics.playCount'),
    value: String(summary.value.playCount),
  },
  {
    key: 'active-days',
    icon: CalendarDays,
    label: t('listeningStats.metrics.activeDays'),
    value: t('listeningStats.units.days', { count: summary.value.activeDays }),
  },
  {
    key: 'streak',
    icon: Flame,
    label: t('listeningStats.metrics.longestStreak'),
    value: t('listeningStats.units.days', { count: summary.value.longestStreakDays }),
  },
])

const peakDayDateLabel = computed(() =>
  summary.value.peakDay ? formatDateKey(summary.value.peakDay) : '',
)
const peakDayDurationLabel = computed(() => formatDuration(summary.value.peakDaySeconds))
const peakCellDate = computed(() => summary.value.peakDay ?? '')

const dailyBuckets = computed(() =>
  [...stats.value.daily]
    .filter((day) => day?.date)
    .sort((left, right) => left.date.localeCompare(right.date)),
)
const maxDailySeconds = computed(() =>
  dailyBuckets.value.reduce((maxSeconds, day) => Math.max(maxSeconds, normalizeNumber(day.seconds)), 0),
)
const heatmapCells = computed<HeatmapCell[]>(() => {
  const days = dailyBuckets.value

  if (days.length === 0) {
    return []
  }

  const firstDate = parseDateKey(days[0].date)
  const leadingEmptyDays = firstDate ? firstDate.getDay() : 0
  const cells: HeatmapCell[] = Array.from({ length: leadingEmptyDays }, (_, index) => ({
    key: `empty-${index}`,
    date: '',
    seconds: 0,
    level: 0,
    empty: true,
    title: '',
  }))

  for (const day of days) {
    const seconds = normalizeNumber(day.seconds)
    cells.push({
      key: day.date,
      date: day.date,
      seconds,
      level: resolveHeatLevel(seconds, maxDailySeconds.value),
      empty: false,
      title: `${formatDateKey(day.date)} · ${formatDuration(seconds)}`,
    })
  }

  return cells
})
const heatmapWeekCount = computed(() => Math.max(1, Math.ceil(heatmapCells.value.length / 7)))
const hasHeatmap = computed(() => heatmapCells.value.some((cell) => !cell.empty))
const heatLegendLevels = [0, 1, 2, 3, 4]
const heatmapStyle = computed(() => ({
  '--stats-week-count': String(heatmapWeekCount.value),
}))
const heatmapMonthSlots = computed(() => {
  const monthFormatter = new Intl.DateTimeFormat(locale.value, { month: 'short' })

  return Array.from({ length: heatmapWeekCount.value }, (_, weekIndex) => {
    const weekCells = heatmapCells.value.slice(weekIndex * 7, weekIndex * 7 + 7)
    const firstDay = weekCells.find((cell) => !cell.empty && cell.date)
    const date = firstDay ? parseDateKey(firstDay.date) : null
    const shouldShow = Boolean(date && (weekIndex === 0 || date.getDate() <= 7))

    return {
      key: `week-${weekIndex}`,
      label: shouldShow && date ? monthFormatter.format(date) : '',
    }
  })
})

function resolveArtworkUrl(...candidates: Array<string | undefined | null>): string {
  const failedUrls = failedArtworkUrls.value

  for (const candidate of candidates) {
    const artwork = typeof candidate === 'string' ? candidate.trim() : ''

    if (artwork && !failedUrls.has(artwork)) {
      return artwork
    }
  }
  return ''
}

function markArtworkFailed(url: string) {
  if (!url || failedArtworkUrls.value.has(url)) {
    return
  }

  const nextFailedUrls = new Set(failedArtworkUrls.value)
  nextFailedUrls.add(url)
  failedArtworkUrls.value = nextFailedUrls
}

const topTracks = computed<TrackRankRow[]>(() =>
  stats.value.topTracks
    .filter((track) => track.trackId)
    .map((track) => ({ ...track, artworkUrl: resolveArtworkUrl(track.artwork) })),
)
const albumGroups = computed<AlbumGroupRow[]>(() =>
  stats.value.albumGroups
    .filter((album) => album.key)
    .map((album) => ({
      ...album,
      artworkUrl: resolveArtworkUrl(
        album.artwork,
        ...album.tracks.map((track) => track.artwork),
      ),
    })),
)
const albumTrackCountLabel = (album: ListeningStatsAlbumGroup) =>
  t('player.albumBrowser.trackCountShort', { count: album.trackCount })

watch(
  () => [props.isOpen, props.libraryId, props.revision, selectedDays.value] as const,
  () => {
    if (props.isOpen) {
      void refreshStats()
    }
  },
  { immediate: true },
)

function createEmptyStats(days: number): ListeningStatsSnapshot {
  return {
    generatedAt: '',
    libraryId: null,
    days,
    summary: {},
    daily: [],
    topTracks: [],
    albumGroups: [],
  }
}

function normalizeNumber(value: unknown): number {
  return Number.isFinite(value) ? Math.max(0, Number(value)) : 0
}

function normalizeInteger(value: unknown): number {
  return Number.isInteger(value) && Number(value) >= 0 ? Number(value) : 0
}

function parseDateKey(value: string): Date | null {
  const parts = value.split('-').map((part) => Number(part))

  if (parts.length !== 3 || parts.some((part) => !Number.isInteger(part))) {
    return null
  }

  return new Date(parts[0], parts[1] - 1, parts[2])
}

function formatDateKey(value: string): string {
  const date = parseDateKey(value)

  if (!date) {
    return value
  }

  return new Intl.DateTimeFormat(locale.value, {
    month: 'short',
    day: 'numeric',
  }).format(date)
}

function formatDateTime(value: string): string {
  const date = new Date(value)

  if (Number.isNaN(date.getTime())) {
    return value
  }

  return new Intl.DateTimeFormat(locale.value, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date)
}

function formatDuration(seconds: unknown): string {
  const totalSeconds = Math.round(normalizeNumber(seconds))

  if (totalSeconds >= 3600) {
    const hours = Math.floor(totalSeconds / 3600)
    const minutes = Math.round((totalSeconds % 3600) / 60)
    return t('listeningStats.units.hoursMinutes', { hours, minutes })
  }

  if (totalSeconds >= 60) {
    return t('listeningStats.units.minutes', {
      minutes: Math.max(1, Math.round(totalSeconds / 60)),
    })
  }

  return t('listeningStats.units.seconds', { seconds: totalSeconds })
}

function formatPlayCount(value: unknown): string {
  return t('listeningStats.units.plays', { count: normalizeInteger(value) })
}

function formatTrackTitle(track: ListeningStatsTrackRank): string {
  return track.title || track.album || track.trackId
}

function formatTrackArtist(track: ListeningStatsTrackRank): string {
  return track.artist || track.albumArtist || t('listeningStats.unknownArtist')
}

function formatAlbumName(album: ListeningStatsAlbumGroup | ListeningStatsTrackRank): string {
  return album.album || t('listeningStats.unknownAlbum')
}

function resolveHeatLevel(seconds: number, maxSeconds: number): number {
  if (seconds <= 0 || maxSeconds <= 0) {
    return 0
  }

  const ratio = seconds / maxSeconds

  if (ratio >= 0.75) {
    return 4
  }

  if (ratio >= 0.5) {
    return 3
  }

  if (ratio >= 0.25) {
    return 2
  }

  return 1
}

async function refreshStats() {
  const requestId = ++loadRequestId
  isLoading.value = true
  errorMessage.value = ''
  failedArtworkUrls.value = new Set()

  try {
    const nextStats = await props.loadStats({
      libraryId: props.libraryId,
      days: selectedDays.value,
      trackLimit: 24,
      albumLimit: 12,
      albumTrackLimit: 6,
      timezoneOffsetMinutes: new Date().getTimezoneOffset(),
    })

    if (requestId === loadRequestId) {
      stats.value = {
        ...createEmptyStats(selectedDays.value),
        ...nextStats,
        summary: nextStats.summary ?? {},
        daily: Array.isArray(nextStats.daily) ? nextStats.daily : [],
        topTracks: Array.isArray(nextStats.topTracks) ? nextStats.topTracks : [],
        albumGroups: Array.isArray(nextStats.albumGroups) ? nextStats.albumGroups : [],
      }
    }
  } catch (error) {
    if (requestId === loadRequestId) {
      errorMessage.value = error instanceof Error ? error.message : String(error ?? '')
      stats.value = createEmptyStats(selectedDays.value)
    }
  } finally {
    if (requestId === loadRequestId) {
      isLoading.value = false
    }
  }
}

function selectTrack(track: ListeningStatsTrackRank) {
  if (track.trackId) {
    emit('select-track', track.trackId)
  }
}
</script>

<template>
  <Teleport to="body">
    <Transition name="listening-stats">
      <div v-if="isOpen" class="listening-stats-backdrop" @click.self="emit('close')">
        <section class="listening-stats-window" role="dialog" aria-modal="true" :aria-label="t('listeningStats.title')">
          <header class="listening-stats-header">
            <div class="listening-stats-title-wrap">
              <p class="listening-stats-eyebrow">
                <Headphones aria-hidden="true" />
                <span>{{ t('listeningStats.eyebrow') }}</span>
              </p>
              <h2>{{ t('listeningStats.title') }}</h2>
              <span class="listening-stats-subtitle">{{ subtitle }}</span>
            </div>
            <div class="listening-stats-header-actions">
              <div class="listening-stats-range" role="tablist" :aria-label="t('listeningStats.title')">
                <button
                  v-for="option in rangeOptions"
                  :key="option.key"
                  type="button"
                  :class="{ 'is-active': selectedDays === option.value }"
                  :aria-pressed="selectedDays === option.value"
                  @click="selectedDays = option.value"
                >
                  {{ option.label }}
                </button>
              </div>
              <button
                type="button"
                class="listening-stats-icon-button"
                :aria-label="t('listeningStats.close')"
                :title="t('listeningStats.close')"
                @click="emit('close')"
              >
                <X aria-hidden="true" />
              </button>
            </div>
          </header>

          <div v-if="errorMessage" class="listening-stats-state is-error">
            <strong>{{ t('listeningStats.errorTitle') }}</strong>
            <span>{{ errorMessage }}</span>
          </div>

          <div v-else-if="isLoading && !hasStats" class="listening-stats-state is-loading">
            <RefreshCw class="is-spinning" aria-hidden="true" />
            <span>{{ t('listeningStats.loading') }}</span>
          </div>

          <div v-else class="listening-stats-body">
            <section class="listening-stats-hero" :aria-label="t('listeningStats.metrics.totalTime')">
              <span class="listening-stats-hero-label">
                <Clock aria-hidden="true" />
                {{ t('listeningStats.metrics.totalTime') }}
              </span>
              <strong class="listening-stats-hero-value">{{ formatDuration(summary.totalSeconds) }}</strong>
              <div class="listening-stats-hero-meta">
                <span v-for="metric in heroMetrics" :key="metric.key" class="listening-stats-hero-meta-item">
                  <component :is="metric.icon" class="listening-stats-hero-meta-icon" aria-hidden="true" />
                  <b>{{ metric.value }}</b>
                  <small>{{ metric.label }}</small>
                </span>
              </div>
            </section>

            <div v-if="!hasStats" class="listening-stats-state is-empty">
              <BarChart3 aria-hidden="true" />
              <strong>{{ t('listeningStats.emptyTitle') }}</strong>
              <span>{{ t('listeningStats.emptyCopy') }}</span>
            </div>

            <Transition v-else name="stats-fade" mode="out-in">
              <div :key="selectedDays" class="listening-stats-content">
                <section v-if="hasHeatmap" class="listening-stats-card is-heatmap">
                  <div class="listening-stats-section-head">
                    <h3>{{ t('listeningStats.sections.heatmap') }}</h3>
                    <span v-if="peakDayDateLabel" class="listening-stats-section-peak">
                      <TrendingUp aria-hidden="true" />
                      {{ t('listeningStats.peakDayValue', { date: peakDayDateLabel, duration: peakDayDurationLabel }) }}
                    </span>
                  </div>
                  <div class="listening-stats-heatmap-wrap">
                    <div class="listening-stats-heatmap-scroll">
                      <div class="listening-stats-heatmap-inner" :style="heatmapStyle">
                        <div class="listening-stats-months">
                          <span v-for="slot in heatmapMonthSlots" :key="slot.key">{{ slot.label }}</span>
                        </div>
                        <div
                          class="listening-stats-heatmap"
                          role="img"
                          :aria-label="t('listeningStats.sections.heatmap')"
                        >
                          <span
                            v-for="cell in heatmapCells"
                            :key="cell.key"
                            class="listening-stats-heat-cell"
                            :class="[
                              `is-level-${cell.level}`,
                              { 'is-empty': cell.empty, 'is-peak': !cell.empty && cell.date === peakCellDate },
                            ]"
                            :title="cell.title"
                          ></span>
                        </div>
                      </div>
                    </div>
                    <div class="listening-stats-legend" aria-hidden="true">
                      <span
                        v-for="level in heatLegendLevels"
                        :key="`legend-${level}`"
                        class="listening-stats-legend-cell"
                        :class="`is-level-${level}`"
                      ></span>
                    </div>
                  </div>
                </section>

                <div class="listening-stats-rank-grid">
                  <section class="listening-stats-card is-list">
                    <div class="listening-stats-section-head">
                      <h3>{{ t('listeningStats.sections.tracks') }}</h3>
                      <span class="listening-stats-section-count">{{ topTracks.length }}</span>
                    </div>
                    <div class="listening-stats-track-list">
                      <button
                        v-for="(track, index) in topTracks"
                        :key="track.trackId"
                        type="button"
                        class="listening-stats-track-row"
                        :class="{ 'is-top': index < 3 }"
                        @click="selectTrack(track)"
                      >
                        <span class="listening-stats-rank" :class="`is-rank-${Math.min(index + 1, 4)}`">
                          {{ index + 1 }}
                        </span>
                        <span class="listening-stats-track-art">
                          <img
                            v-if="track.artworkUrl"
                            :src="track.artworkUrl"
                            alt=""
                            loading="lazy"
                            decoding="async"
                            draggable="false"
                            @error="markArtworkFailed(track.artworkUrl)"
                          />
                          <span v-else class="listening-stats-art-placeholder">
                            <Music2 aria-hidden="true" />
                          </span>
                        </span>
                        <span class="listening-stats-track-main">
                          <strong>{{ formatTrackTitle(track) }}</strong>
                          <span>{{ formatTrackArtist(track) }} · {{ formatAlbumName(track) }}</span>
                        </span>
                        <span class="listening-stats-track-meta">
                          <strong>{{ formatDuration(track.listenSeconds) }}</strong>
                          <span>{{ formatPlayCount(track.playCount) }}</span>
                        </span>
                      </button>
                    </div>
                  </section>

                  <section class="listening-stats-card is-list">
                    <div class="listening-stats-section-head">
                      <h3>{{ t('listeningStats.sections.albums') }}</h3>
                      <span class="listening-stats-section-count">{{ albumGroups.length }}</span>
                    </div>
                    <div class="listening-stats-album-grid">
                      <article v-for="album in albumGroups" :key="album.key" class="listening-stats-album">
                        <header class="listening-stats-album-head">
                          <span class="listening-stats-album-art">
                            <img
                              v-if="album.artworkUrl"
                              :src="album.artworkUrl"
                              alt=""
                              loading="lazy"
                              decoding="async"
                              draggable="false"
                              @error="markArtworkFailed(album.artworkUrl)"
                            />
                            <span v-else class="listening-stats-art-placeholder">
                              <Disc3 aria-hidden="true" />
                            </span>
                          </span>
                          <div class="listening-stats-album-title">
                            <strong>{{ formatAlbumName(album) }}</strong>
                            <small>{{ album.albumArtist || t('listeningStats.unknownArtist') }}</small>
                          </div>
                          <div class="listening-stats-album-meta">
                            <strong>{{ formatDuration(album.listenSeconds) }}</strong>
                            <em>{{ albumTrackCountLabel(album) }}</em>
                          </div>
                        </header>
                        <div class="listening-stats-album-tracks">
                          <button
                            v-for="track in album.tracks"
                            :key="track.trackId"
                            type="button"
                            class="listening-stats-album-track"
                            @click="selectTrack(track)"
                          >
                            <span class="listening-stats-album-track-title">{{ formatTrackTitle(track) }}</span>
                            <span class="listening-stats-album-track-plays">{{ formatPlayCount(track.playCount) }}</span>
                            <strong class="listening-stats-album-track-duration">{{ formatDuration(track.listenSeconds) }}</strong>
                          </button>
                        </div>
                      </article>
                    </div>
                  </section>
                </div>
              </div>
            </Transition>

            <p v-if="generatedAtLabel" class="listening-stats-footer">{{ generatedAtLabel }}</p>
          </div>
        </section>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.listening-stats-backdrop {
  position: fixed;
  inset: 0;
  z-index: 140;
  display: grid;
  place-items: center;
  padding: 24px;
  background: var(--dialog-backdrop, var(--of-dialog-overlay, rgba(15, 23, 42, 0.5)));
  backdrop-filter: var(--dialog-backdrop-filter, blur(8px) saturate(0.9));
  -webkit-backdrop-filter: var(--dialog-backdrop-filter, blur(8px) saturate(0.9));
}

.listening-stats-window {
  --stats-bg: var(--surface-modal-soft, var(--of-bg-soft, #eef1f6));
  --stats-panel: var(--of-card-bg, var(--surface-modal, var(--of-surface, #ffffff)));
  --stats-panel-soft: var(--of-surface-sunken, var(--surface-soft, #f3f6fb));
  --stats-panel-muted: var(--of-surface-variant, var(--surface-soft-active, #e6ecf4));
  --stats-line: var(--of-divider, var(--line-soft, rgba(60, 72, 94, 0.12)));
  --stats-line-strong: var(--of-border, var(--line, rgba(60, 72, 94, 0.22)));
  --stats-ink: var(--of-ink, var(--ink, #161b26));
  --stats-muted: var(--of-ink-muted, var(--ink-muted, #5b6678));
  --stats-subtle: var(--of-ink-subtle, var(--ink-subtle, #8a94a6));
  --stats-accent: var(--of-player-accent, var(--of-playing, var(--of-brand, var(--primary, #0ea5e9))));
  --stats-accent-ink: var(--of-playing, var(--of-brand, var(--primary, #0ea5e9)));
  --stats-accent-soft: var(--of-brand-border, color-mix(in srgb, var(--stats-accent) 16%, transparent));
  --stats-accent-softer: var(--of-brand-soft, color-mix(in srgb, var(--stats-accent) 10%, transparent));
  --stats-art-bg: var(--of-surface-sunken, var(--surface-soft, #e9eef6));
  --stats-art-border: var(--of-divider, var(--line-soft, rgba(60, 72, 94, 0.14)));
  --stats-heat-base: color-mix(in srgb, var(--stats-muted) 22%, var(--stats-panel));
  --stats-heat-1: color-mix(in srgb, var(--stats-accent) 28%, var(--stats-heat-base));
  --stats-heat-2: color-mix(in srgb, var(--stats-accent) 52%, var(--stats-heat-base));
  --stats-heat-3: color-mix(in srgb, var(--stats-accent) 76%, var(--stats-heat-base));
  --stats-heat-4: var(--stats-accent);
  --stats-shadow: var(--of-dialog-shadow, var(--shadow-lg, 0 24px 64px -28px rgba(15, 23, 42, 0.5)));
  --stats-radius: 8px;
  --stats-heat-cell: 13px;
  --stats-heat-gap: 3px;
  display: grid;
  grid-template-rows: auto minmax(0, 1fr);
  width: min(1120px, calc(100vw - 48px));
  max-height: min(840px, calc(100vh - 48px));
  overflow: hidden;
  border: 1px solid var(--stats-line-strong);
  border-radius: var(--stats-radius);
  background: var(--stats-bg);
  color: var(--stats-ink);
  box-shadow: var(--stats-shadow);
  isolation: isolate;
}

:global(html[data-window-surface='native-glass'] .listening-stats-window) {
  --stats-bg: var(--surface-modal, var(--surface-solid, var(--of-surface-raised, #ffffff)));
  --stats-panel: var(--surface-elevated, var(--of-card-bg, var(--of-surface, #ffffff)));
  --stats-panel-soft: var(--surface-soft, var(--of-surface-sunken, #f3f6fb));
  background: var(--stats-bg);
  backdrop-filter: none;
  -webkit-backdrop-filter: none;
}

/* ---------- Header ---------- */
.listening-stats-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 18px;
  min-width: 0;
  padding: 18px 22px;
  border-bottom: 1px solid var(--stats-line);
  background: var(--stats-panel);
}

.listening-stats-title-wrap {
  display: grid;
  gap: 4px;
  min-width: 0;
}

.listening-stats-eyebrow {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin: 0;
  color: var(--stats-accent-ink);
  font-size: var(--font-size-xs, 0.72rem);
  font-weight: var(--font-weight-semibold, 600);
  letter-spacing: 0;
  text-transform: uppercase;
}

.listening-stats-eyebrow svg {
  width: 13px;
  height: 13px;
}

.listening-stats-title-wrap h2 {
  margin: 0;
  font-size: 1.4rem;
  line-height: 1.18;
  letter-spacing: 0;
}

.listening-stats-subtitle {
  color: var(--stats-muted);
  font-size: var(--font-size-sm, 0.84rem);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.listening-stats-header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: 0 0 auto;
}

.listening-stats-range {
  display: inline-flex;
  gap: 2px;
  padding: 3px;
  border: 1px solid var(--stats-line);
  border-radius: var(--stats-radius);
  background: var(--stats-panel-soft);
}

.listening-stats-range button {
  min-width: 60px;
  border: 1px solid transparent;
  border-radius: 6px;
  padding: 0.38rem 0.7rem;
  background: transparent;
  color: var(--stats-muted);
  font-size: var(--font-size-sm, 0.84rem);
  font-weight: var(--font-weight-semibold, 600);
  cursor: pointer;
  transition: background 120ms ease, color 120ms ease, border-color 120ms ease;
}

.listening-stats-range button:hover {
  color: var(--stats-ink);
}

.listening-stats-range button.is-active {
  border-color: var(--stats-line);
  background: var(--stats-panel);
  color: var(--stats-ink);
  box-shadow: 0 1px 2px rgba(15, 23, 42, 0.06);
}

.listening-stats-icon-button {
  display: grid;
  place-items: center;
  width: 34px;
  height: 34px;
  padding: 0;
  border: 1px solid var(--stats-line);
  border-radius: var(--stats-radius);
  background: var(--stats-panel-soft);
  color: var(--stats-ink);
  cursor: pointer;
  transition: background 120ms ease, border-color 120ms ease;
}

.listening-stats-icon-button:hover {
  border-color: var(--stats-line-strong);
  background: var(--stats-panel);
}

.listening-stats-icon-button:disabled {
  cursor: default;
  opacity: 0.6;
}

.listening-stats-icon-button svg {
  width: 16px;
  height: 16px;
}

/* ---------- Body + immersive scrollbar ---------- */
.listening-stats-body {
  min-height: 0;
  overflow: auto;
  display: grid;
  align-content: start;
  gap: 16px;
  padding: 18px 22px 18px;
  scrollbar-gutter: stable;
  scrollbar-width: thin;
  scrollbar-color: var(--scrollbar-thumb-idle, transparent) transparent;
  transition: scrollbar-color 160ms ease;
}

.listening-stats-body:hover,
.listening-stats-body:focus-within {
  scrollbar-color: var(--scrollbar-thumb-hover, rgba(0, 0, 0, 0.2)) transparent;
}

@supports selector(::-webkit-scrollbar) {
  .listening-stats-body::-webkit-scrollbar,
  .listening-stats-heatmap-scroll::-webkit-scrollbar {
    width: var(--scrollbar-hit-size, 10px);
    height: var(--scrollbar-hit-size, 10px);
  }

  .listening-stats-body::-webkit-scrollbar-track,
  .listening-stats-body::-webkit-scrollbar-track-piece,
  .listening-stats-heatmap-scroll::-webkit-scrollbar-track,
  .listening-stats-heatmap-scroll::-webkit-scrollbar-track-piece {
    background: transparent;
    border-radius: var(--radius-full, 9999px);
  }

  .listening-stats-body::-webkit-scrollbar-thumb,
  .listening-stats-heatmap-scroll::-webkit-scrollbar-thumb {
    border-radius: var(--radius-full, 9999px);
    background-clip: content-box;
    background-color: var(--scrollbar-thumb-idle, transparent);
    border: var(--scrollbar-thumb-idle-inset, 3px) solid transparent;
    transition: background-color var(--transition-fast, 160ms ease),
      border-width var(--transition-fast, 160ms ease);
  }

  .listening-stats-body:hover::-webkit-scrollbar-thumb,
  .listening-stats-body:focus-within::-webkit-scrollbar-thumb,
  .listening-stats-heatmap-scroll:hover::-webkit-scrollbar-thumb,
  .listening-stats-heatmap-scroll:focus-within::-webkit-scrollbar-thumb {
    background-color: var(--scrollbar-thumb-hover, rgba(0, 0, 0, 0.2));
    border-width: var(--scrollbar-thumb-active-inset, 2px);
  }

  .listening-stats-body::-webkit-scrollbar-thumb:hover,
  .listening-stats-body::-webkit-scrollbar-thumb:active,
  .listening-stats-heatmap-scroll::-webkit-scrollbar-thumb:hover,
  .listening-stats-heatmap-scroll::-webkit-scrollbar-thumb:active {
    background-color: var(--scrollbar-thumb-active, rgba(0, 0, 0, 0.35));
    border-width: 1px;
  }

  .listening-stats-body::-webkit-scrollbar-button,
  .listening-stats-body::-webkit-scrollbar-corner,
  .listening-stats-heatmap-scroll::-webkit-scrollbar-button,
  .listening-stats-heatmap-scroll::-webkit-scrollbar-corner {
    display: block;
    width: 0;
    height: 0;
    background: transparent;
  }
}

/* ---------- Hero (no card, quiet typography) ---------- */
.listening-stats-hero {
  display: grid;
  gap: 10px;
  min-width: 0;
  padding: 4px 2px 6px;
}

.listening-stats-hero-label {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--stats-muted);
  font-size: var(--font-size-sm, 0.84rem);
  font-weight: var(--font-weight-semibold, 600);
}

.listening-stats-hero-label svg {
  width: 14px;
  height: 14px;
  color: var(--stats-accent-ink);
}

.listening-stats-hero-value {
  min-width: 0;
  font-size: 2.25rem;
  line-height: 1.05;
  letter-spacing: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.listening-stats-hero-meta {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0;
  min-width: 0;
  margin-top: 2px;
}

.listening-stats-hero-meta-item {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  min-width: 0;
  padding: 2px 14px;
  border-left: 1px solid var(--stats-line);
}

.listening-stats-hero-meta-item:first-child {
  padding-left: 0;
  border-left: 0;
}

.listening-stats-hero-meta-icon {
  width: 15px;
  height: 15px;
  flex: 0 0 auto;
  color: var(--stats-accent-ink);
}

.listening-stats-hero-meta-item b {
  min-width: 0;
  font-size: var(--font-size-sm, 0.84rem);
  font-weight: var(--font-weight-semibold, 600);
  white-space: nowrap;
}

.listening-stats-hero-meta-item small {
  min-width: 0;
  overflow: hidden;
  color: var(--stats-muted);
  font-size: var(--font-size-xs, 0.72rem);
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ---------- Content fade on range switch ---------- */
.stats-fade-enter-active,
.stats-fade-leave-active {
  transition: opacity 180ms ease, transform 180ms ease;
}

.stats-fade-enter-from {
  opacity: 0;
  transform: translateY(4px);
}

.stats-fade-leave-to {
  opacity: 0;
  transform: translateY(-2px);
}

/* ---------- Cards ---------- */
.listening-stats-content {
  display: grid;
  gap: 14px;
  min-width: 0;
}

.listening-stats-card {
  min-width: 0;
  padding: 14px 16px 16px;
  border: 1px solid var(--stats-line);
  border-radius: var(--stats-radius);
  background: var(--stats-panel);
}

.listening-stats-section-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 12px;
  min-width: 0;
}

.listening-stats-section-head h3 {
  margin: 0;
  font-size: 0.95rem;
  letter-spacing: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.listening-stats-section-count {
  display: inline-flex;
  align-items: center;
  min-height: 22px;
  padding: 1px 9px;
  border: 1px solid var(--stats-line);
  border-radius: 999px;
  background: var(--stats-panel-soft);
  color: var(--stats-muted);
  font-size: var(--font-size-xs, 0.72rem);
  font-weight: var(--font-weight-semibold, 600);
}

.listening-stats-section-peak {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
  padding: 3px 10px;
  border: 1px solid var(--stats-accent-soft);
  border-radius: 999px;
  background: var(--stats-accent-softer);
  color: var(--stats-accent-ink);
  font-size: var(--font-size-xs, 0.72rem);
  font-weight: var(--font-weight-semibold, 600);
  overflow: hidden;
}

.listening-stats-section-peak svg {
  width: 12px;
  height: 12px;
  flex: 0 0 auto;
}

.listening-stats-section-peak {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ---------- Heatmap ---------- */
.listening-stats-heatmap-wrap {
  display: grid;
  gap: 10px;
}

.listening-stats-heatmap-scroll {
  overflow-x: auto;
  padding-bottom: 2px;
  scrollbar-width: thin;
  scrollbar-color: var(--scrollbar-thumb-idle, transparent) transparent;
}

.listening-stats-heatmap-scroll:hover,
.listening-stats-heatmap-scroll:focus-within {
  scrollbar-color: var(--scrollbar-thumb-hover, rgba(0, 0, 0, 0.2)) transparent;
}

.listening-stats-heatmap-inner {
  display: grid;
  gap: 6px;
  width: max-content;
  min-width: 100%;
  margin: 0 auto;
}

.listening-stats-months {
  display: grid;
  grid-template-columns: repeat(var(--stats-week-count), var(--stats-heat-cell));
  gap: var(--stats-heat-gap);
  color: var(--stats-muted);
  font-size: 0.68rem;
  line-height: 1;
}

.listening-stats-months span {
  min-width: 0;
  overflow: visible;
  white-space: nowrap;
  text-align: left;
}

.listening-stats-heatmap {
  display: grid;
  grid-auto-flow: column;
  grid-template-rows: repeat(7, var(--stats-heat-cell));
  grid-template-columns: repeat(var(--stats-week-count), var(--stats-heat-cell));
  gap: var(--stats-heat-gap);
}

.listening-stats-heat-cell {
  width: var(--stats-heat-cell);
  height: var(--stats-heat-cell);
  border-radius: 3px;
  background: var(--stats-heat-base);
  transition: transform 120ms ease, background-color 160ms ease;
}

.listening-stats-heat-cell.is-level-1 { background: var(--stats-heat-1); }
.listening-stats-heat-cell.is-level-2 { background: var(--stats-heat-2); }
.listening-stats-heat-cell.is-level-3 { background: var(--stats-heat-3); }
.listening-stats-heat-cell.is-level-4 { background: var(--stats-heat-4); }

.listening-stats-heat-cell.is-empty {
  visibility: hidden;
}

.listening-stats-heat-cell.is-peak {
  outline: 1.5px solid var(--stats-accent-ink);
  outline-offset: 1px;
}

.listening-stats-heat-cell:not(.is-empty):hover {
  transform: scale(1.18);
}

.listening-stats-legend {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  justify-self: end;
}

.listening-stats-legend-cell {
  width: 12px;
  height: 12px;
  border-radius: 3px;
  background: var(--stats-heat-base);
}

.listening-stats-legend-cell.is-level-1 { background: var(--stats-heat-1); }
.listening-stats-legend-cell.is-level-2 { background: var(--stats-heat-2); }
.listening-stats-legend-cell.is-level-3 { background: var(--stats-heat-3); }
.listening-stats-legend-cell.is-level-4 { background: var(--stats-heat-4); }

/* ---------- Rank grid ---------- */
.listening-stats-rank-grid {
  display: grid;
  grid-template-columns: minmax(340px, 0.95fr) minmax(0, 1.05fr);
  gap: 14px;
  align-items: start;
}

.listening-stats-track-list,
.listening-stats-album-grid {
  display: grid;
  gap: 2px;
}

.listening-stats-track-row {
  display: grid;
  grid-template-columns: 24px 40px minmax(0, 1fr) auto;
  align-items: center;
  gap: 12px;
  min-width: 0;
  width: 100%;
  padding: 7px 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: inherit;
  text-align: left;
  cursor: pointer;
  transition: background 120ms ease;
}

.listening-stats-track-row:hover {
  background: var(--stats-panel-soft);
}

.listening-stats-rank {
  display: grid;
  place-items: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  color: var(--stats-muted);
  font-size: var(--font-size-xs, 0.72rem);
  font-weight: var(--font-weight-semibold, 600);
}

.listening-stats-rank.is-rank-1 {
  color: color-mix(in srgb, var(--stats-accent) 72%, var(--stats-ink));
}

.listening-stats-rank.is-rank-2 {
  color: color-mix(in srgb, var(--stats-muted) 82%, var(--stats-ink));
}

.listening-stats-rank.is-rank-3 {
  color: color-mix(in srgb, var(--of-warm, var(--stats-accent)) 70%, var(--stats-ink));
}

.listening-stats-track-art,
.listening-stats-album-art {
  display: grid;
  place-items: center;
  position: relative;
  width: 40px;
  height: 40px;
  overflow: hidden;
  border-radius: 6px;
  border: 1px solid var(--stats-art-border);
  background: var(--stats-art-bg);
  color: var(--stats-subtle);
  flex: 0 0 auto;
  box-shadow: inset 0 1px 0 rgba(255, 255, 255, 0.08);
}

.listening-stats-album-art {
  width: 48px;
  height: 48px;
}

.listening-stats-track-art img,
.listening-stats-album-art img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  display: block;
  user-select: none;
  pointer-events: none;
}

.listening-stats-art-placeholder {
  display: grid;
  place-items: center;
  width: 100%;
  height: 100%;
  color: var(--stats-subtle);
}

.listening-stats-art-placeholder svg {
  width: 18px;
  height: 18px;
}

.listening-stats-album-art .listening-stats-art-placeholder svg {
  width: 20px;
  height: 20px;
}

.listening-stats-track-main {
  display: grid;
  gap: 2px;
  min-width: 0;
}

.listening-stats-track-main strong,
.listening-stats-track-main span {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.listening-stats-track-main strong {
  font-size: var(--font-size-sm, 0.84rem);
  font-weight: var(--font-weight-semibold, 600);
}

.listening-stats-track-main span {
  color: var(--stats-muted);
  font-size: var(--font-size-xs, 0.72rem);
}

.listening-stats-track-meta {
  display: grid;
  justify-items: end;
  gap: 1px;
  min-width: 0;
}

.listening-stats-track-meta strong {
  font-size: var(--font-size-sm, 0.84rem);
}

.listening-stats-track-meta span {
  color: var(--stats-subtle);
  font-size: 0.66rem;
  white-space: nowrap;
}

/* ---------- Albums ---------- */
.listening-stats-album-grid {
  gap: 10px;
  grid-template-columns: 1fr;
}

.listening-stats-album {
  display: grid;
  gap: 8px;
  min-width: 0;
  padding: 12px 14px 10px;
  border: 1px solid var(--stats-line);
  border-radius: var(--stats-radius);
  background: var(--stats-panel-soft);
}

.listening-stats-album-head {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  align-items: center;
  gap: 12px;
  min-width: 0;
}

.listening-stats-album-title {
  display: grid;
  gap: 1px;
  min-width: 0;
}

.listening-stats-album-title strong,
.listening-stats-album-title small {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.listening-stats-album-title strong {
  font-size: var(--font-size-sm, 0.84rem);
  font-weight: var(--font-weight-semibold, 600);
}

.listening-stats-album-title small {
  color: var(--stats-muted);
  font-size: var(--font-size-xs, 0.72rem);
}

.listening-stats-album-meta {
  display: grid;
  justify-items: end;
  gap: 1px;
  min-width: 0;
}

.listening-stats-album-meta strong {
  font-size: var(--font-size-sm, 0.84rem);
  white-space: nowrap;
}

.listening-stats-album-meta em {
  color: var(--stats-subtle);
  font-size: 0.66rem;
  font-style: normal;
  white-space: nowrap;
}

.listening-stats-album-tracks {
  display: grid;
  gap: 1px;
}

.listening-stats-album-track {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto auto;
  align-items: center;
  gap: 10px;
  min-width: 0;
  width: 100%;
  padding: 6px 8px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: inherit;
  text-align: left;
  cursor: pointer;
  transition: background 120ms ease;
}

.listening-stats-album-track:hover {
  background: var(--stats-panel);
}

.listening-stats-album-track-title {
  min-width: 0;
  overflow: hidden;
  color: var(--stats-ink);
  font-size: var(--font-size-xs, 0.72rem);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.listening-stats-album-track-plays {
  min-width: 0;
  color: var(--stats-subtle);
  font-size: 0.66rem;
  white-space: nowrap;
}

.listening-stats-album-track-duration {
  min-width: 0;
  color: var(--stats-muted);
  font-size: var(--font-size-xs, 0.72rem);
  font-weight: var(--font-weight-semibold, 600);
  white-space: nowrap;
}

/* ---------- Footer ---------- */
.listening-stats-footer {
  margin: 0;
  padding: 0 2px;
  color: var(--stats-subtle);
  font-size: 0.68rem;
  text-align: right;
}

/* ---------- State (loading / error / empty) ---------- */
.listening-stats-state {
  display: grid;
  justify-items: center;
  gap: 8px;
  margin: 0;
  padding: 32px 24px;
  border: 1px solid var(--stats-line);
  border-radius: var(--stats-radius);
  background: var(--stats-panel);
  color: var(--stats-muted);
  text-align: center;
}

.listening-stats-state strong {
  color: var(--stats-ink);
  font-size: 1rem;
}

.listening-stats-state svg {
  width: 24px;
  height: 24px;
  color: var(--stats-subtle);
}

.listening-stats-state.is-error {
  border-color: var(--danger-border, rgba(220, 38, 38, 0.35));
  color: var(--danger-ink, #b91c1c);
}

.listening-stats-state.is-error strong {
  color: var(--danger-ink, #b91c1c);
}

.is-spinning {
  animation: listening-stats-spin 900ms linear infinite;
}

.listening-stats-enter-active,
.listening-stats-leave-active {
  transition: opacity var(--transition-fast, 160ms ease);
}

.listening-stats-enter-active .listening-stats-window,
.listening-stats-leave-active .listening-stats-window {
  transition: transform var(--transition-fast, 160ms ease);
}

.listening-stats-enter-from,
.listening-stats-leave-to {
  opacity: 0;
}

.listening-stats-enter-from .listening-stats-window,
.listening-stats-leave-to .listening-stats-window {
  transform: translateY(10px);
}

@keyframes listening-stats-spin {
  to {
    transform: rotate(360deg);
  }
}

/* ---------- Responsive ---------- */
@media (max-width: 980px) {
  .listening-stats-rank-grid {
    grid-template-columns: 1fr;
  }
}

@media (max-width: 720px) {
  .listening-stats-backdrop {
    padding: 12px;
  }

  .listening-stats-window {
    width: calc(100vw - 24px);
    max-height: calc(100vh - 24px);
    border-radius: var(--stats-radius);
  }

  .listening-stats-header {
    align-items: stretch;
    flex-direction: column;
    gap: 12px;
    padding: 16px 16px 14px;
  }

  .listening-stats-header-actions {
    align-items: stretch;
    flex-wrap: wrap;
    gap: 8px;
  }

  .listening-stats-range {
    flex: 1 1 100%;
  }

  .listening-stats-range button {
    flex: 1 1 0;
    min-width: 0;
  }

  .listening-stats-body {
    padding: 14px 16px 16px;
    gap: 14px;
  }

  .listening-stats-hero-value {
    font-size: 1.85rem;
  }

  .listening-stats-hero-meta-item {
    padding: 2px 10px;
  }

  .listening-stats-track-row {
    grid-template-columns: 22px 36px minmax(0, 1fr);
    gap: 10px;
  }

  .listening-stats-track-art {
    width: 36px;
    height: 36px;
  }

  .listening-stats-track-meta {
    grid-column: 3;
    justify-items: end;
    grid-auto-flow: column;
    grid-auto-columns: max-content;
    gap: 8px;
  }

  .listening-stats-track-meta span {
    font-size: 0.62rem;
  }

  .listening-stats-album-track {
    grid-template-columns: minmax(0, 1fr) auto;
    gap: 8px;
  }

  .listening-stats-album-track-plays {
    display: none;
  }
}

@media (max-width: 460px) {
  .listening-stats-hero-value {
    font-size: 1.65rem;
  }

  .listening-stats-hero-meta-item small {
    display: none;
  }
}

/* ---------- Reduced motion ---------- */
@media (prefers-reduced-motion: reduce) {
  .stats-fade-enter-active,
  .stats-fade-leave-active,
  .listening-stats-heat-cell,
  .listening-stats-enter-active,
  .listening-stats-leave-active,
  .listening-stats-enter-active .listening-stats-window,
  .listening-stats-leave-active .listening-stats-window {
    transition: none;
    animation: none;
  }

  .listening-stats-heat-cell:not(.is-empty):hover {
    transform: none;
  }
}
</style>
