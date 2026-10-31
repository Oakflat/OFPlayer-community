import { resolve } from 'node:path'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  cacheDir: 'node_modules/.vite',
  plugins: [vue()],
  clearScreen: false,
  base: '/',
  build: {
    target: 'chrome105',
    outDir: 'dist',
    emptyOutDir: true,
    cssCodeSplit: false,
    cssMinify: false,
    manifest: true,
    rollupOptions: {
      input: {
        immersive: resolve(import.meta.dirname, 'immersive.html'),
        lyricCapsule: resolve(import.meta.dirname, 'lyric-capsule.html'),
      },
    },
  },
})
