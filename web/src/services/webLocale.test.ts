import assert from 'node:assert/strict'
import test from 'node:test'
import { computed, createRenderer, h } from 'vue'
import { initHostBridge } from '../immersiveWeb/hostBridge.ts'
import { createLyricCapsuleSnapshot } from './lyricCapsuleBridge.ts'
import { createWebLocaleState, resolveWebLocale, WEB_LOCALES, WEB_TEXT, webText, type WebTextKey } from './webLocale.ts'
import { useWebLocale } from './useWebLocale.ts'

test('supported language families resolve from system variants and reject unsupported input', () => {
  for (const [input, expected] of Object.entries({ 'en-US': 'en', 'zh-Hans-CN': 'zh-CN', 'ja-JP': 'ja', 'fr_CA': 'fr', 'de-DE': 'de' }))
    assert.equal(resolveWebLocale(input), expected)
  for (const input of ['system', 'es-ES', '', null, {}]) assert.equal(resolveWebLocale(input), null)
})

test('every embedded text has five nonempty translations with matching placeholders', () => {
  for (const [key, translations] of Object.entries(WEB_TEXT)) {
    assert.equal(translations.length, WEB_LOCALES.length, key)
    const placeholders = translations[0].match(/\{\w+\}/g) ?? []
    for (const text of translations) {
      assert.ok(text.trim().length > 0, key)
      assert.deepEqual(text.match(/\{\w+\}/g) ?? [], placeholders, key)
    }
  }
  assert.equal(webText('ja', 'previous'), '前の曲')
  assert.equal(webText('fr', 'lockPosition'), 'Verrouiller la position de la capsule')
  assert.equal(webText('de', 'stateError', { error: 'example' }), 'Wiedergabestatus konnte nicht abgerufen werden: example')
})

test('a delayed boot locale cannot overwrite a live switch', () => {
  const state = createWebLocaleState('en')
  const bootRevision = state.revision
  assert.equal(state.apply('ja'), true)
  assert.equal(state.apply('fr', bootRevision), false)
  assert.equal(state.locale, 'ja')
  assert.equal(state.apply('unsupported'), false)
  assert.equal(state.locale, 'ja')
})

test('embedded page locale follows host live events, updates document and unsubscribes on unmount', async () => {
  const sent: Array<Record<string, unknown>> = []
  let receive: (frame: { data: unknown }) => void = () => {}
  Object.assign(globalThis, {
    window: { setTimeout, clearTimeout, chrome: { webview: {
      postMessage: (frame: unknown) => sent.push(frame as Record<string, unknown>),
      addEventListener: (_type: string, handler: typeof receive) => { receive = handler },
    } } },
    document: { documentElement: { lang: '' }, title: '' },
  })
  initHostBridge()
  const renderer = createRenderer<object, object>({
    patchProp() {}, insert() {}, remove() {}, createElement: () => ({}), createText: () => ({}),
    createComment: () => ({}), setText() {}, setElementText() {}, parentNode: () => null, nextSibling: () => null,
  })
  let translate: (key: WebTextKey) => string = () => ''
  let pauseLabel = computed(() => '')
  const app = renderer.createApp({ setup() {
    const { t } = useWebLocale('capsuleTitle')
    translate = t
    pauseLabel = computed(() => t('pause'))
    return () => h('div')
  } })
  app.mount({})
  assert.equal(sent[0].command, 'web_locale_get')
  receive({ data: { event: 'web.locale', data: { language: 'ja' } } })
  assert.equal(pauseLabel.value, '一時停止')
  receive({ data: { id: sent[0].id, result: { language: 'en' } } })
  await Promise.resolve()
  assert.equal(document.documentElement.lang, 'ja', 'late boot reply must not revert live selection')
  assert.equal(document.title, 'OFPlayer 歌詞カプセル')
  for (const [language, expected] of [['fr', 'Titre suivant'], ['de', 'Nächster Titel'], ['zh-CN', '下一首'], ['en', 'Next track']]) {
    receive({ data: { event: 'web.locale', data: { language } } })
    assert.equal(translate('next'), expected)
    assert.equal(document.documentElement.lang, language)
  }
  receive({ data: { event: 'web.locale', data: { language: null } } })
  assert.equal(document.documentElement.lang, 'en')
  app.unmount()
  receive({ data: { event: 'web.locale', data: { language: 'de' } } })
  assert.equal(document.documentElement.lang, 'en', 'unmounted page ignores events')
})

test('snapshot normalization keeps locale-dependent placeholders out of music metadata', () => {
  const empty = createLyricCapsuleSnapshot()
  assert.equal(empty.title, '')
  assert.equal(empty.lyricText, '')
  assert.equal(empty.hasTrack, false)
  const song = createLyricCapsuleSnapshot({ trackId: 'song', title: '日本語の曲名', artist: 'Artiste', lyricText: 'Original lyrics' })
  assert.equal(song.title, '日本語の曲名')
  assert.equal(song.lyricText, 'Original lyrics')
  assert.equal(song.artist, 'Artiste')
})

test('unmounted page ignores a pending initial language reply', async () => {
  const sent: Array<Record<string, unknown>> = []
  let receive: (frame: { data: unknown }) => void = () => {}
  Object.assign(globalThis, {
    window: { setTimeout, clearTimeout, chrome: { webview: {
      postMessage: (frame: unknown) => sent.push(frame as Record<string, unknown>),
      addEventListener: (_type: string, handler: typeof receive) => { receive = handler },
    } } },
    document: { documentElement: { lang: '' }, title: '' },
  })
  initHostBridge()
  const renderer = createRenderer<object, object>({
    patchProp() {}, insert() {}, remove() {}, createElement: () => ({}), createText: () => ({}),
    createComment: () => ({}), setText() {}, setElementText() {}, parentNode: () => null, nextSibling: () => null,
  })
  let translate: (key: WebTextKey) => string = () => ''
  const app = renderer.createApp({ setup() {
    translate = useWebLocale('capsuleTitle').t
    return () => h('div')
  } })
  app.mount({})
  const initialLanguage = document.documentElement.lang
  const initialTitle = document.title
  const initialLabel = translate('next')
  app.unmount()
  const differentLanguage = initialLanguage === 'de' ? 'ja' : 'de'
  receive({ data: { id: sent[0].id, result: { language: differentLanguage } } })
  await Promise.resolve()
  assert.equal(document.documentElement.lang, initialLanguage)
  assert.equal(document.title, initialTitle)
  assert.equal(translate('next'), initialLabel)
})
