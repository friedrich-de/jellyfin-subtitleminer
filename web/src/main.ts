import './styles/fonts.css';
import './styles/base.css';

import { createApp } from 'vue';

import App from './App.vue';

createApp(App, {
  env: {
    storage: window.localStorage,
    fetch: window.fetch.bind(window),
    base: document.baseURI,
    userAgent: navigator.userAgent,
    version: __APP_VERSION__,
  },
  events: window,
}).mount('#app');
