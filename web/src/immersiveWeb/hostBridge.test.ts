import assert from 'node:assert/strict'
import test from 'node:test'
import { hostRequest, initHostBridge, onHostEvent } from './hostBridge.ts'

test('WebView2 bridge correlates responses, errors and event unsubscription', async () => {
  const sent: Array<Record<string, unknown>> = []
  let receive: (frame: { data: unknown }) => void = () => {}
  const webview = {
    postMessage(frame: unknown) { sent.push(frame as Record<string, unknown>) },
    addEventListener(_type: string, handler: (frame: { data: unknown }) => void) { receive = handler },
  }
  Object.assign(globalThis, {
    window: { chrome: { webview }, setTimeout, clearTimeout },
    requestAnimationFrame: (callback: () => void) => { callback(); return 1 },
  })
  assert.equal(initHostBridge(), true)
  assert.equal(initHostBridge(), true, 'reinitialization does not add another handler')

  const first = hostRequest<string>('capsule_get_boot_state')
  const second = hostRequest<string>('capsule_release')
  assert.deepEqual(sent.map(frame => frame.command), ['capsule_get_boot_state', 'capsule_release'])
  receive({ data: { id: sent[1].id, result: 'second' } })
  receive({ data: { id: sent[0].id, result: 'first' } })
  assert.equal(await first, 'first')
  assert.equal(await second, 'second')

  const failed = hostRequest('capsule_apply_hit_region')
  receive({ data: { id: sent[2].id, error: { code: 'invalid', message: 'bad region' } } })
  await assert.rejects(failed, /bad region/)

  const events: string[] = []
  const unlisten = onHostEvent(event => events.push(event.event))
  receive({ data: { event: 'capsule://state', data: { title: 'song' }, perfId: 7 } })
  unlisten()
  receive({ data: { event: 'capsule://meter', data: {} } })
  assert.deepEqual(events, ['capsule://state'])
  assert.deepEqual(sent.at(-1), { kind: 'perf.web.applied', id: 7, jsMs: sent.at(-1)?.jsMs })
})
