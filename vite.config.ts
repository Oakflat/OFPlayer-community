import { resolve } from 'node:path'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import tauriConfig from './src-tauri/tauri.conf.json'

const host = process.env.TAURI_DEV_HOST as string | undefined
const platform = process.env.TAURI_ENV_PLATFORM as string | undefined
const isTauriDebug = process.env.TAURI_ENV_DEBUG === 'true'
const buildTarget = platform === 'windows' || !platform ? 'chrome105' : 'safari13'
// Keep the frontend and desktop shell on the same port.
const devServerPort = Number(new URL(tauriConfig.build.devUrl).port)

export default defineConfig(() => ({
  plugins: [vue()],
  clearScreen: false,
  server: {
    port: devServerPort,
    strictPort: true,
    host: host || false,
    hmr: host
      ? {
          protocol: 'ws' as const,
          host,
          port: devServerPort,
        }
      : undefined,
    watch: {
      ignored: ['**/src-tauri/**'],
    },
  },
  build: {
    target: buildTarget,
    emptyOutDir: true,
    cssCodeSplit: false,
    // WebView2 release builds need both backdrop-filter declarations preserved.
    cssMinify: false,
    minify: isTauriDebug ? false : 'esbuild',
    sourcemap: isTauriDebug,
    rollupOptions: {
      input: {
        main: resolve(__dirname, 'index.html'),
        lyricCapsule: resolve(__dirname, 'lyric-capsule.html'),
      },
    },
  },
}))
