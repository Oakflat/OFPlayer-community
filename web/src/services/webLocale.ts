export const WEB_LOCALES = ['en', 'zh-CN', 'ja', 'fr', 'de'] as const
export type WebLocale = typeof WEB_LOCALES[number]

// Every embedded-page string has an explicit value in each supported language.
export const WEB_TEXT = {
  connecting: ['Connecting to OFPlayer…', '正在连接宿主…', 'OFPlayer に接続中…', 'Connexion à OFPlayer…', 'Verbindung mit OFPlayer…'],
  unavailable: ['Open this page in the OFPlayer immersive view.', '宿主桥接不可用（此页面需在 OFPlayer 沉浸式视图中打开）', 'このページは OFPlayer の没入型ビューで開いてください。', 'Ouvrez cette page dans la vue immersive d’OFPlayer.', 'Öffnen Sie diese Seite in der immersiven Ansicht von OFPlayer.'],
  unknownTrack: ['Unknown track', '未知曲目', '不明な曲', 'Titre inconnu', 'Unbekannter Titel'],
  noLyrics: ['No lyrics', '无歌词', '歌詞なし', 'Aucune parole', 'Keine Liedtexte'],
  togetherLyricsUnavailable: ["Lyrics aren't shared in Listen Together yet. They can't be edited on this device.", '一起听暂未共享歌词，接收设备也不能编辑这首远端歌曲的歌词。', '一緒に聴く機能では、歌詞はまだ共有されません。この端末では編集できません。', 'Les paroles ne sont pas encore partagées dans l’écoute à plusieurs et ne peuvent pas être modifiées sur cet appareil.', 'Beim gemeinsamen Hören werden Liedtexte noch nicht geteilt. Sie können auf diesem Gerät nicht bearbeitet werden.'],
  chooseTrack: ['Choose a track to start playing', '选择曲目开始播放', '曲を選んで再生してください', 'Choisissez un titre pour lancer la lecture', 'Wählen Sie einen Titel zum Abspielen'],
  trackError: ['Could not load track: {error}', '曲目加载失败：{error}', '曲を読み込めませんでした：{error}', 'Impossible de charger le titre : {error}', 'Titel konnte nicht geladen werden: {error}'],
  lyricError: ['Could not read lyrics: {error}', '歌词解析失败：{error}', '歌詞を読み取れませんでした：{error}', 'Impossible de lire les paroles : {error}', 'Liedtexte konnten nicht gelesen werden: {error}'],
  stateError: ['Could not get playback status: {error}', '状态获取失败：{error}', '再生状態を取得できませんでした：{error}', 'Impossible de récupérer l’état de lecture : {error}', 'Wiedergabestatus konnte nicht abgerufen werden: {error}'],
  seekError: ['Could not seek: {error}', '跳转失败：{error}', '再生位置を変更できませんでした：{error}', 'Impossible de changer la position : {error}', 'Wiedergabeposition konnte nicht geändert werden: {error}'],
  controlError: ['Playback control failed: {error}', '控制失败：{error}', '再生操作に失敗しました：{error}', 'Échec de la commande de lecture : {error}', 'Wiedergabesteuerung fehlgeschlagen: {error}'],
  volumeError: ['Could not change volume: {error}', '音量失败：{error}', '音量を変更できませんでした：{error}', 'Impossible de régler le volume : {error}', 'Lautstärke konnte nicht geändert werden: {error}'],
  bridgeUnavailable: ['Host bridge unavailable', '宿主桥接不可用', 'ホストとの接続が利用できません', 'Connexion à l’application indisponible', 'Verbindung zur Anwendung nicht verfügbar'],
  requestTimeout: ['Request timed out: {command}', '请求超时：{command}', '要求がタイムアウトしました：{command}', 'Délai de la requête dépassé : {command}', 'Zeitüberschreitung der Anfrage: {command}'],
  pinControls: ['Pin bottom controls', '固定底部控制条', '下部の操作バーを固定', 'Épingler les commandes du bas', 'Untere Steuerleiste anheften'],
  unpinControls: ['Unpin controls', '取消固定控制条', '操作バーの固定を解除', 'Détacher les commandes', 'Steuerleiste lösen'],
  showTaskbar: ['Show taskbar', '显示任务栏', 'タスクバーを表示', 'Afficher la barre des tâches', 'Taskleiste anzeigen'],
  hideTaskbar: ['Hide taskbar', '隐藏任务栏', 'タスクバーを非表示', 'Masquer la barre des tâches', 'Taskleiste ausblenden'],
  closeImmersive: ['Close immersive view', '收起沉浸式', '没入型ビューを閉じる', 'Fermer la vue immersive', 'Immersive Ansicht schließen'],
  closeImmersiveTip: ['Close immersive view (Esc)', '收起沉浸式（Esc）', '没入型ビューを閉じる（Esc）', 'Fermer la vue immersive (Échap)', 'Immersive Ansicht schließen (Esc)'],
  previous: ['Previous track', '上一首', '前の曲', 'Titre précédent', 'Vorheriger Titel'],
  next: ['Next track', '下一首', '次の曲', 'Titre suivant', 'Nächster Titel'],
  play: ['Play', '播放', '再生', 'Lire', 'Wiedergeben'],
  pause: ['Pause', '暂停', '一時停止', 'Mettre en pause', 'Pausieren'],
  progress: ['Playback progress', '播放进度', '再生位置', 'Progression de lecture', 'Wiedergabefortschritt'],
  volume: ['Volume', '音量', '音量', 'Volume', 'Lautstärke'],
  musicReady: ['Music is ready', '音乐已就绪', '音楽の準備ができました', 'La musique est prête', 'Musik ist bereit'],
  syncing: ['SYNC', '同步中', '同期中', 'SYNCHRO', 'SYNCH.'],
  ready: ['READY', '就绪', '準備完了', 'PRÊT', 'BEREIT'],
  live: ['LIVE', '播放中', '再生中', 'LECTURE', 'LÄUFT'],
  paused: ['PAUSED', '已暂停', '一時停止', 'EN PAUSE', 'PAUSIERT'],
  unlockPosition: ['Unlock capsule position', '解锁胶囊位置', 'カプセルの位置固定を解除', 'Déverrouiller la position de la capsule', 'Kapselposition entsperren'],
  lockPosition: ['Lock capsule position', '锁定胶囊位置', 'カプセルの位置を固定', 'Verrouiller la position de la capsule', 'Kapselposition sperren'],
  resetPosition: ['Restore default capsule position', '恢复胶囊默认位置', 'カプセルの位置を初期状態に戻す', 'Rétablir la position par défaut de la capsule', 'Standardposition der Kapsel wiederherstellen'],
  closeCapsule: ['Close lyric capsule', '关闭歌词胶囊', '歌詞カプセルを閉じる', 'Fermer la capsule de paroles', 'Liedtextkapsel schließen'],
  capsuleSettings: ['Lyric capsule settings', '歌词胶囊设置', '歌詞カプセルの設定', 'Réglages de la capsule de paroles', 'Einstellungen der Liedtextkapsel'],
  immersiveTitle: ['OFPlayer Immersive', 'OFPlayer 沉浸式', 'OFPlayer 没入型ビュー', 'OFPlayer · Vue immersive', 'OFPlayer · Immersive Ansicht'],
  capsuleTitle: ['OFPlayer Lyric Capsule', 'OFPlayer 歌词胶囊', 'OFPlayer 歌詞カプセル', 'OFPlayer · Capsule de paroles', 'OFPlayer · Liedtextkapsel'],
} as const satisfies Record<string, readonly [string, string, string, string, string]>

