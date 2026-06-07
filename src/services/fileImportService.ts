import { isTauri } from '@tauri-apps/api/core'
import {
  SUPPORTED_AUDIO_EXTENSIONS,
  SUPPORTED_AUDIO_EXTENSION_VALUES,
  resolveAudioMimeType,
  type TrackFileLike,
} from '../models/track'

const AUDIO_DIALOG_FILTERS = [
  {
    name: 'Audio',
    extensions: [...SUPPORTED_AUDIO_EXTENSION_VALUES],
  },
]

interface NativePathFile extends File {
  nativePath: string
}

function getFileExtension(filePath = ''): string {
  return String(filePath)
    .split('.')
    .pop()
    ?.trim()
    .toLowerCase() ?? ''
}

function resolveFileName(filePath = ''): string {
  const normalizedPath = String(filePath).replace(/\\/g, '/')
  return normalizedPath.split('/').pop()?.trim() || 'untitled-audio'
}

function createFileFromPath(filePath: string): NativePathFile {
  const extension = getFileExtension(filePath)
  const file = new File([], resolveFileName(filePath), {
    type: resolveAudioMimeType(extension),
  })

  Object.defineProperty(file, 'nativePath', {
    value: filePath,
    configurable: false,
    enumerable: false,
    writable: false,
  })

  return file as NativePathFile
}

function normalizeDialogSelection(selection: unknown): string[] {
  if (typeof selection === 'string') {
    return [selection]
  }

  if (Array.isArray(selection)) {
    return selection.filter((item) => typeof item === 'string')
  }

  return []
}

function isSupportedAudioPath(filePath: string): boolean {
  return SUPPORTED_AUDIO_EXTENSIONS.has(getFileExtension(filePath))
}

async function loadDialogPlugin() {
  const { open } = await import('@tauri-apps/plugin-dialog')
  return { open }
}

export function createFileImportService() {
  if (!isTauri()) {
    throw new Error('OFPlayer file import requires the Tauri runtime.')
  }

  const importMode = 'native-dialog'

  return {
    importMode,
    async pickAudioFiles(): Promise<TrackFileLike[]> {
      const { open } = await loadDialogPlugin()
      const selection = await open({
        multiple: true,
        directory: false,
        filters: AUDIO_DIALOG_FILTERS,
      })
      const selectedPaths = normalizeDialogSelection(selection).filter(isSupportedAudioPath)

      if (selectedPaths.length === 0) {
        return []
      }

      return selectedPaths.map((filePath) => createFileFromPath(filePath))
    },
  }
}
