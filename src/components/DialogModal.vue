<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { X } from 'lucide-vue-next'
import { useI18n } from '../composables/useI18n'

interface DialogModalProps {
  isOpen?: boolean
  title?: string
  message?: string
  confirmLabel?: string
  cancelLabel?: string
  inputLabel?: string
  inputValue?: string
  isDanger?: boolean
  showInput?: boolean
}

const props = withDefaults(defineProps<DialogModalProps>(), {
  isOpen: false,
  title: '',
  message: '',
  confirmLabel: '',
  cancelLabel: '',
  inputLabel: '',
  inputValue: '',
  isDanger: false,
  showInput: false,
})

const emit = defineEmits<{
  close: []
  confirm: [value: string]
}>()

const { t } = useI18n()
const localInputValue = ref(props.inputValue)
const resolvedConfirmLabel = computed(() => props.confirmLabel || t('common.confirm'))
const resolvedCancelLabel = computed(() => props.cancelLabel || t('common.cancel'))
const closeDialogLabel = computed(() => t('common.closeDialog'))

watch(
  () => props.inputValue,
  (newValue) => {
    localInputValue.value = newValue
  },
)

function handleConfirm() {
  emit('confirm', localInputValue.value)
  emit('close')
}

function handleCancel() {
  emit('close')
}
</script>

<template>
  <Teleport to="body">
    <Transition name="dialog">
      <div v-if="isOpen" class="dialog-backdrop">
        <div class="dialog-modal" role="dialog" :aria-labelledby="title ? 'dialog-title' : undefined">
          <header v-if="title" class="dialog-header">
            <h2 id="dialog-title" class="dialog-title">{{ title }}</h2>
            <button
              type="button"
              class="dialog-close"
              :aria-label="closeDialogLabel"
              @click="handleCancel"
            >
              <X aria-hidden="true" />
            </button>
          </header>

          <div class="dialog-body">
            <p v-if="message" class="dialog-message">{{ message }}</p>

            <label v-if="showInput" class="dialog-input-wrap">
              <span v-if="inputLabel" class="dialog-input-label">{{ inputLabel }}</span>
              <input
                v-model="localInputValue"
                type="text"
                class="dialog-input"
                @keydown.enter="handleConfirm"
              />
            </label>
          </div>

          <footer class="dialog-footer">
            <button type="button" class="dialog-button dialog-button-cancel" @click="handleCancel">
              {{ resolvedCancelLabel }}
            </button>
            <button
              type="button"
              class="dialog-button dialog-button-confirm"
              :class="{ 'is-danger': isDanger }"
              @click="handleConfirm"
            >
              {{ resolvedConfirmLabel }}
            </button>
          </footer>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.dialog-backdrop {
  position: fixed;
  inset: 0;
  z-index: 60;
  display: grid;
  place-items: center;
  padding: var(--space-6);
  background: var(--dialog-backdrop);
  backdrop-filter: var(--dialog-backdrop-filter);
  -webkit-backdrop-filter: var(--dialog-backdrop-filter);
}

.dialog-modal {
  width: min(420px, calc(100vw - 48px));
  padding: var(--space-6);
  border-radius: var(--radius-2xl);
  border: 1px solid var(--line);
  background: var(--surface-modal);
  box-shadow: var(--shadow-lg);
}

.dialog-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-4);
  margin-bottom: var(--space-5);
}

.dialog-title {
  margin: 0;
  font-size: var(--font-size-xl);
  font-weight: var(--font-weight-semibold);
  color: var(--ink);
  line-height: var(--line-height-tight);
}

.dialog-close {
  flex-shrink: 0;
  display: grid;
  place-items: center;
  width: 32px;
  height: 32px;
  padding: 0;
  border: 1px solid var(--line);
  border-radius: 50%;
  background: var(--control-soft);
  color: var(--ink-soft);
  cursor: pointer;
  transition: all var(--transition-fast);
}

.dialog-close:hover {
  border-color: var(--line-strong);
  background: var(--control-soft-hover);
  color: var(--ink);
}

.dialog-close svg {
  width: 16px;
  height: 16px;
}

.dialog-body {
  margin-bottom: var(--space-5);
}

.dialog-message {
  margin: 0 0 var(--space-4);
  font-size: var(--font-size-base);
  color: var(--ink-muted);
  line-height: var(--line-height-relaxed);
}

.dialog-message:last-child {
  margin-bottom: 0;
}

.dialog-input-wrap {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
}

.dialog-input-label {
  font-size: var(--font-size-sm);
  font-weight: var(--font-weight-medium);
  color: var(--ink-soft);
}

.dialog-input {
  width: 100%;
  padding: 0.625rem 0.875rem;
  border: 1px solid var(--line);
  border-radius: var(--radius-md);
  background: var(--surface-soft);
  font-size: var(--font-size-base);
  color: var(--ink);
  transition: all var(--transition-fast);
}

.dialog-input:focus {
  outline: none;
  border-color: var(--line-strong);
  background: var(--surface-modal);
}

.dialog-footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: var(--space-3);
}

.dialog-button {
  padding: 0.625rem 1.25rem;
  border: 1px solid var(--line);
  border-radius: var(--radius-full);
  font-size: var(--font-size-sm);
  font-weight: var(--font-weight-semibold);
  cursor: pointer;
  transition: all var(--transition-fast);
}

.dialog-button-cancel {
  background: var(--surface-soft);
  color: var(--ink);
}

.dialog-button-cancel:hover {
  border-color: var(--line-strong);
  background: var(--surface-soft-hover);
}

.dialog-button-confirm {
  background: var(--button-primary-bg);
  border-color: var(--button-primary-border);
  color: var(--button-primary-ink);
}

.dialog-button-confirm:hover {
  box-shadow: var(--shadow-md);
}

.dialog-button-confirm.is-danger {
  background: var(--of-danger);
  border-color: var(--of-danger-active);
  color: var(--of-danger-text-on-fill);
}

.dialog-button-confirm.is-danger:hover {
  background: var(--of-danger-hover);
  box-shadow: var(--of-glow-danger);
}

.dialog-enter-active,
.dialog-leave-active {
  transition: all var(--transition-normal);
}

.dialog-enter-active .dialog-modal,
.dialog-leave-active .dialog-modal {
  transition: all var(--transition-normal);
}

.dialog-enter-from,
.dialog-leave-to {
  opacity: 0;
}

.dialog-enter-from .dialog-modal,
.dialog-leave-to .dialog-modal {
  opacity: 0;
  transform: translateY(12px) scale(0.98);
}
</style>
