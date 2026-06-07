import { invoke, isTauri } from '@tauri-apps/api/core'
import { listen } from '@tauri-apps/api/event'
import type { UnlistenFn } from '@tauri-apps/api/event'

const STORAGE_WATCH_EVENT = 'storage://watch-changed'
const LIBRARY_SCAN_PROGRESS_EVENT = 'library://scan-progress'

export interface ImportSourceFile {
  sourcePath?: string
  originalPath?: string
  nativePath?: string
  path?: string
  name?: string
  fileName?: string
  [key: string]: unknown
}

export interface DesktopImportItem {
  sourcePath: string
  originalPath: string
  fileName: string
}

export interface ConfigureWatchDirectoriesRequest {
  storageRoot: string
  directories: string[]
  enabled: boolean
}

export type DesktopStorageEventListener = (payload: unknown) => void

export interface DesktopStorageService {
  available: true
  requiresManagedStorage: false
  pickStorageDirectory(): Promise<string>
  pickScanDirectory(): Promise<string>
  pickLyricsDirectory(): Promise<string>
  pickLyricsFile(options?: { defaultPath?: string }): Promise<string>
  createImportItems(files?: Iterable<ImportSourceFile> | null): DesktopImportItem[]
  configureWatchDirectories(request: ConfigureWatchDirectoriesRequest): Promise<unknown>
  analyzeStorageUsage(): Promise<unknown>
  collectGarbage(): Promise<unknown>
  listenForWatchEvents(listener: DesktopStorageEventListener): Promise<UnlistenFn>
  listenForScanProgress(listener: DesktopStorageEventListener): Promise<UnlistenFn>
}

const noopUnlisten: UnlistenFn = () => {}

function normalizeDialogSelection(selection: string | string[] | null): string {
  if (typeof selection === 'string') {
    return selection
  }

  if (Array.isArray(selection)) {
    return typeof selection[0] === 'string' ? selection[0] : ''
  }

  return ''
}

function resolveDefaultDialogPath(value: unknown): string {
  return normalizeText(value)
}

function normalizeText(value: unknown): string {
  return typeof value === 'string' ? value.trim() : ''
}

function resolveImportSourcePath(file: ImportSourceFile): string {
  return (
    normalizeText(file?.sourcePath) ||
    normalizeText(file?.originalPath) ||
    normalizeText(file?.nativePath) ||
    normalizeText(file?.path)
  )
}

function resolveImportFileName(file: ImportSourceFile, sourcePath: string): string {
  const explicitFileName = normalizeText(file?.name) || normalizeText(file?.fileName)

  if (explicitFileName) {
    return explicitFileName
  }

  const normalizedPath = String(sourcePath ?? '').replace(/\\/g, '/')
  return normalizedPath.split('/').pop()?.trim() || 'track'
}

function createImportItems(files?: Iterable<ImportSourceFile> | null): DesktopImportItem[] {
  return Array.from(files ?? [])
    .map((file) => {
      const sourcePath = resolveImportSourcePath(file)

      if (!sourcePath) {
        return null
      }

      return {
        sourcePath,
        originalPath: normalizeText(file?.originalPath) || sourcePath,
        fileName: resolveImportFileName(file, sourcePath),
      }
    })
    .filter((item): item is DesktopImportItem => Boolean(item))
}

export function createDesktopStorageService(): DesktopStorageService {
  if (!isTauri()) {
    throw new Error('OFPlayer desktop storage features require the Tauri runtime.')
  }

  const available = true

  return {
    available,
    requiresManagedStorage: false,
    async pickStorageDirectory() {
      const { open } = await import('@tauri-apps/plugin-dialog')
      const selected = await open({
        directory: true,
        multiple: false,
      })

      return normalizeDialogSelection(selected)
    },
    async pickScanDirectory() {
      const { open } = await import('@tauri-apps/plugin-dialog')
      const selected = await open({
        directory: true,
        multiple: false,
      })

      return normalizeDialogSelection(selected)
    },
    async pickLyricsDirectory() {
      const { open } = await import('@tauri-apps/plugin-dialog')
      const selected = await open({
        directory: true,
        multiple: false,
      })

      return normalizeDialogSelection(selected)
    },
    async pickLyricsFile({ defaultPath = '' } = {}) {
      const { open } = await import('@tauri-apps/plugin-dialog')
      const selected = await open({
        multiple: false,
        directory: false,
        defaultPath: resolveDefaultDialogPath(defaultPath) || undefined,
        filters: [
          {
            name: 'Lyrics',
            extensions: ['lrc', 'txt'],
          },
        ],
      })

      return normalizeDialogSelection(selected)
    },
    createImportItems(files?: Iterable<ImportSourceFile> | null) {
      return createImportItems(files)
    },
    async configureWatchDirectories({ storageRoot, directories, enabled }: ConfigureWatchDirectoriesRequest) {
      return invoke('storage_configure_watch', {
        request: {
          storageRoot,
          directories,
          enabled,
        },
      })
    },
    async analyzeStorageUsage() {
      return invoke('desktop_storage_analyze')
    },
    async collectGarbage() {
      return invoke('desktop_storage_collect_garbage')
    },
    async listenForWatchEvents(listener: DesktopStorageEventListener) {
      if (typeof listener !== 'function') {
        return noopUnlisten
      }

      return listen(STORAGE_WATCH_EVENT, (event) => {
        listener(event.payload ?? null)
      })
    },
    async listenForScanProgress(listener: DesktopStorageEventListener) {
      if (typeof listener !== 'function') {
        return noopUnlisten
      }

      return listen(LIBRARY_SCAN_PROGRESS_EVENT, (event) => {
        listener(event.payload ?? null)
      })
    },
  }
}
