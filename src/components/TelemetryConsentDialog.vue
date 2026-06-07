<script setup lang="ts">
import { onBeforeUnmount, onMounted } from 'vue'
import { ShieldCheck } from 'lucide-vue-next'
import { useI18n } from '../composables/useI18n'

const emit = defineEmits<{
  accept: []
  decline: []
}>()
const { t } = useI18n()

function handleKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    emit('decline')
  }
}

onMounted(() => {
  document.addEventListener('keydown', handleKeydown)
})

onBeforeUnmount(() => {
  document.removeEventListener('keydown', handleKeydown)
})
</script>

<template>
  <Teleport to="body">
    <div class="tc-root" role="dialog" aria-modal="true" aria-labelledby="telemetry-consent-title">
      <section class="tc-card">
        <div class="tc-icon" aria-hidden="true">
          <ShieldCheck />
        </div>

        <div class="tc-copy">
          <h2 id="telemetry-consent-title">{{ t('telemetry.consent.title') }}</h2>
          <p>{{ t('telemetry.consent.body') }}</p>
        </div>

        <ul class="tc-list">
          <li>{{ t('telemetry.consent.item1') }}</li>
          <li>{{ t('telemetry.consent.item2') }}</li>
          <li>{{ t('telemetry.consent.item3') }}</li>
        </ul>

        <p class="tc-note">{{ t('telemetry.consent.note') }}</p>

        <div class="tc-actions">
          <button class="tc-button tc-button--secondary" type="button" @click="emit('decline')">
            {{ t('telemetry.consent.decline') }}
          </button>
          <button class="tc-button tc-button--primary" type="button" @click="emit('accept')">
            {{ t('telemetry.consent.accept') }}
          </button>
        </div>
      </section>
    </div>
  </Teleport>
</template>

<style scoped>
.tc-root {
  position: fixed;
  right: 1.25rem;
  bottom: 1.25rem;
  z-index: 8900;
  width: min(380px, calc(100vw - 2rem));
  pointer-events: none;
  animation: fade-in var(--duration-md) var(--ease-standard) both;
}

.tc-card {
  display: grid;
  gap: 0.8rem;
  padding: 1.1rem;
  border: 1px solid var(--line-soft);
  border-radius: var(--radius-xl);
  background: var(--surface-modal);
  box-shadow: 0 18px 48px rgba(17, 24, 39, 0.18);
  pointer-events: auto;
  animation: slide-up-fade var(--duration-xl) var(--ease-emphasized-decelerate) both;
  animation-delay: 60ms;
}

.tc-icon {
  display: inline-grid;
  place-items: center;
  width: 38px;
  height: 38px;
  border-radius: 14px;
  background: var(--primary-container);
  color: var(--primary);
  animation: scale-in-soft var(--duration-lg) var(--ease-emphasized-decelerate) both;
  animation-delay: 150ms;
}

.tc-icon svg {
  width: 20px;
  height: 20px;
}

.tc-copy {
  display: grid;
  gap: 0.4rem;
}

.tc-copy h2 {
  margin: 0;
  font-size: 1rem;
  line-height: 1.3;
  letter-spacing: 0;
  color: var(--ink);
}

.tc-copy p,
.tc-note {
  margin: 0;
  font-size: var(--font-size-sm);
  line-height: 1.55;
  color: var(--ink-soft);
}

.tc-list {
  display: grid;
  gap: 0.35rem;
  margin: 0;
  padding-left: 1.05rem;
  color: var(--ink-muted);
  animation: fade-in var(--duration-lg) var(--ease-standard) both;
  animation-delay: 180ms;
}

.tc-list li {
  font-size: var(--font-size-xs);
  line-height: 1.5;
}

.tc-note {
  font-size: var(--font-size-xs);
  color: var(--ink-subtle);
}

.tc-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.55rem;
  animation: slide-up-fade var(--duration-lg) var(--ease-emphasized-decelerate) both;
  animation-delay: 220ms;
}

.tc-button {
  min-height: 38px;
  padding: 0 0.85rem;
  border-radius: var(--radius-full);
  border: 1px solid var(--line);
  font-size: var(--font-size-sm);
  font-weight: var(--font-weight-semibold);
  transition:
    border-color var(--transition-fast),
    background var(--transition-fast),
    color var(--transition-fast),
    transform var(--transition-bounce),
    box-shadow var(--transition-fast);
}

.tc-button:hover {
  transform: translateY(-1px);
  box-shadow: var(--shadow-sm);
}

.tc-button--secondary {
  background: var(--surface-soft);
  color: var(--ink-soft);
}

.tc-button--primary {
  border-color: var(--primary);
  background: var(--primary);
  color: var(--on-primary);
}

@media (max-width: 640px) {
  .tc-root {
    right: 1rem;
    bottom: 1rem;
  }

  .tc-actions {
    flex-direction: column-reverse;
  }

  .tc-button {
    width: 100%;
  }
}
</style>
