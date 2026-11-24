/** A picture or a piece of sound the page has the server cut, and how far that is. */
export type Making =
  | { kind: 'waiting' }
  | { kind: 'ready'; url: string }
  | { kind: 'failed'; message: string }
  /** Nothing can be cut, and why. */
  | { kind: 'none'; message: string };

/** Where sending the chosen lines to Anki is. */
export type Sending =
  | { kind: 'idle' }
  | { kind: 'busy'; text: string }
  | { kind: 'done'; text: string; canUndo: boolean }
  | { kind: 'failed'; text: string };
