// Development-only component fixture; not an entry in the release build.
import { createApp } from 'vue'
import Preview from './ui-preview.vue'
import { installOFPlayerApp } from '../src/app/ofplayerApp'
import '../src/style.css'
import '../src/styles/tokens/of-color-tokens.css'
import '../src/styles/tokens/of-theme-mapping.css'
import '../src/styles/tokens/of-component-tokens.css'
import '../src/styles/tokens/of-player-tokens.css'
import '../src/themes/mist.css'
import '../src/themes/paper.css'
import '../src/themes/material.css'

const app = createApp(Preview)
installOFPlayerApp(app, { scanProgress: {} })
app.mount('#app')
