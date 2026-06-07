<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import {
  Check,
  ChevronLeft,
  ChevronRight,
  FileText,
  HardDriveDownload,
  Library,
  ListMusic,
  Music2,
  Plus,
  X,
} from 'lucide-vue-next'
import { useI18n } from '../composables/useI18n'

const ONBOARDING_KEY = 'ofplayer:onboarding:seen:v1'
const TOTAL_STEPS = 6

const emit = defineEmits<{
  close: []
}>()

const { t } = useI18n()
const step = ref(0)
const isFirst = computed(() => step.value === 0)
const isLast = computed(() => step.value === TOTAL_STEPS - 1)
const pulse = ref(false)
let pulseTimer: number | null = null

function persistSeen() {
  try {
    window.localStorage.setItem(ONBOARDING_KEY, '1')
  } catch {
    // Storage can be unavailable in restricted WebView modes.
  }
}

function closeGuide() {
  persistSeen()
  emit('close')
}

function previousStep() {
  if (!isFirst.value) {
    step.value -= 1
  }
}

function nextStep() {
  if (!isLast.value) {
    step.value += 1
  }
}

function setStep(index: number) {
  step.value = Math.max(0, Math.min(TOTAL_STEPS - 1, index))
}

function handleKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    closeGuide()
  }

  if (event.key === 'ArrowLeft') {
    previousStep()
  }

  if (event.key === 'ArrowRight') {
    nextStep()
  }
}

onMounted(() => {
  pulseTimer = window.setInterval(() => {
    pulse.value = !pulse.value
  }, 1400)
  document.addEventListener('keydown', handleKeydown)
})

onBeforeUnmount(() => {
  if (pulseTimer !== null) {
    window.clearInterval(pulseTimer)
    pulseTimer = null
  }
  document.removeEventListener('keydown', handleKeydown)
})
</script>

