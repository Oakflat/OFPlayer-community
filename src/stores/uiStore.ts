import { computed, reactive } from 'vue'
import { createPreferencesModel } from '../models/preferences'

type PreferencesState = ReturnType<typeof createPreferencesModel>

type UiStoreOptions = {
  dataService?: any
  initialPreferences?: Record<string, any> | null
}

function applyPreferencesState(state: PreferencesState, nextState: PreferencesState): void {
  state.volume = nextState.volume
  state.librarySearchQuery = nextState.librarySearchQuery
  state.librarySortOption = nextState.librarySortOption
  state.libraryTypeFilter = nextState.libraryTypeFilter
  state.dataDriver = nextState.dataDriver
}

export function createUiStore({ dataService, initialPreferences }: UiStoreOptions = {}) {
  const state = reactive(createPreferencesModel(initialPreferences ?? undefined))

  async function hydrate() {
    const persistedPreferences = await dataService.preferences.load()
    applyPreferencesState(state, createPreferencesModel(persistedPreferences))
    return state
  }

  function persist() {
    void dataService.preferences.save({ ...state })
  }

  function applyNormalizedPatch(patch: Record<string, any>) {
    const normalized = createPreferencesModel({
      ...state,
      ...patch,
    })

    applyPreferencesState(state, normalized)
    persist()
    return normalized
  }

  function setSearchQuery(query: unknown) {
    return applyNormalizedPatch({ librarySearchQuery: query }).librarySearchQuery
  }

  function setSortOption(option: unknown) {
    return applyNormalizedPatch({ librarySortOption: option }).librarySortOption
  }

  function setTypeFilter(option: unknown) {
    return applyNormalizedPatch({ libraryTypeFilter: option }).libraryTypeFilter
  }

  function setVolume(volume: unknown) {
    return applyNormalizedPatch({ volume }).volume
  }

  return {
    state,
    volume: computed(() => state.volume),
    searchQuery: computed(() => state.librarySearchQuery),
    sortOption: computed(() => state.librarySortOption),
    typeFilter: computed(() => state.libraryTypeFilter),
    hydrate,
    setSearchQuery,
    setSortOption,
    setTypeFilter,
    setVolume,
  }
}
