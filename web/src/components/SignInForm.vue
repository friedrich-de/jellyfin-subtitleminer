<script setup lang="ts">
import { ref } from 'vue';

defineProps<{
  busy: boolean;
  error: string | null;
  /** Where Jellyfin's own web client is, for the link. */
  jellyfinAddress: string;
}>();

const emit = defineEmits<{ submit: [name: string, password: string] }>();

const name = ref('');
const password = ref('');

function submit(): void {
  if (name.value.trim() === '') return;
  emit('submit', name.value.trim(), password.value);
}
</script>

<template>
  <section class="sign-in" aria-labelledby="sign-in-title">
    <h1 id="sign-in-title">Sign in</h1>
    <p class="muted">
      Use your Jellyfin name and password. If you sign in to Jellyfin in this browser, this page
      signs in by itself.
    </p>

    <form novalidate @submit.prevent="submit">
      <label for="sign-in-name">Name</label>
      <input
        id="sign-in-name"
        v-model="name"
        type="text"
        autocomplete="username"
        autocapitalize="none"
        spellcheck="false"
        required
        :disabled="busy"
      />

      <label for="sign-in-password">Password</label>
      <input
        id="sign-in-password"
        v-model="password"
        type="password"
        autocomplete="current-password"
        :disabled="busy"
      />

      <p v-if="error" class="error" role="alert">{{ error }}</p>

      <div class="actions">
        <button class="button primary" type="submit" :disabled="busy">
          {{ busy ? 'Signing in…' : 'Sign in' }}
        </button>
        <!-- Beside the page, also in the installed app: the page goes on by itself once Jellyfin has
             the login. -->
        <a class="button" :href="jellyfinAddress" target="_blank" rel="noopener">Open Jellyfin</a>
      </div>
    </form>
  </section>
</template>

<style scoped>
.sign-in {
  display: flex;
  flex-direction: column;
  gap: 16px;
  width: 100%;
  max-width: 400px;
  margin: 0 auto;
  padding: 32px 16px;
}

h1 {
  font-size: 24px;
  font-weight: 600;
  color: var(--text-strong);
}

form {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

label {
  margin-top: 8px;
  font-size: 13px;
  color: var(--text-muted);
}

input {
  min-height: 44px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--surface);
  color: var(--text-strong);
}

input:focus-visible {
  outline-offset: 0;
  border-color: var(--accent);
}

.error {
  color: var(--danger);
}

.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 16px;
}
</style>
