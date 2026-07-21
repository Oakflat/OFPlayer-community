<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch, type Component } from 'vue'
import { Check, Database, Globe2, Lock, Server, Wand2, X } from 'lucide-vue-next'
import { useI18n } from '../composables/useI18n'
import { EXTERNAL_LIBRARY_PROVIDERS } from '../models/externalLibrary'
import { normalizeExternalLibraryConnectionInput } from '../services/externalLibraryConnectionNormalizer'

type ExternalLibraryProvider = string
type PasswordInputType = 'password' | 'text'

interface ExternalLibraryDialogProps {
  isOpen?: boolean
  isConnecting?: boolean
  error?: string
}

interface ExternalLibraryForm {
  provider: ExternalLibraryProvider
  name: string
  endpoint: string
  rootPath: string
  username: string
  password: string
}

interface ExternalLibraryProviderOption {
  value: ExternalLibraryProvider
  label: string
  description: string
  icon: Component
  disabled: boolean
}

interface NormalizedExternalLibraryDraft {
  changed: boolean
  reason?: string
  connection: {
    provider: ExternalLibraryProvider
    name: string
    endpoint: string
    rootPath: string
  }
}

interface ExternalLibraryConnectPayload {
  provider: ExternalLibraryProvider
  name: string
  endpoint: string
  rootPath: string
  auth: {
    username: string
    password: string
  }
  sync: Record<string, never>
}

const props = withDefaults(defineProps<ExternalLibraryDialogProps>(), {
  isOpen: false,
  isConnecting: false,
  error: '',
})

const emit = defineEmits<{
  close: []
  connect: [payload: ExternalLibraryConnectPayload]
}>()

const { t } = useI18n()
const passwordInputType = ref<PasswordInputType>('password')
const form = reactive<ExternalLibraryForm>(createDefaultForm())

const providerOptions = computed<ExternalLibraryProviderOption[]>(() => [
  {
    value: EXTERNAL_LIBRARY_PROVIDERS.WEBDAV,
    label: t('externalLibrary.providers.webdav'),
    description: t('externalLibrary.providers.webdavCopy'),
    icon: Globe2,
    disabled: false,
  },
  {
    value: EXTERNAL_LIBRARY_PROVIDERS.SUBSONIC,
    label: t('externalLibrary.providers.subsonic'),
    description: t('externalLibrary.providers.subsonicCopy'),
    icon: Database,
    disabled: false,
  },
  {
    value: EXTERNAL_LIBRARY_PROVIDERS.FTP,
    label: t('externalLibrary.providers.ftp'),
    description: t('externalLibrary.providers.ftpCopy'),
    icon: Server,
    disabled: true,
  },
])

const canSubmit = computed(() =>
  !props.isConnecting &&
  Boolean(form.endpoint.trim()) &&
  Boolean(form.name.trim()) &&
    form.provider !== EXTERNAL_LIBRARY_PROVIDERS.FTP,
)
const normalizedDraft = computed<NormalizedExternalLibraryDraft>(
  () => normalizeExternalLibraryConnectionInput(form) as NormalizedExternalLibraryDraft,
)
const endpointPlaceholder = computed(() =>
  form.provider === EXTERNAL_LIBRARY_PROVIDERS.SUBSONIC
    ? 'http://192.168.100.1:4533'
    : 'http://192.168.100.1:5244/dav',
)
const rootPathLabel = computed(() =>
  form.provider === EXTERNAL_LIBRARY_PROVIDERS.SUBSONIC
    ? t('externalLibrary.fields.subsonicRootPath')
    : t('externalLibrary.fields.webdavRootPath'),
)
const rootPathPlaceholder = computed(() =>
  form.provider === EXTERNAL_LIBRARY_PROVIDERS.SUBSONIC ? '1' : '/Roomfile/music',
)
const normalizationMessage = computed(() => {
  const draft = normalizedDraft.value

  if (!draft.changed || !draft.reason) {
    return ''
  }

  if (draft.reason === 'navidrome') {
    return t('externalLibrary.autoFix.navidrome')
  }

  if (draft.reason === 'alist') {
    return t('externalLibrary.autoFix.alist')
  }

  if (draft.reason === 'webdav') {
    return t('externalLibrary.autoFix.webdav')
  }

  return ''
})
const hasNormalizationMessage = computed(() => Boolean(normalizationMessage.value))

