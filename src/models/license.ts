export const LICENSE_PLANS = Object.freeze({
  COMMUNITY: 'community',
})

export const LICENSE_STATUSES = Object.freeze({
  ACTIVE: 'active',
})

export const LICENSE_FEATURES = Object.freeze({
  LIBRARY: 'library',
  PLAYLIST: 'playlist',
})

const UNLIMITED_LIMIT = null

type LicensePlan = (typeof LICENSE_PLANS)[keyof typeof LICENSE_PLANS]
type LicenseStatus = (typeof LICENSE_STATUSES)[keyof typeof LICENSE_STATUSES]
type LicenseFeature = (typeof LICENSE_FEATURES)[keyof typeof LICENSE_FEATURES]

export type LicenseState = {
  status: LicenseStatus
  plan: LicensePlan
  betaAccess: boolean
  licenseId: string
  source: string
  checkedAt: string
  expiresAt: string
}

export type FeatureLimitSnapshot = {
  unlocked: boolean
  betaAccess: boolean
  libraryCount: number
  playlistCount: number
  libraryLimit: number | null
  playlistLimit: number | null
  canCreateLibrary: boolean
  canConnectLibrary: boolean
  canCreatePlaylist: boolean
  libraryRemaining: number | null
  playlistRemaining: number | null
}

function normalizeCount(value: unknown): number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 ? Math.floor(value) : 0
}

export function createLicenseStateModel(overrides: Partial<LicenseState> = {}): LicenseState {
  return {
    status: LICENSE_STATUSES.ACTIVE,
    plan: LICENSE_PLANS.COMMUNITY,
    betaAccess: true,
    licenseId: '',
    source: overrides.source || 'community',
    checkedAt: overrides.checkedAt || new Date().toISOString(),
    expiresAt: '',
  }
}

export function isLicenseUnlocked(
  licenseState: Partial<LicenseState> | null | undefined,
  options: { now?: number } = {},
): boolean {
  void licenseState
  void options
  return true
}

export function createFeatureLimitSnapshot({
  licenseState = null,
  libraryCount = 0,
  playlistCount = 0,
}: {
  licenseState?: Partial<LicenseState> | null
  libraryCount?: unknown
  playlistCount?: unknown
} = {}): FeatureLimitSnapshot {
  void licenseState

  return {
    unlocked: true,
    betaAccess: true,
    libraryCount: normalizeCount(libraryCount),
    playlistCount: normalizeCount(playlistCount),
    libraryLimit: UNLIMITED_LIMIT,
    playlistLimit: UNLIMITED_LIMIT,
    canCreateLibrary: true,
    canConnectLibrary: true,
    canCreatePlaylist: true,
    libraryRemaining: UNLIMITED_LIMIT,
    playlistRemaining: UNLIMITED_LIMIT,
  }
}

export function createFeatureLimitError(
  feature: LicenseFeature,
  snapshot?: FeatureLimitSnapshot,
): Error & {
  code?: string
  feature?: LicenseFeature
  limit?: number | null
  count?: number
} {
  const limits = snapshot ?? createFeatureLimitSnapshot()
  const isPlaylist = feature === LICENSE_FEATURES.PLAYLIST
  const error = new Error('The community build does not enforce library or playlist limits.') as Error & {
    code?: string
    feature?: LicenseFeature
    limit?: number | null
    count?: number
  }

  error.code = 'OFPLAYER_COMMUNITY_NO_FEATURE_LIMIT'
  error.feature = feature
  error.limit = isPlaylist ? limits.playlistLimit : limits.libraryLimit
  error.count = isPlaylist ? limits.playlistCount : limits.libraryCount
  return error
}