<template>
  <Teleport to="body">
    <div
      class="og-backdrop"
      role="dialog"
      aria-modal="true"
      :aria-label="t('onboarding.ariaLabel')"
      @click.self="closeGuide"
    >
      <section class="og-card">
        <header class="og-header">
          <div class="og-brand">
            <span class="og-brand-mark">OF</span>
            <span>{{ t('onboarding.title') }}</span>
          </div>

          <button class="og-icon-button" type="button" :aria-label="t('onboarding.close')" @click="closeGuide">
            <X aria-hidden="true" />
          </button>
        </header>

        <div class="og-dots" role="tablist" :aria-label="t('onboarding.stepsLabel')">
          <button
            v-for="index in TOTAL_STEPS"
            :key="index"
            class="og-dot"
            :class="{ 'is-active': step === index - 1 }"
            type="button"
            role="tab"
            :aria-selected="step === index - 1"
            :aria-label="t('onboarding.stepLabel', { index })"
            @click="setStep(index - 1)"
          ></button>
        </div>

        <Transition name="og-slide" mode="out-in">
          <div v-if="step === 0" key="library" class="og-body">
            <div class="og-visual">
              <div class="og-mini-sidebar">
                <div class="og-mini-section">
                  <span>{{ t('sidebar.librarySection') }}</span>
                  <span class="og-mini-add" :class="{ 'is-pulsing': pulse }"><Plus aria-hidden="true" /></span>
                </div>
                <div class="og-mini-row is-active">
                  <Library aria-hidden="true" />
                  <span>{{ t('onboarding.demoLocalLib') }}</span>
                  <em>54</em>
                </div>
                <div class="og-mini-row">
                  <Library aria-hidden="true" />
                  <span>{{ t('onboarding.demoArchiveLib') }}</span>
                  <em>18</em>
                </div>
              </div>
            </div>

            <div class="og-copy">
              <p class="og-eyebrow">{{ t('onboarding.step0Eyebrow') }}</p>
              <h2>{{ t('onboarding.step0Title') }}</h2>
              <p>{{ t('onboarding.step0Body') }}</p>
              <ul>
                <li>{{ t('onboarding.step0Bullet0') }}</li>
                <li>{{ t('onboarding.step0Bullet1') }}</li>
              </ul>
            </div>
          </div>

          <div v-else-if="step === 1" key="storage" class="og-body">
            <div class="og-visual">
              <div class="og-storage-demo">
                <HardDriveDownload aria-hidden="true" />
                <strong>{{ t('onboarding.storageDemoRoot') }}</strong>
                <span>OFPlayer / Managed Audio</span>
                <div class="og-storage-flow">
                  <i></i>
                  <i></i>
                  <i></i>
                </div>
              </div>
            </div>

            <div class="og-copy">
              <p class="og-eyebrow">{{ t('onboarding.stepStorageEyebrow') }}</p>
              <h2>{{ t('onboarding.stepStorageTitle') }}</h2>
              <p>{{ t('onboarding.stepStorageBody') }}</p>
              <ul>
                <li>{{ t('onboarding.stepStorageBullet0') }}</li>
                <li>{{ t('onboarding.stepStorageBullet1') }}</li>
              </ul>
            </div>
          </div>

          <div v-else-if="step === 2" key="import" class="og-body">
            <div class="og-visual">
              <div class="og-import-demo">
                <div class="og-format-cloud">
                  <span v-for="format in ['FLAC', 'MP3', 'WAV', 'AIFF', 'M4A', 'OGG']" :key="format">
                    {{ format }}
                  </span>
                </div>
                <div class="og-import-target">
                  <HardDriveDownload aria-hidden="true" />
                  <span>{{ t('onboarding.importDemoTarget') }}</span>
                </div>
              </div>
            </div>

            <div class="og-copy">
              <p class="og-eyebrow">{{ t('onboarding.stepImportEyebrow') }}</p>
              <h2>{{ t('onboarding.stepImportTitle') }}</h2>
              <p>{{ t('onboarding.stepImportBody') }}</p>
              <ul>
                <li>{{ t('onboarding.stepImportBullet0') }}</li>
                <li>{{ t('onboarding.stepImportBullet1') }}</li>
              </ul>
            </div>
          </div>

          <div v-else-if="step === 3" key="playlist" class="og-body">
            <div class="og-visual">
              <div class="og-mini-sidebar og-mini-sidebar--wide">
                <div class="og-mini-section">
                  <span>{{ t('sidebar.librarySection') }}</span>
                </div>
                <div class="og-mini-row is-active">
                  <Library aria-hidden="true" />
                  <span>{{ t('onboarding.demoLocalLib') }}</span>
                </div>
                <div class="og-mini-divider"></div>
                <div class="og-mini-section">
                  <span>{{ t('sidebar.playlistSection') }}</span>
                </div>
                <div class="og-mini-row og-mini-row--nested">
                  <ListMusic aria-hidden="true" />
                  <span>{{ t('onboarding.demoPlaylistDefault') }}</span>
                  <b>{{ t('onboarding.defaultBadge') }}</b>
                </div>
                <div class="og-mini-row og-mini-row--nested">
                  <ListMusic aria-hidden="true" />
                  <span>{{ t('onboarding.demoPlaylistFav') }}</span>
                </div>
              </div>
            </div>

            <div class="og-copy">
              <p class="og-eyebrow">{{ t('onboarding.stepPlaylistEyebrow') }}</p>
              <h2>{{ t('onboarding.stepPlaylistTitle') }}</h2>
              <p>{{ t('onboarding.stepPlaylistBody') }}</p>
              <ul>
                <li>{{ t('onboarding.stepPlaylistBullet0') }}</li>
                <li>{{ t('onboarding.stepPlaylistBullet1') }}</li>
              </ul>
            </div>
          </div>

          <div v-else-if="step === 4" key="lyrics" class="og-body">
            <div class="og-visual">
              <div class="og-lyrics-demo">
                <div class="og-lrc-file">
                  <FileText aria-hidden="true" />
                  <span>track.lrc</span>
                </div>
                <div class="og-lrc-preview">
                  <span>[00:12.34] {{ t('onboarding.lrcLine0') }}</span>
                  <span>[00:16.80] {{ t('onboarding.lrcLine1') }}</span>
                  <span>[00:21.00] {{ t('onboarding.lrcLine2') }}</span>
                </div>
              </div>
            </div>

            <div class="og-copy">
              <p class="og-eyebrow">{{ t('onboarding.stepLyricsEyebrow') }}</p>
              <h2>{{ t('onboarding.stepLyricsTitle') }}</h2>
              <p>{{ t('onboarding.stepLyricsBody') }}</p>
              <ul>
                <li>{{ t('onboarding.stepLyricsBullet0') }}</li>
                <li>{{ t('onboarding.stepLyricsBullet1') }}</li>
              </ul>
            </div>
          </div>

          <div v-else key="capsule" class="og-body">
            <div class="og-visual">
              <div class="og-capsule-demo">
                <div class="og-capsule-pill">
                  <Music2 aria-hidden="true" />
                  <span>{{ t('onboarding.capsuleLine') }}</span>
                </div>
                <div class="og-capsule-controls">
                  <span></span>
                  <span></span>
                  <span></span>
                </div>
              </div>
            </div>

            <div class="og-copy">
              <p class="og-eyebrow">{{ t('onboarding.stepCapsuleEyebrow') }}</p>
              <h2>{{ t('onboarding.stepCapsuleTitle') }}</h2>
              <p>{{ t('onboarding.stepCapsuleBody') }}</p>
              <ul>
                <li>{{ t('onboarding.stepCapsuleBullet0') }}</li>
                <li>{{ t('onboarding.stepCapsuleBullet1') }}</li>
              </ul>
            </div>
          </div>
        </Transition>

        <footer class="og-footer">
          <button class="og-button og-button--ghost" type="button" :disabled="isFirst" @click="previousStep">
            <ChevronLeft aria-hidden="true" />
            {{ t('onboarding.prev') }}
          </button>

          <button v-if="!isLast" class="og-button og-button--primary" type="button" @click="nextStep">
            {{ t('onboarding.next') }}
            <ChevronRight aria-hidden="true" />
          </button>
          <button v-else class="og-button og-button--done" type="button" @click="closeGuide">
            <Check aria-hidden="true" />
            {{ t('onboarding.done') }}
          </button>
        </footer>
      </section>
    </div>
  </Teleport>