watch(
  () => props.isOpen,
  (isOpen) => {
    if (isOpen) {
      Object.assign(form, createDefaultForm())
      passwordInputType.value = 'password'

      if (typeof document !== 'undefined') {
        document.body.classList.add('has-modal-open')
      }
    } else if (typeof document !== 'undefined') {
      document.body.classList.remove('has-modal-open')
    }
  },
)

onBeforeUnmount(() => {
  if (typeof document !== 'undefined') {
    document.body.classList.remove('has-modal-open')
  }
})

function createDefaultForm(): ExternalLibraryForm {
  return {
    provider: EXTERNAL_LIBRARY_PROVIDERS.WEBDAV,
    name: 'Remote Library',
    endpoint: '',
    rootPath: '',
    username: '',
    password: '',
  }
}

function selectProvider(provider: ExternalLibraryProvider) {
  if (props.isConnecting || provider === EXTERNAL_LIBRARY_PROVIDERS.FTP) {
    return
  }

  form.provider = provider

  if (provider === EXTERNAL_LIBRARY_PROVIDERS.SUBSONIC && form.name === 'Remote Library') {
    form.name = 'Navidrome'
  } else if (provider === EXTERNAL_LIBRARY_PROVIDERS.WEBDAV && form.name === 'Navidrome') {
    form.name = 'WebDAV'
  }
}

function applyConnectionNormalization() {
  if (props.isConnecting) {
    return
  }

  const draft = normalizeExternalLibraryConnectionInput(form) as NormalizedExternalLibraryDraft

  if (!draft.changed) {
    return
  }

  Object.assign(form, {
    provider: draft.connection.provider,
    name: draft.connection.name,
    endpoint: draft.connection.endpoint,
    rootPath: draft.connection.rootPath,
  })
}

function togglePasswordVisibility() {
  passwordInputType.value = passwordInputType.value === 'password' ? 'text' : 'password'
}

function submit() {
  applyConnectionNormalization()

  if (!canSubmit.value) {
    return
  }

  const draft = normalizeExternalLibraryConnectionInput(form) as NormalizedExternalLibraryDraft

  emit('connect', {
    provider: draft.connection.provider,
    name: draft.connection.name,
    endpoint: draft.connection.endpoint,
    rootPath: draft.connection.rootPath,
    auth: {
      username: form.username.trim(),
      password: form.password,
    },
    sync: {},
  })
}
</script>

<template>
  <Teleport to="body">
    <Transition name="dialog">
      <div v-if="isOpen" class="external-dialog-backdrop">
        <section class="external-dialog" role="dialog" aria-modal="true" :aria-label="t('externalLibrary.title')">
          <header class="external-dialog-head">
            <div>
              <p class="eyebrow">{{ t('externalLibrary.eyebrow') }}</p>
              <h2>{{ t('externalLibrary.title') }}</h2>
            </div>
            <button type="button" class="external-dialog-close" :aria-label="t('common.closeDialog')" @click="emit('close')">
              <X aria-hidden="true" />
            </button>
          </header>

          <form class="external-dialog-form" @submit.prevent="submit">
            <div class="external-provider-grid" :aria-label="t('externalLibrary.providerLabel')">
              <button
                v-for="provider in providerOptions"
                :key="provider.value"
                class="external-provider-option"
                :class="{ 'is-active': form.provider === provider.value, 'is-disabled': provider.disabled }"
                type="button"
                :disabled="provider.disabled"
                @click="selectProvider(provider.value)"
              >
                <component :is="provider.icon" class="external-provider-icon" aria-hidden="true" />
                <strong>{{ provider.label }}</strong>
                <span>{{ provider.description }}</span>
                <Check v-if="form.provider === provider.value" class="external-provider-check" aria-hidden="true" />
              </button>
            </div>

            <label class="external-field">
              <span>{{ t('externalLibrary.fields.name') }}</span>
              <input v-model="form.name" type="text" autocomplete="off" />
            </label>

            <label class="external-field">
              <span>{{ t('externalLibrary.fields.endpoint') }}</span>
              <input v-model="form.endpoint" type="text" inputmode="url" :placeholder="endpointPlaceholder" @blur="applyConnectionNormalization" />
            </label>

            <label class="external-field">
              <span>{{ rootPathLabel }}</span>
              <input v-model="form.rootPath" type="text" autocomplete="off" :placeholder="rootPathPlaceholder" @blur="applyConnectionNormalization" />
            </label>
            <p class="external-dialog-hint" :class="{ 'is-visible': hasNormalizationMessage }" aria-live="polite">
              <Wand2 aria-hidden="true" />
              <span>{{ normalizationMessage }}</span>
            </p>

            <div class="external-field-row">
              <label class="external-field">
                <span>{{ t('externalLibrary.fields.username') }}</span>
                <input v-model="form.username" type="text" autocomplete="username" />
              </label>

              <label class="external-field">
                <span>{{ t('externalLibrary.fields.password') }}</span>
                <span class="external-password-wrap">
                  <input v-model="form.password" :type="passwordInputType" autocomplete="current-password" />
                  <button type="button" :aria-label="t('externalLibrary.fields.togglePassword')" @click="togglePasswordVisibility">
                    <Lock aria-hidden="true" />
                  </button>
                </span>
              </label>
            </div>

            <p v-if="error" class="external-dialog-error">{{ error }}</p>

            <footer class="external-dialog-actions">
              <button type="button" class="external-dialog-button" @click="emit('close')">
                {{ t('common.cancel') }}
              </button>
              <button type="submit" class="external-dialog-button external-dialog-button--primary" :disabled="!canSubmit">
                {{ isConnecting ? t('externalLibrary.connecting') : t('externalLibrary.connect') }}
              </button>
            </footer>
          </form>
        </section>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.external-dialog-backdrop {
  position: fixed;
  inset: 0;
  z-index: 70;
  display: grid;
  place-items: center;
  padding: var(--space-6);
  background: rgba(22, 28, 38, 0.2);
  backdrop-filter: var(--dialog-backdrop-filter);
  -webkit-backdrop-filter: var(--dialog-backdrop-filter);
}

