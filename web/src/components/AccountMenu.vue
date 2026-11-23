<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, useTemplateRef } from 'vue';

import type { Session } from '@/auth/signIn';

const props = defineProps<{ session: Session }>();
const emit = defineEmits<{ signOut: [] }>();

const open = ref(false);
const root = useTemplateRef<HTMLElement>('root');

function onPointer(event: PointerEvent): void {
  if (open.value && root.value !== null && !root.value.contains(event.target as Node)) {
    open.value = false;
  }
}

function onKey(event: KeyboardEvent): void {
  if (open.value && event.key === 'Escape') open.value = false;
}

onMounted(() => {
  document.addEventListener('pointerdown', onPointer);
  document.addEventListener('keydown', onKey);
});

onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', onPointer);
  document.removeEventListener('keydown', onKey);
});

const status = props.session.status;
</script>

<template>
  <div ref="root" class="account">
    <button
      class="button account-button"
      type="button"
      data-testid="account"
      :aria-expanded="open"
      aria-controls="account-panel"
      @click="open = !open"
    >
      <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
        <path
          fill="currentColor"
          d="M12 12a4.5 4.5 0 1 0 0-9 4.5 4.5 0 0 0 0 9Zm0 2c-4.4 0-8 2.2-8 5v2h16v-2c0-2.8-3.6-5-8-5Z"
        />
      </svg>
      <span class="name">{{ status.UserName }}</span>
    </button>

    <div v-show="open" id="account-panel" class="panel">
      <p data-testid="signed-in-as">Signed in as {{ status.UserName }}</p>
      <p class="muted small">
        {{
          session.source === 'jellyfin'
            ? 'Through Jellyfin in this browser. Sign out there to sign out here.'
            : 'With a login of this page.'
        }}
      </p>
      <p class="muted small" data-testid="versions">
        Subtitle Miner {{ status.Version }} on {{ status.ServerName }}, Jellyfin
        {{ status.ServerVersion }}
      </p>
      <button
        v-if="session.source === 'page'"
        class="button"
        type="button"
        @click="emit('signOut')"
      >
        Sign out
      </button>
    </div>
  </div>
</template>

<style scoped>
.account {
  position: relative;
}

.account-button {
  padding: 0 12px;
  background: transparent;
}

.name {
  max-width: 12em;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.panel {
  position: absolute;
  z-index: 20;
  top: calc(100% + 6px);
  right: 0;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 8px;
  width: min(320px, calc(100vw - 32px));
  padding: 16px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--surface);
  box-shadow: 0 8px 24px rgb(0 0 0 / 50%);
}

.small {
  font-size: 13px;
}
</style>
