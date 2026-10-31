import assert from 'node:assert/strict'
import test from 'node:test'
import { initHostBridge } from '../immersiveWeb/hostBridge.ts'

test('capsule position and native dragging use allowlisted WebView2 commands', async () => {
  const sent: Array<Record<string, unknown>> = []
  const documentHandlers = new Map<string, (event: { button: number; clientX: number; clientY: number }) => void>()
  let receive: (frame: { data: unknown }) => void = () => {}
  const webview = {
    postMessage(frame: unknown) { sent.push(frame as Record<string, unknown>) },
    addEventListener(_type: string, handler: (frame: { data: unknown }) => void) { receive = handler },
  }
  Object.assign(globalThis, {
    window: { chrome: { webview }, setTimeout, clearTimeout },
    document: { addEventListener: (name: string, handler: typeof documentHandlers extends Map<string, infer V> ? V : never) =>
      documentHandlers.set(name, handler) },
  })
  initHostBridge()
  const { getCurrentWindow, LogicalPosition, primaryMonitor } = await import('./capsuleWindowHost.ts')
  const position = getCurrentWindow().setPosition(new LogicalPosition(120, 30))
  assert.deepEqual(sent[0], {
    id: sent[0].id, command: 'capsule_set_position', args: { value: { Logical: { x: 120, y: 30 } } },
  })
  receive({ data: { id: sent[0].id, result: null } })
  await position

  documentHandlers.get('pointerdown')?.({ button: 0, clientX: 44, clientY: 12 })
  const drag = getCurrentWindow().startDragging()
  assert.deepEqual(sent[1], {
    id: sent[1].id, command: 'capsule_start_dragging', args: { dragClientX: 44, dragClientY: 12 },
  })
  receive({ data: { id: sent[1].id, result: null } })
  await drag

  const monitor = primaryMonitor()
  assert.equal(sent[2].command, 'capsule_primary_monitor')
  receive({ data: { id: sent[2].id, result: { position: { x: 0, y: 0 }, size: { width: 1920, height: 1080 }, scaleFactor: 1 } } })
  assert.equal((await monitor)?.size.width, 1920)
})