.external-dialog {
  width: min(620px, calc(100vw - 48px));
  max-height: min(760px, calc(100vh - 48px));
  overflow: auto;
  padding: var(--space-6);
  border: 1px solid var(--line);
  border-radius: var(--radius-2xl);
  background: var(--surface-modal);
  box-shadow: 0 24px 56px -18px rgba(26, 31, 46, 0.28);
}

.dialog-enter-active .external-dialog-head {
  animation: slide-up-fade var(--duration-lg) var(--ease-emphasized-decelerate) both;
  animation-delay: 70ms;
}

.dialog-enter-active .external-provider-option {
  animation: item-enter var(--duration-lg) var(--ease-emphasized-decelerate) both;
  animation-delay: calc(110ms + var(--provider-index, 0) * 36ms);
}

.dialog-enter-active .external-field,
.dialog-enter-active .external-dialog-error {
  animation: slide-up-fade-soft var(--duration-xl) var(--ease-emphasized-decelerate) both;
  animation-delay: calc(180ms + var(--field-index, 0) * 34ms);
}

.dialog-enter-active .external-dialog-actions {
  animation: slide-up-fade var(--duration-lg) var(--ease-emphasized-decelerate) both;
  animation-delay: 330ms;
}

.external-dialog-head,
.external-dialog-actions,
.external-field-row {
  display: flex;
  gap: var(--space-4);
}

.external-dialog-head {
  align-items: flex-start;
  justify-content: space-between;
  margin-bottom: var(--space-5);
}

.external-dialog-head h2 {
  margin: 0;
  color: var(--ink);
  font-size: var(--font-size-xl);
  line-height: var(--line-height-tight);
}

.external-dialog-close,
.external-password-wrap button {
  display: grid;
  place-items: center;
  padding: 0;
  border: 1px solid var(--line);
  background: var(--surface-variant);
  color: var(--ink-soft);
  cursor: pointer;
}

.external-dialog-close {
  width: 32px;
  height: 32px;
  border-radius: 50%;
}

.external-dialog-close svg,
.external-password-wrap svg {
  width: 16px;
  height: 16px;
}

.external-dialog-form {
  display: grid;
  gap: var(--space-4);
}

.external-provider-grid {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: var(--space-3);
}

.external-provider-option {
  position: relative;
  display: grid;
  gap: var(--space-2);
  min-height: 124px;
  padding: var(--space-4);
  border: 1px solid var(--line);
  border-radius: var(--radius-lg);
  background: var(--surface-variant);
  color: var(--ink);
  text-align: left;
  cursor: pointer;
  transition:
    border-color var(--transition-fast),
    background var(--transition-fast),
    transform var(--transition-normal),
    box-shadow var(--transition-fast),
    opacity var(--transition-fast);
}

.external-provider-option:nth-child(1) { --provider-index: 0; }
.external-provider-option:nth-child(2) { --provider-index: 1; }
.external-provider-option:nth-child(3) { --provider-index: 2; }