</template>

<style scoped>
.og-backdrop {
  position: fixed;
  inset: 0;
  z-index: 9000;
  display: grid;
  place-items: center;
  padding: 1.25rem;
  background: rgba(15, 18, 25, 0.36);
  backdrop-filter: var(--dialog-backdrop-filter);
  -webkit-backdrop-filter: var(--dialog-backdrop-filter);
  animation: fade-in var(--duration-md) var(--ease-standard) both;
}

.og-card {
  display: grid;
  width: min(700px, 100%);
  overflow: hidden;
  border: 1px solid var(--line-soft);
  border-radius: var(--radius-xl);
  background: var(--surface-modal);
  box-shadow: 0 24px 72px rgba(17, 24, 39, 0.2);
  animation: scale-in-soft var(--duration-3xl) var(--ease-emphasized-decelerate) both;
  animation-delay: 70ms;
}

.og-header,
.og-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
}

.og-header {
  padding: 1rem 1.15rem 0;
  animation: slide-up-fade var(--duration-lg) var(--ease-emphasized-decelerate) both;
  animation-delay: 150ms;
}

.og-brand {
  display: inline-flex;
  align-items: center;
  gap: 0.625rem;
  font-size: var(--font-size-sm);
  font-weight: var(--font-weight-semibold);
  color: var(--ink-muted);
}

.og-brand-mark {
  display: inline-grid;
  place-items: center;
  width: 30px;
  height: 30px;
  border-radius: 10px;
  background: var(--primary-container);
  color: var(--ink);
  font-size: 0.72rem;
  letter-spacing: 0.08em;
}

.og-icon-button {
  display: inline-grid;
  place-items: center;
  width: 32px;
  height: 32px;
  padding: 0;
  border: 1px solid var(--line-soft);
  border-radius: 10px;
  background: var(--surface-overlay);
  color: var(--ink-muted);
}

