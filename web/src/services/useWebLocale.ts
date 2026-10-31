import { ref, onBeforeUnmount } from 'vue'
import { hostRequest, isHostBridgeAvailable, onHostEvent } from '../immersiveWeb/hostBridge.ts'
import { createWebLocaleState, resolveWebLocale, setActiveWebLocale, webText, type WebTextKey } from './webLocale.ts'

export function useWebLocale(titleKey: 'immersiveTitle' | 'capsuleTitle') {
  let active = true
  const state = createWebLocaleState(resolveWebLocale(globalThis.navigator?.language) ?? 'en')
  const locale = ref(state.locale)
  function syncDocument() {
    locale.value = state.locale
    setActiveWebLocale(state.locale)
    document.documentElement.lang = state.locale
    document.title = webText(state.locale, titleKey)
  }
  syncDocument()
  // Subscribe before the boot request so ready-time and live events cannot be lost.
  const detach = onHostEvent(event => {
    if (event.event !== 'web.locale' || !event.data || typeof event.data !== 'object') return
    if (state.apply((event.data as { language?: unknown }).language)) syncDocument()
  })
  if (isHostBridgeAvailable()) {
    const revision = state.revision
    void hostRequest<{ language: string }>('web_locale_get').then(reply => {
      if (active && state.apply(reply.language, revision)) syncDocument()
    }).catch(() => {})
  }
  onBeforeUnmount(() => {
    active = false
    detach()
  })
  return { locale, t: (key: WebTextKey, values?: Record<string, string>) => webText(locale.value, key, values) }
}
