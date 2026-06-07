<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch, type CSSProperties } from 'vue'

export interface MenuDropdownItem {
  key: string | number
  label: string
  disabled?: boolean
  [key: string]: unknown
}

interface MenuDropdownProps {
  isOpen?: boolean
  items?: MenuDropdownItem[]
  anchorEl?: HTMLElement | null
}

const props = withDefaults(defineProps<MenuDropdownProps>(), {
  isOpen: false,
  items: () => [],
  anchorEl: null,
})

const emit = defineEmits<{
  close: []
  select: [item: MenuDropdownItem]
}>()

const menuRef = ref<HTMLElement | null>(null)
const menuStyle = ref<CSSProperties>({})

function isDomNode(value: EventTarget | null): value is Node {
  return value instanceof Node
}

function handleDocumentClick(event: MouseEvent) {
  if (!props.isOpen) {
    return
  }

  if (
    isDomNode(event.target) &&
    (menuRef.value?.contains(event.target) || props.anchorEl?.contains(event.target))
  ) {
    return
  }

  emit('close')
}

function updatePosition() {
  if (!props.isOpen || !props.anchorEl) {
    menuStyle.value = {}
    return
  }

  const anchorRect = props.anchorEl.getBoundingClientRect()
  const viewportWidth = window.innerWidth
  const viewportHeight = window.innerHeight
  const margin = 12
  const gap = 6
  const maxMenuWidth = Math.max(0, viewportWidth - margin * 2)
  const menuWidth = Math.min(menuRef.value?.offsetWidth ?? 168, maxMenuWidth)
  const menuHeight = menuRef.value?.offsetHeight ?? 0

  let left = anchorRect.right - menuWidth
  left = Math.max(margin, Math.min(left, viewportWidth - menuWidth - margin))

  let top = anchorRect.bottom + gap

  if (top + menuHeight > viewportHeight - margin) {
    top = Math.max(margin, anchorRect.top - menuHeight - gap)
  }

  menuStyle.value = {
    left: `${left}px`,
    top: `${top}px`,
    maxWidth: `${maxMenuWidth}px`,
  }
}

function handleViewportChange() {
  if (!props.isOpen) {
    emit('close')
    return
  }

  updatePosition()
}

function handleEscape(event: KeyboardEvent) {
  if (event.key === 'Escape' && props.isOpen) {
    emit('close')
  }
}

function handleItemClick(item: MenuDropdownItem) {
  if (item.disabled) {
    return
  }

  emit('select', item)
  emit('close')
}

onMounted(() => {
  document.addEventListener('click', handleDocumentClick, true)
  document.addEventListener('keydown', handleEscape, true)
  window.addEventListener('resize', handleViewportChange)
  window.addEventListener('scroll', handleViewportChange, true)
})

onBeforeUnmount(() => {
  document.removeEventListener('click', handleDocumentClick, true)
  document.removeEventListener('keydown', handleEscape, true)
  window.removeEventListener('resize', handleViewportChange)
  window.removeEventListener('scroll', handleViewportChange, true)
})

watch(
  () => [props.isOpen, props.anchorEl, props.items.length] as const,
  async ([isOpen]) => {
    if (!isOpen) {
      menuStyle.value = {}
      return
    }

    await nextTick()
    updatePosition()
    window.requestAnimationFrame(updatePosition)
  },
  { immediate: true },
)
</script>

<template>
  <Teleport to="body">
    <Transition name="menu-dropdown">
      <div v-if="isOpen" ref="menuRef" class="menu-dropdown" :style="menuStyle" role="menu">
        <button
          v-for="item in items"
          :key="item.key"
          type="button"
          class="menu-dropdown-item"
          :class="{ 'is-disabled': item.disabled }"
          :disabled="item.disabled"
          role="menuitem"
          @click="handleItemClick(item)"
        >
          {{ item.label }}
        </button>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.menu-dropdown {
  position: fixed;
  z-index: 64;
  width: max-content;
  min-width: min(164px, calc(100vw - 24px));
  max-width: min(320px, calc(100vw - 24px));
  padding: 0.375rem;
  border-radius: var(--radius-md);
  border: 1px solid var(--line-soft);
  background: var(--surface-solid);
  box-shadow:
    0 0 0 0.5px var(--border-inner) inset,
    var(--shadow-md);
  overflow: hidden;
  backdrop-filter: blur(18px) saturate(1.08);
  -webkit-backdrop-filter: blur(18px) saturate(1.08);
}

.menu-dropdown-item {
  display: block;
  width: 100%;
  min-width: 0;
  padding: 0.5rem 0.7rem;
  border: none;
  border-radius: var(--radius-sm);
  background: transparent;
  text-align: left;
  font-size: var(--font-size-sm);
  font-weight: var(--font-weight-medium);
  color: var(--ink);
  cursor: pointer;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  transition:
    background-color var(--transition-fast),
    color var(--transition-fast);
}

.menu-dropdown-item:hover:not(:disabled) {
  background: var(--surface-soft-hover);
}

.menu-dropdown-item.is-disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.menu-dropdown-enter-active,
.menu-dropdown-leave-active {
  transition: all var(--transition-fast);
}

.menu-dropdown-enter-from,
.menu-dropdown-leave-to {
  opacity: 0;
  transform: translateY(-4px) scale(0.98);
}

html[data-window-surface='native-glass'] .menu-dropdown {
  background: var(--surface-solid);
  border-color: var(--line-soft);
  box-shadow:
    0 0 0 0.5px var(--border-inner) inset,
    0 18px 42px -24px rgba(0, 0, 0, 0.46);
}

html[data-effective-color-scheme='dark'][data-window-surface='native-glass'] .menu-dropdown {
  background: rgba(15, 20, 27, 0.98);
}
</style>
