<script setup lang="ts">
import { ref, useTemplateRef, watch } from 'vue';

import type { Making } from './making';

const props = defineProps<{
  count: number;
  /** "00:04 to 00:09, 5.0 seconds" */
  span: string;
  picture: Making;
  sound: Making;
}>();

const emit = defineEmits<{ clear: [] }>();

const player = useTemplateRef<HTMLAudioElement>('player');
const playing = ref(false);

watch(
  () => props.sound,
  () => {
    playing.value = false;
  },
);

async function play(): Promise<void> {
  const audio = player.value;
  if (audio === null) return;
  if (playing.value) {
    audio.pause();
    audio.currentTime = 0;
    playing.value = false;
    return;
  }

  audio.currentTime = 0;
  try {
    playing.value = true;
    await audio.play();
  } catch {
    playing.value = false;
  }
}
</script>

<template>
  <section class="selection" aria-label="Selected lines">
    <div class="summary">
      <p class="count" data-testid="selected-count" aria-live="polite">
        {{ count === 1 ? '1 line selected' : `${count} lines selected` }}
      </p>
      <p class="muted span">{{ span }}</p>
      <button class="link" type="button" @click="emit('clear')">Clear</button>
    </div>

    <div class="preview">
      <div class="picture" data-testid="preview-picture">
        <img v-if="picture.kind === 'ready'" :src="picture.url" alt="The picture for the card" />
        <span v-else-if="picture.kind === 'waiting'" class="muted note">Making the picture…</span>
        <span v-else class="muted note">{{ picture.message }}</span>
      </div>

      <div class="sound">
        <button
          class="button"
          type="button"
          data-testid="play-sound"
          :disabled="sound.kind !== 'ready'"
          @click="play"
        >
          <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
            <path v-if="playing" fill="currentColor" d="M6 6h12v12H6z" />
            <path v-else fill="currentColor" d="M8 5v14l11-7z" />
          </svg>
          {{
            sound.kind === 'waiting' ? 'Cutting the sound…' : playing ? 'Stop' : 'Play the sound'
          }}
        </button>
        <p v-if="sound.kind === 'failed' || sound.kind === 'none'" class="muted note">
          {{ sound.message }}
        </p>
        <audio
          v-if="sound.kind === 'ready'"
          ref="player"
          :src="sound.url"
          preload="auto"
          data-testid="sound"
          @ended="playing = false"
          @pause="playing = false"
        />
      </div>
    </div>
  </section>
</template>

<style scoped>
.selection {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 12px 24px;
  width: 100%;
  max-width: 880px;
  margin: 0 auto;
}

.summary {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 4px 12px;
  flex: 1 1 220px;
}

.count {
  font-weight: 600;
  color: var(--text-strong);
}

.span {
  font-size: 13px;
  font-variant-numeric: tabular-nums;
}

.link {
  padding: 8px 0;
  border: 0;
  background: none;
  color: var(--text);
  text-decoration: underline;
  cursor: pointer;
}

.preview {
  display: flex;
  align-items: center;
  gap: 12px;
}

.picture {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 128px;
  height: 72px;
  overflow: hidden;
  border-radius: var(--radius);
  background: var(--raised);
}

.picture img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.note {
  padding: 4px;
  font-size: 12px;
  text-align: center;
}

.sound {
  display: flex;
  flex-direction: column;
  gap: 4px;
}
</style>
