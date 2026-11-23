import { readonly, shallowRef } from 'vue';

import { WEB_CLIENT_KEY } from './credentials';
import {
  type AuthEnv,
  resolveSession,
  type Session,
  signInWithPassword,
  signOut as endSession,
} from './signIn';

export type SessionState =
  | { kind: 'loading' }
  | { kind: 'signed-in'; session: Session }
  | { kind: 'signed-out'; busy: boolean; error: string | null }
  | { kind: 'unreachable' }
  | { kind: 'failed'; message: string };

/** The page's login, and what changes it. */
export function createSessionStore(
  env: AuthEnv,
  target: Pick<Window, 'addEventListener' | 'removeEventListener'>,
) {
  const state = shallowRef<SessionState>({ kind: 'loading' });
  let run = 0;

  async function resolve(showLoading: boolean): Promise<void> {
    const mine = ++run;
    if (showLoading) state.value = { kind: 'loading' };
    const result = await resolveSession(env);
    if (mine !== run) return;
    state.value =
      result.kind === 'signed-out' ? { kind: 'signed-out', busy: false, error: null } : result;
  }

  // Jellyfin writes its login into this browser's storage when someone signs in or out, in any tab.
  // The page follows without being opened again.
  function onStorage(event: StorageEvent): void {
    if (event.key !== null && event.key !== WEB_CLIENT_KEY) return;
    const now = state.value;
    const waiting = now.kind === 'signed-out' && !now.busy;
    const borrowed = now.kind === 'signed-in' && now.session.source === 'jellyfin';
    if (waiting || borrowed) void resolve(false);
  }

  return {
    state: readonly(state),

    async start(): Promise<void> {
      target.addEventListener('storage', onStorage);
      await resolve(true);
    },

    stop(): void {
      target.removeEventListener('storage', onStorage);
      run++;
    },

    retry: () => resolve(true),

    async signIn(name: string, password: string): Promise<void> {
      const mine = ++run;
      state.value = { kind: 'signed-out', busy: true, error: null };
      const result = await signInWithPassword(env, name, password);
      if (mine !== run) return;
      state.value =
        result.kind === 'refused'
          ? { kind: 'signed-out', busy: false, error: result.message }
          : result.kind === 'signed-out'
            ? { kind: 'signed-out', busy: false, error: null }
            : result;
    },

    async signOut(): Promise<void> {
      const now = state.value;
      if (now.kind !== 'signed-in') return;
      await endSession(env, now.session);
      await resolve(false);
    },
  };
}

export type SessionStore = ReturnType<typeof createSessionStore>;
