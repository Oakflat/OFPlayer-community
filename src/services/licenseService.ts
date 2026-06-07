import { createLicenseStateModel } from '../models/license'

export type LicensePlan = 'community'
export type LicenseStatus = 'active'

export interface LicenseState {
  status: LicenseStatus
  plan: LicensePlan
  betaAccess: boolean
  licenseId: string
  source: string
  checkedAt: string
  expiresAt: string
}

export interface LicenseServiceOptions {
  storage?: Pick<Storage, 'getItem' | 'setItem'> | null
}

export interface LicenseService {
  load(): LicenseState
  save(state: Partial<LicenseState> | null | undefined): LicenseState
  clear(): LicenseState
}

function createCommunityLicenseState(): LicenseState {
  return createLicenseStateModel({
    source: 'community',
  }) as LicenseState
}

export function createLicenseService(options: LicenseServiceOptions = {}): LicenseService {
  void options

  function load(): LicenseState {
    return createCommunityLicenseState()
  }

  function save(state: Partial<LicenseState> | null | undefined): LicenseState {
    void state
    return createCommunityLicenseState()
  }

  function clear(): LicenseState {
    return createCommunityLicenseState()
  }

  return {
    load,
    save,
    clear,
  }
}