.og-icon-button svg,
.og-button svg {
  width: 16px;
  height: 16px;
}

.og-dots {
  display: flex;
  justify-content: center;
  gap: 0.375rem;
  padding: 0.9rem 1rem 0;
  animation: fade-in var(--duration-lg) var(--ease-standard) both;
  animation-delay: 190ms;
}

.og-dot {
  width: 20px;
  height: 5px;
  padding: 0;
  border: 0;
  border-radius: var(--radius-full);
  background: var(--state-layer-pressed);
  transition: width var(--transition-normal), background var(--transition-normal);
}

.og-dot.is-active {
  width: 34px;
  background: var(--primary);
}

.og-body {
  display: grid;
  grid-template-columns: minmax(180px, 220px) minmax(0, 1fr);
  gap: 1.5rem;
  min-height: 310px;
  padding: 1.5rem 1.6rem 1rem;
}

.og-visual {
  display: grid;
  place-items: center;
}

.og-copy {
  display: flex;
  flex-direction: column;
  justify-content: center;
  gap: 0.65rem;
  min-width: 0;
}

.og-eyebrow {
  margin: 0;
  font-size: 0.72rem;
  font-weight: var(--font-weight-bold);
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--ink-subtle);
}

.og-copy h2 {
  margin: 0;
  font-size: clamp(1.28rem, 2vw, 1.7rem);
  line-height: 1.18;
  letter-spacing: 0;
  color: var(--ink);
}

.og-copy p {
  margin: 0;
  font-size: var(--font-size-sm);
  line-height: 1.65;
  color: var(--ink-soft);
}

.og-copy ul {
  display: grid;
  gap: 0.35rem;
  margin: 0.1rem 0 0;
  padding-left: 1.1rem;
  color: var(--ink-muted);
}

.og-copy li {
  font-size: var(--font-size-xs);
  line-height: 1.55;
}

.og-mini-sidebar,
.og-storage-demo,
.og-import-demo,
.og-lyrics-demo,
.og-capsule-demo {
  width: 190px;
  border: 1px solid var(--line-soft);
  border-radius: var(--radius-lg);
  background: var(--surface-overlay);
  box-shadow: var(--shadow-sm);
}

.og-mini-sidebar {
  display: grid;
  gap: 0.35rem;
  padding: 0.75rem;
}

.og-mini-sidebar--wide {
  width: 205px;
}

.og-mini-section,
.og-mini-row {
  display: flex;
  align-items: center;
  gap: 0.45rem;
}

.og-mini-section {
  justify-content: space-between;
  padding: 0 0.15rem;
  font-size: 0.66rem;
  font-weight: var(--font-weight-bold);
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--ink-subtle);
}

.og-mini-add {
  display: inline-grid;
  place-items: center;
  width: 22px;
  height: 22px;
  border-radius: 8px;
  background: var(--surface-soft);
  color: var(--ink-muted);
  transition: transform var(--transition-bounce), background var(--transition-fast), color var(--transition-fast);
}

.og-mini-add.is-pulsing {
  transform: scale(1.08);
  background: var(--primary-container);
  color: var(--ink);
}

.og-mini-add svg,
.og-mini-row svg {
  width: 14px;
  height: 14px;
}

.og-mini-row {
  min-height: 34px;
  padding: 0 0.55rem;
  border-radius: 10px;
  color: var(--ink-muted);
}

.og-mini-row.is-active {
  background: var(--primary-container);
  color: var(--ink);
}

.og-mini-row span {
  min-width: 0;
  flex: 1 1 auto;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: var(--font-size-xs);
  font-weight: var(--font-weight-semibold);
}

.og-mini-row em,
.og-mini-row b {
  font-style: normal;
  font-size: 0.64rem;
  font-weight: var(--font-weight-bold);
  color: var(--ink-subtle);
}

.og-mini-row--nested {
  padding-left: 1.1rem;
}

.og-mini-divider {
  height: 1px;
  margin: 0.2rem 0;
  background: var(--line-soft);
}

