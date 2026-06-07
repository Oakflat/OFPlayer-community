import { createApp } from 'vue'
import LyricCapsuleWindow from './components/LyricCapsuleWindow.vue'
import { elapsedMs, logLyricCapsuleInfo, nowMs } from './services/lyricCapsuleDiagnostics'

const scriptStartedAt = nowMs()
document.documentElement.dataset.appView = 'lyric-capsule'

void logLyricCapsuleInfo('capsule_entry_script_start', {
  timestampMs: Math.round(scriptStartedAt),
})

const mountStartedAt = nowMs()
createApp(LyricCapsuleWindow).mount('#app')

void logLyricCapsuleInfo('capsule_entry_mount_called', {
  mountCallMs: elapsedMs(mountStartedAt),
  elapsedSinceScriptMs: elapsedMs(scriptStartedAt),
})
