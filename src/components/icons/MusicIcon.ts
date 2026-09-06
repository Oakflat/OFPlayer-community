import { defineComponent, h, type PropType } from 'vue'
import { musicIconAttributes, musicIconPaths, type MusicIconName } from './musicIconPaths'

/** Decorative by default: the enclosing button owns its accessible label. */
export const MusicIcon = defineComponent({
  name: 'MusicIcon',
  inheritAttrs: false,
  props: { name: { type: String as PropType<MusicIconName>, required: true } },
  setup: (props, { attrs }) => () => h('svg', {
    ...musicIconAttributes, 'aria-hidden': 'true', focusable: 'false', ...attrs,
  }, musicIconPaths[props.name].map(path => h('path', path))),
})

function icon(name: MusicIconName) {
  return defineComponent({
    name: `MusicIcon-${name}`, inheritAttrs: false,
    setup: (_, { attrs }) => () => h(MusicIcon, { ...attrs, name }),
  })
}

export const MusicLibrary = icon('library')
export const MusicRemote = icon('remote')
export const MusicAllTracks = icon('all-tracks')
export const MusicRecentImports = icon('recent-imports')
export const MusicHistory = icon('history')
export const MusicFavorites = icon('favorites')
export const MusicQueue = icon('queue')
export const MusicAlbums = icon('albums')
export const MusicArtists = icon('artists')
export const MusicImportAudio = icon('import-audio')
export const MusicImportFolder = icon('import-folder')
export const MusicSettings = icon('settings')
