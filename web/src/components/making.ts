/** A picture or a piece of sound the page has the server cut, and how far that is. */
export type Making =
  | { kind: 'waiting' }
  | { kind: 'ready'; url: string }
  | { kind: 'failed'; message: string }
  /** Nothing can be cut, and why. */
  | { kind: 'none'; message: string };
