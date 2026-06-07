<script setup lang="ts">
import { computed } from 'vue'
import { Minimize2, Power, X } from 'lucide-vue-next'
import { useI18n } from '../composables/useI18n'

interface CloseBehaviorDialogProps {
  isOpen?: boolean
  capsuleActive?: boolean
  isBusy?: boolean
}

const props = withDefaults(defineProps<CloseBehaviorDialogProps>(), {
  isOpen: false,
  capsuleActive: false,
  isBusy: false,
})

const emit = defineEmits<{
  close: []
  minimize: []
  quit: []
}>()

const { t } = useI18n()

const message = computed(() =>
  props.capsuleActive ? t('window.closeChoiceCapsuleMessage') : t('window.closeChoiceMessage'),
)

function closeDialog() {
  if (!props.isBusy) {
    emit('close')
  }
}
</script>

<template>
  <Teleport to="body">
    <Transition name="dialog">
      <div v-if="isOpen" class="close-choice-backdrop" @click.self="closeDialog">
        <section
          class="close-choice-dialog"
          role="dialog"
          aria-modal="true"
          :aria-labelledby="'close-choice-title'"
          :aria-describedby="'close-choice-message'"
        >
          <header class="close-choice-header">
            <div>
              <p class="eyebrow">{{ t('window.closeChoiceEyebrow') }}</p>
              <h2 id="close-choice-title">{{ t('window.closeChoiceTitle') }}</h2>
            </div>
            <button
              class="close-choice-icon-button"
              type="button"
              :aria-label="t('common.closeDialog')"
              :disabled="isBusy"
              @click="closeDialog"
            >
              <X aria-hidden="true" />
            </button>
          </header>

          <p id="close-choice-message" class="close-choice-message">{{ message }}</p>

          <div class="close-choice-actions">
            <button
              class="close-choice-action is-minimize"
              type="button"
              :disabled="isBusy"
              @click="emit('minimize')"
            >
              <Minimize2 aria-hidden="true" />
              <span>
                <strong>{{ t('window.minimizeToBackground') }}</strong>
                <small>{{ t('window.minimizeToBackgroundCopy') }}</small>
              </span>
            </button>

            <button
              class="close-choice-action is-quit"
              type="button"
              :disabled="isBusy"
              @click="emit('quit')"
            >
              <Power aria-hidden="true" />
              <span>
                <strong>{{ isBusy ? t('window.quitWorking') : t('window.quitApp') }}</strong>
                <small>{{ t('window.quitAppCopy') }}</small>
              </span>
            </button>
          </div>
        </section>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.close-choice-backdrop {
  position: fixed;
  inset: 0;
  z-index: 70;
  display: grid;
  place-items: center;
  padding: var(--space-6);
  background: var(--dialog-backdrop);
  backdrop-filter: var(--dialog-backdrop-filter);
  -webkit-backdrop-filter: var(--dialog-backdrop-filter);
}

.close-choice-dialog {
  width: min(460px, calc(100vw - 48px));
  display: grid;
  gap: var(--space-5);
  padding: var(--space-6);
  border-radius: var(--radius-2xl);
  border: 1px solid var(--line);
  background: var(--surface-modal);
  box-shadow: var(--shadow-lg);
}

.close-choice-header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: var(--space-4);
}

.close-choice-header h2 {
  margin: 0.25rem 0 0;
  font-size: var(--font-size-xl);
  font-weight: var(--font-weight-semibold);
  line-height: var(--line-height-tight);
  color: var(--ink);
}

.close-choice-icon-button {
  flex: 0 0 auto;
  display: grid;
  place-items: center;
  width: 32px;
  height: 32px;
  padding: 0;
  border: 1px solid var(--line);
  border-radius: 50%;
  background: var(--control-soft);
  color: var(--ink-soft);
}

.close-choice-icon-button:hover:not(:disabled) {
  border-color: var(--line-strong);
  background: var(--control-soft-hover);
  color: var(--ink);
}

.close-choice-icon-button svg {
  width: 16px;
  height: 16px;
}

.close-choice-message {
  margin: 0;
  font-size: var(--font-size-base);
  line-height: var(--line-height-relaxed);
  color: var(--ink-muted);
}

.close-choice-actions {
  display: grid;
  gap: var(--space-3);
}

.close-choice-action {
  width: 100%;
  display: grid;
  grid-template-columns: 38px minmax(0, 1fr);
  gap: var(--space-3);
  align-items: center;
  min-height: 76px;
  padding: 0.875rem 1rem;
  border-radius: var(--radius-xl);
  border: 1px solid var(--line-soft);
  background: var(--surface-soft);
  text-align: left;
}

.close-choice-action:hover:not(:disabled) {
  border-color: var(--line-strong);
  background: var(--surface-soft-hover);
}

.close-choice-action.is-minimize {
  border-color: var(--color-playing-border);
  background: var(--color-playing-bg);
}

.close-choice-action.is-quit:hover:not(:disabled) {
  border-color: var(--of-danger-border);
  background: var(--of-danger-soft);
}

.close-choice-action > svg {
  width: 18px;
  height: 18px;
  justify-self: center;
  color: var(--ink-soft);
}

.close-choice-action.is-minimize > svg {
  color: var(--color-playing);
}

.close-choice-action.is-quit:hover:not(:disabled) > svg {
  color: var(--of-danger);
}

.close-choice-action span {
  min-width: 0;
  display: grid;
  gap: 0.2rem;
}

.close-choice-action strong,
.close-choice-action small {
  min-width: 0;
  overflow-wrap: anywhere;
}

.close-choice-action strong {
  font-size: var(--font-size-base);
  line-height: 1.35;
  color: var(--ink);
}

.close-choice-action small {
  font-size: var(--font-size-xs);
  line-height: 1.45;
  color: var(--ink-muted);
}

.close-choice-action:disabled,
.close-choice-icon-button:disabled {
  cursor: not-allowed;
  opacity: 0.55;
}

@media (max-width: 520px) {
  .close-choice-backdrop {
    padding: var(--space-4);
  }

  .close-choice-dialog {
    width: calc(100vw - 32px);
    padding: var(--space-5);
  }
}
</style>