.og-storage-demo,
.og-import-demo,
.og-lyrics-demo,
.og-capsule-demo {
  display: grid;
  justify-items: center;
  gap: 0.75rem;
  padding: 1rem;
  text-align: center;
}

.og-storage-demo > svg,
.og-import-target svg,
.og-lrc-file svg {
  width: 28px;
  height: 28px;
  color: var(--primary);
}

.og-storage-demo strong,
.og-storage-demo span,
.og-import-target span,
.og-lrc-file span {
  max-width: 100%;
  overflow-wrap: anywhere;
}

.og-storage-demo strong {
  font-size: var(--font-size-sm);
  color: var(--ink);
}

.og-storage-demo span {
  font-size: var(--font-size-xs);
  color: var(--ink-muted);
}

.og-storage-flow {
  display: flex;
  gap: 0.35rem;
}

.og-storage-flow i,
.og-capsule-controls span {
  width: 28px;
  height: 4px;
  border-radius: var(--radius-full);
  background: var(--primary);
  opacity: 0.35;
}

.og-format-cloud {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 0.35rem;
}

.og-format-cloud span {
  padding: 0.25rem 0.45rem;
  border-radius: 8px;
  background: var(--surface-soft);
  color: var(--ink-muted);
  font-size: 0.68rem;
  font-weight: var(--font-weight-bold);
}

.og-import-target,
.og-lrc-file {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  max-width: 100%;
}

.og-lrc-preview {
  display: grid;
  gap: 0.3rem;
  width: 100%;
  padding: 0.75rem;
  border-radius: 10px;
  background: var(--surface-soft);
  text-align: left;
}

.og-lrc-preview span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--font-mono, 'Consolas', monospace);
  font-size: 0.68rem;
  color: var(--ink-muted);
}

.og-capsule-demo {
  gap: 1rem;
  background:
    radial-gradient(circle at 30% 0%, rgba(255, 255, 255, 0.2), transparent 42%),
    var(--surface-overlay);
}

.og-capsule-pill {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  max-width: 100%;
  padding: 0.6rem 0.75rem;
  border: 1px solid var(--line-soft);
  border-radius: var(--radius-full);
  background: var(--surface-elevated);
  color: var(--ink);
  box-shadow: var(--shadow-sm);
}

.og-capsule-pill svg {
  width: 16px;
  height: 16px;
  flex: 0 0 auto;
  color: var(--primary);
}

.og-capsule-pill span {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: var(--font-size-xs);
  font-weight: var(--font-weight-semibold);
}

.og-capsule-controls {
  display: flex;
  gap: 0.45rem;
}

.og-footer {
  padding: 1rem 1.6rem 1.25rem;
  border-top: 1px solid var(--line-soft);
  animation: slide-up-fade var(--duration-lg) var(--ease-emphasized-decelerate) both;
  animation-delay: 250ms;
}

.og-button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.45rem;
  min-height: 40px;
  padding: 0 0.95rem;
  border-radius: var(--radius-full);
  border: 1px solid var(--line);
  font-size: var(--font-size-sm);
  font-weight: var(--font-weight-semibold);
}

.og-button:disabled {
  cursor: not-allowed;
  opacity: 0.45;
}

.og-button--ghost {
  background: transparent;
  color: var(--ink-muted);
}

.og-button--primary,
.og-button--done {
  border-color: var(--primary);
  background: var(--primary);
  color: var(--on-primary);
}

.og-slide-enter-active,
.og-slide-leave-active {
  transition: opacity 180ms ease, transform 180ms ease;
}

.og-slide-enter-from {
  opacity: 0;
  transform: translateX(18px);
}

.og-slide-leave-to {
  opacity: 0;
  transform: translateX(-18px);
}

@media (max-width: 640px) {
  .og-card {
    max-height: calc(100vh - 2rem);
    overflow-y: auto;
  }

  .og-body {
    grid-template-columns: 1fr;
    min-height: 0;
  }

  .og-copy {
    justify-content: flex-start;
  }
}
</style>
