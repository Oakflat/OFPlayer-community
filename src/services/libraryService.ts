import { normalizeEntityName, sortByOrder } from './catalogHelpers'
import { loadCatalogState } from './catalogState'

type ServiceRecord = Record<string, any>

type CreateLibraryInput =
  | string
  | {
      name?: unknown
      source?: unknown
      [key: string]: any
    }

export function createLibraryService({ dataService }: { dataService: ServiceRecord }) {
  const libraryTransactions = dataService.catalog.libraryTransactions

  async function loadCatalog(preloadedSnapshot = null, options: ServiceRecord = {}) {
    return loadCatalogState(dataService, preloadedSnapshot, options)
  }

  async function listLibraries() {
    const snapshot = await loadCatalog()
    return sortByOrder(snapshot.libraries)
  }

  async function createLibrary(input: CreateLibraryInput = {}) {
    const name = typeof input === 'string' ? input : input.name
    const source = typeof input === 'object' && input ? input.source : undefined
    const normalizedName = normalizeEntityName(name, 'Library')
    const result = await libraryTransactions.createLibrary({
      name: normalizedName,
    })

    if (!source) {
      return result
    }

    const library = await dataService.catalog.putLibrary({
      ...result.library,
      source,
      updatedAt: new Date().toISOString(),
    })

    return {
      ...result,
      library,
    }
  }

  async function renameLibrary(libraryId: string, name: unknown) {
    const normalizedName = normalizeEntityName(name, 'Library')
    return libraryTransactions.renameLibrary({
      libraryId,
      name: normalizedName,
    })
  }

  async function deleteLibrary(libraryId: string) {
    return libraryTransactions.deleteLibrary({ libraryId })
  }

  async function reorderLibraries(orderedLibraryIds: string[]) {
    return libraryTransactions.reorderLibraries({ orderedLibraryIds })
  }

  return {
    loadCatalog,
    listLibraries,
    createLibrary,
    renameLibrary,
    deleteLibrary,
    reorderLibraries,
  }
}
