import { hostRequest } from '../immersiveWeb/hostBridge.ts'

export class LogicalPosition {
  public x: number
  public y: number
  constructor(x: number, y: number) { this.x = x; this.y = y }
}

type PhysicalPosition = { x: number; y: number }
type Monitor = {
  position: PhysicalPosition
  size: { width: number; height: number }
  scaleFactor: number
}

let dragOrigin: { x: number; y: number } | null = null
document.addEventListener('pointerdown', (event) => {
  if (event.button === 0) dragOrigin = { x: event.clientX, y: event.clientY }
}, true)
document.addEventListener('pointerup', () => { dragOrigin = null }, true)
document.addEventListener('pointercancel', () => { dragOrigin = null }, true)

const capsuleWindow = {
  setPosition(position: LogicalPosition): Promise<void> {
    return hostRequest<void>('capsule_set_position', { value: { Logical: { x: position.x, y: position.y } } })
  },
  outerPosition(): Promise<PhysicalPosition> {
    return hostRequest<PhysicalPosition>('capsule_outer_position')
  },
  scaleFactor(): Promise<number> {
    return hostRequest<number>('capsule_scale_factor')
  },
  startDragging(): Promise<void> {
    return hostRequest<void>('capsule_start_dragging',
      dragOrigin ? { dragClientX: dragOrigin.x, dragClientY: dragOrigin.y } : {},
      120_000)
  },
}

export function getCurrentWindow() { return capsuleWindow }
export function primaryMonitor(): Promise<Monitor | null> {
  return hostRequest<Monitor | null>('capsule_primary_monitor')
}