.external-provider-option:hover:not(:disabled) {
  transform: translateY(-2px);
  box-shadow: var(--shadow-sm);
}

.external-provider-option.is-active {
  border-color: var(--line-strong);
  background: var(--surface-modal-soft);
}

.external-provider-option.is-disabled {
  cursor: not-allowed;
  opacity: 0.55;
}

.external-provider-icon {
  width: 18px;
  height: 18px;
  color: var(--primary);
}

.external-provider-option strong,
.external-field span {
  color: var(--ink);
  font-size: var(--font-size-sm);
  font-weight: var(--font-weight-semibold);
}

.external-provider-option span {
  color: var(--ink-muted);
  font-size: var(--font-size-xs);
  line-height: var(--line-height-relaxed);
}

.external-provider-check {
  position: absolute;
  top: var(--space-3);
  right: var(--space-3);
  width: 16px;
  height: 16px;
  color: var(--primary);
}

.external-field {
  display: grid;
  flex: 1 1 0;
  gap: var(--space-2);
}

.external-dialog-form > .external-field:nth-of-type(1) { --field-index: 0; }
.external-dialog-form > .external-field:nth-of-type(2) { --field-index: 1; }
.external-dialog-form > .external-field:nth-of-type(3) { --field-index: 2; }
.external-field-row { --field-index: 3; }
.external-dialog-hint { --field-index: 3; }
.external-dialog-error { --field-index: 4; }

.external-field input {
  width: 100%;
  min-width: 0;
  padding: 0.625rem 0.75rem;
  border: 1px solid var(--line);
  border-radius: var(--radius-md);
  background: var(--surface-variant);
  color: var(--ink);
  transition:
    border-color var(--transition-fast),
    background var(--transition-fast),
    box-shadow var(--transition-fast);
}

.external-field input:focus {
  outline: none;
  border-color: var(--line-strong);
  background: var(--surface-modal-soft);
}

.external-password-wrap {
  display: grid;
  grid-template-columns: 1fr 36px;
}

.external-password-wrap input {
  border-top-right-radius: 0;
  border-bottom-right-radius: 0;
}

.external-password-wrap button {
  border-left: 0;
  border-radius: 0 var(--radius-md) var(--radius-md) 0;
}

.external-dialog-hint {
  display: flex;
  align-items: center;
  gap: var(--space-1);
  min-height: 18px;
  margin: calc(var(--space-2) * -1) 0 0;
  padding: 0 0.125rem;
  color: var(--ink-muted);
  font-size: var(--font-size-xs);
  line-height: var(--line-height-relaxed);
  opacity: 0;
  overflow: hidden;
  pointer-events: none;
  transform: translateY(-2px);
  transition:
    opacity var(--transition-fast),
    transform var(--transition-fast);
  white-space: nowrap;
}

.external-dialog-hint.is-visible {
  opacity: 1;
  transform: translateY(0);
}

.external-dialog-hint svg {
  flex: 0 0 auto;
  width: 16px;
  height: 16px;
  color: var(--primary);
}

.external-dialog-hint span {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}

.external-dialog-error {
  margin: 0;
  padding: var(--space-3);
  border: 1px solid var(--of-danger-border);
  border-radius: var(--radius-md);
  background: var(--of-danger-soft);
  color: var(--of-danger);
  font-size: var(--font-size-sm);
  line-height: var(--line-height-relaxed);
}

.external-dialog-actions {
  justify-content: flex-end;
  margin-top: var(--space-2);
}

.external-dialog-button {
  padding: 0.625rem 1rem;
  border: 1px solid var(--line);
  border-radius: var(--radius-full);
  background: var(--surface-variant);
  color: var(--ink);
  font-size: var(--font-size-sm);
  font-weight: var(--font-weight-semibold);
  cursor: pointer;
  transition:
    border-color var(--transition-fast),
    background var(--transition-fast),
    color var(--transition-fast),
    transform var(--transition-normal),
    box-shadow var(--transition-fast),
    opacity var(--transition-fast);
}

.external-dialog-button:hover:not(:disabled) {
  transform: translateY(-1px);
  box-shadow: var(--shadow-sm);
}

.external-dialog-button--primary {
  border-color: var(--button-primary-border);
  background: var(--button-primary-bg);
  color: var(--button-primary-ink);
}

.external-dialog-button:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}

@media (max-width: 680px) {
  .external-provider-grid,
  .external-field-row {
    grid-template-columns: 1fr;
    display: grid;
  }
}
</style>
