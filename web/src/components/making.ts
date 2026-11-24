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

/** The card "Add to my newest card" would change, or why there is none. */
export type Newest =
  | { kind: 'looking' }
  | { kind: 'found'; label: string; minutes: number }
  /** No card fits: the button is off, and this says why. */
  | { kind: 'none'; text: string }
  /** Anki could not be asked: the button stays, and sending says what is wrong. */
  | { kind: 'failed'; text: string };
