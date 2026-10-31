import { createApp } from 'vue'
import ImmersiveWebView from './ImmersiveWebView.vue'

document.documentElement.dataset.appView = 'immersive'
createApp(ImmersiveWebView).mount('#app')
