import assert from 'node:assert/strict'
import test from 'node:test'
import {
  LICENSE_PLANS,
  LICENSE_STATUSES,
  createFeatureLimitSnapshot,
  createLicenseStateModel,
  isLicenseUnlocked,
} from './license.ts'

test('community license model is active and unlocked', () => {
  const licenseState = createLicenseStateModel()

  assert.equal(licenseState.status, LICENSE_STATUSES.ACTIVE)
  assert.equal(licenseState.plan, LICENSE_PLANS.COMMUNITY)
  assert.equal(isLicenseUnlocked(licenseState), true)
})

test('community build applies no library or playlist limits', () => {
  const limits = createFeatureLimitSnapshot({
    libraryCount: 99,
    playlistCount: 99,
  })

  assert.equal(limits.unlocked, true)
  assert.equal(limits.canCreateLibrary, true)
  assert.equal(limits.canConnectLibrary, true)
  assert.equal(limits.canCreatePlaylist, true)
  assert.equal(limits.libraryLimit, null)
  assert.equal(limits.playlistLimit, null)
  assert.equal(limits.libraryRemaining, null)
  assert.equal(limits.playlistRemaining, null)
})
