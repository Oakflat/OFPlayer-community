import { invoke } from '@tauri-apps/api/core'

export interface ImmersiveWindowModeRequest {
  hideTaskbar: boolean
}

export interface ImmersiveWindowModeSnapshot {
  fullscreen: boolean
  maximized: boolean
  alwaysOnTop: boolean
}

export function applyNativeImmersiveWindowMode(
  request: ImmersiveWindowModeRequest,
): Promise<ImmersiveWindowModeSnapshot> {
  return invoke<ImmersiveWindowModeSnapshot>('immersive_window_apply_mode', { request })
}
