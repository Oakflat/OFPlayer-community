export type TelemetryValue = string | number | boolean
export type TelemetryData = Record<string, TelemetryValue | null | undefined>

let enabled = false

export function setTelemetryEnabled(value: unknown): void {
  enabled = value === true
}

export function isTelemetryEnabled(): boolean {
  return enabled
}

export function getTelemetrySessionToken(): string {
  return ''
}

export function flushTelemetry(): number {
  return 0
}

export function track(eventName: string, data: TelemetryData = {}): void {
  void eventName
  void data
}

export function trackPlay(): void {
  track('playback.play')
}

export function trackPause(): void {
  track('playback.pause')
}

export function trackSkipNext(): void {
  track('playback.skip_next')
}

export function trackSkipPrev(): void {
  track('playback.skip_prev')
}

export function trackSeek(): void {
  track('playback.seek')
}

export function trackTelemetryConsent(value: unknown): void {
  void value
}
