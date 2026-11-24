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

// The worker keeps the page's files so the installed app opens without the server. Only the
// build has one: during development the page comes from Vite.
if (import.meta.env.PROD && 'serviceWorker' in navigator) {
  navigator.serviceWorker.register('./sw.js', { scope: './' }).catch(() => {
    // The page works the same without it.
  });
}