export type WebTextKey = keyof typeof WEB_TEXT
let activeLocale: WebLocale = 'en'
export function setActiveWebLocale(locale: WebLocale) { activeLocale = locale }
export function activeWebText(key: WebTextKey, values?: Record<string, string>) { return webText(activeLocale, key, values) }

export function resolveWebLocale(value: unknown): WebLocale | null {
  if (typeof value !== 'string') return null
  const base = value.trim().replaceAll('_', '-').toLowerCase().split('-')[0]
  return base === 'zh' ? 'zh-CN' : base === 'en' || base === 'ja' || base === 'fr' || base === 'de' ? base : null
}

export function webText(locale: WebLocale, key: WebTextKey, values: Record<string, string> = {}): string {
  const text = WEB_TEXT[key][WEB_LOCALES.indexOf(locale)]
  return text.replace(/\{(\w+)\}/g, (placeholder, name: string) => values[name] ?? placeholder)
}

// A live host locale wins over any delayed initial locale reply.
export function createWebLocaleState(initial: WebLocale) {
  let locale = initial
  let revision = 0
  return {
    get locale() { return locale },
    get revision() { return revision },
    apply(value: unknown, expectedRevision?: number): boolean {
      if (expectedRevision !== undefined && expectedRevision !== revision) return false
      const resolved = resolveWebLocale(value)
      if (!resolved) return false
      locale = resolved
      revision++
      return true
    },
  }
}
