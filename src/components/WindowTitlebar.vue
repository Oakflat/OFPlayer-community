<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { getCurrentWindow } from '@tauri-apps/api/window'
import { useI18n } from '../composables/useI18n'

const { t } = useI18n()
const emit = defineEmits<{
  'request-close': []
}>()
const appWindow = getCurrentWindow()
const isMaximized = ref(false)
let unlistenResized: (() => void) | null = null

const labels = computed(() => {
  return {
    minimize: t('window.minimize'),
    maximize: t('window.maximize'),
    restore: t('window.restore'),
    close: t('window.close'),
  }
})

function syncDocumentWindowState() {
  if (typeof document === 'undefined') {
    return
  }

  document.documentElement.dataset.windowMaximized = isMaximized.value ? 'true' : 'false'
}

async function syncWindowState() {
  isMaximized.value = await appWindow.isMaximized()
  syncDocumentWindowState()
}

async function handleMinimize() {
  await appWindow.minimize()
}

async function handleToggleMaximize() {
  await appWindow.toggleMaximize()
  await syncWindowState()
}

async function handleClose() {
  emit('request-close')
}

async function handleTitlebarDoubleClick() {
  await handleToggleMaximize()
}

onMounted(async () => {
  await syncWindowState()
  unlistenResized = await appWindow.onResized(() => {
    void syncWindowState()
  })
})

onBeforeUnmount(() => {
  if (typeof document !== 'undefined') {
    delete document.documentElement.dataset.windowMaximized
  }

  unlistenResized?.()
})
</script>

<template>
  <header class="window-titlebar">
    <div
      class="window-titlebar__drag"
      data-tauri-drag-region
      @dblclick="handleTitlebarDoubleClick"
    >
      <div class="window-titlebar__brand" data-tauri-drag-region>
        <img src="/OFplayer.svg" alt="" class="window-titlebar__logo" data-tauri-drag-region />
        <span class="window-titlebar__name" data-tauri-drag-region>OFPlayer</span>
      </div>
    </div>

    <div class="window-titlebar__controls">
      <button
        type="button"
        class="window-titlebar__control"
        :aria-label="labels.minimize"
        @click="handleMinimize"
      >
        <svg viewBox="0 0 10 10" aria-hidden="true">
          <path d="M1.5 5h7" />
        </svg>
      </button>
      <button
        type="button"
        class="window-titlebar__control"
        :aria-label="isMaximized ? labels.restore : labels.maximize"
        @click="handleToggleMaximize"
      >
        <svg v-if="isMaximized" viewBox="0 0 10 10" aria-hidden="true">
          <path d="M3.25 1.5h5.25v5.25" />
          <path d="M1.5 3.25h5.25v5.25H1.5z" />
        </svg>
        <svg v-else viewBox="0 0 10 10" aria-hidden="true">
          <path d="M1.5 1.5h7v7h-7z" />
        </svg>
      </button>
      <button
        type="button"
        class="window-titlebar__control window-titlebar__control--close"
        :aria-label="labels.close"
        @click="handleClose"
      >
        <svg viewBox="0 0 10 10" aria-hidden="true">
          <path d="M2 2l6 6" />
          <path d="M8 2 2 8" />
        </svg>
      </button>
    </div>
  </header>
</template>
