<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted } from 'vue';

import { jellyfinUrl } from '@/api/addresses';
import { createSessionStore } from '@/auth/sessionStore';
import type { AuthEnv } from '@/auth/signIn';
import SignInForm from '@/components/SignInForm.vue';

const props = defineProps<{
  env: AuthEnv;
  /** What tells the page that another tab changed the stored login. The window, outside tests. */
  events: Pick<Window, 'addEventListener' | 'removeEventListener'>;
}>();

const store = createSessionStore(props.env, props.events);
const state = store.state;
const jellyfinAddress = computed(() => jellyfinUrl(props.env.base, 'web/'));

onMounted(() => void store.start());
onBeforeUnmount(() => store.stop());
</script>

<template>
  <div class="app">
    <header class="bar">
      <span class="brand">Subtitle Miner</span>
      <template v-if="state.kind === 'signed-in'">
        <span class="who muted" data-testid="signed-in-as">
          Signed in as {{ state.session.status.UserName }}
        </span>
        <button
          v-if="state.session.source === 'page'"
          class="button"
          type="button"
          @click="store.signOut()"
        >
          Sign out
        </button>
      </template>
    </header>

    <main class="main">
      <p v-if="state.kind === 'loading'" class="note muted" role="status">Signing in…</p>

      <SignInForm
        v-else-if="state.kind === 'signed-out'"
        :busy="state.busy"
        :error="state.error"
        :jellyfin-address="jellyfinAddress"
        @submit="(name, password) => store.signIn(name, password)"
      />

      <section v-else-if="state.kind === 'signed-in'" class="note" data-testid="status">
        <h1>Nothing to show yet</h1>
        <p class="muted">
          Version 2 is still being built. Following what is playing and making Anki cards come next.
        </p>
        <p class="muted" data-testid="versions">
          Subtitle Miner {{ state.session.status.Version }} on
          {{ state.session.status.ServerName }}, Jellyfin {{ state.session.status.ServerVersion }}
        </p>
      </section>

      <section v-else class="note" role="alert">
        <h1>
          {{
            state.kind === 'unreachable'
              ? 'The server could not be reached'
              : 'Something went wrong'
          }}
        </h1>
        <p v-if="state.kind === 'failed'" class="muted">{{ state.message }}</p>
        <p>
          <button class="button primary" type="button" @click="store.retry()">Try again</button>
        </p>
      </section>
    </main>
  </div>
</template>

<style scoped>
.app {
  display: flex;
  flex-direction: column;
  min-height: 100%;
}

.bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 16px;
  min-height: 64px;
  padding: 10px max(16px, env(safe-area-inset-right)) 10px max(16px, env(safe-area-inset-left));
  padding-top: max(10px, env(safe-area-inset-top));
  background: var(--surface);
}

.brand {
  flex: 1 1 auto;
  font-size: 18px;
  font-weight: 600;
  color: var(--text-strong);
}

.who {
  font-size: 13px;
}

.main {
  flex: 1 1 auto;
  display: flex;
  flex-direction: column;
}

.note {
  display: flex;
  flex-direction: column;
  gap: 12px;
  width: 100%;
  max-width: 560px;
  margin: 0 auto;
  padding: 32px 16px;
}

.note h1 {
  font-size: 24px;
  font-weight: 600;
  color: var(--text-strong);
}
</style>
