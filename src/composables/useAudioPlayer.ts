import { useNativeAudioPlayer, type NativeAudioPlayerOptions } from './useNativeAudioPlayer'

export function useAudioPlayer(options: NativeAudioPlayerOptions = {}) {
  return useNativeAudioPlayer(options)
}
