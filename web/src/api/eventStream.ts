/**
 * Server-sent events, read from an answer the page fetched itself. The browser's own EventSource
 * cannot send a login in a header, and a login in the address would end up in logs.
 */

/** One event of the stream. */
export interface ServerEvent {
  /** The event's name. "message" when the server gave none. */
  name: string;
  data: string;
}

/** Puts the pieces of a stream together and takes the events out of them. */
export class EventParser {
  private rest = '';
  private name = '';
  private data: string[] = [];

  /** Takes the next piece of text and returns the events it completes. */
  push(text: string): ServerEvent[] {
    const events: ServerEvent[] = [];
    this.rest += text;
    for (;;) {
      const end = /\r\n|\n|\r/.exec(this.rest);

      // A carriage return at the very end may be the first half of a line end.
      if (end === null || (end[0] === '\r' && end.index === this.rest.length - 1)) break;
      const row = this.rest.slice(0, end.index);
      this.rest = this.rest.slice(end.index + end[0].length);

      if (row === '') {
        // An empty row ends an event. Without data there is none.
        if (this.data.length > 0) {
          events.push({
            name: this.name === '' ? 'message' : this.name,
            data: this.data.join('\n'),
          });
        }
        this.name = '';
        this.data = [];
        continue;
      }

      // A row that starts with a colon is a comment: the server's sign of life.
      if (row.startsWith(':')) continue;

      const colon = row.indexOf(':');
      const field = colon === -1 ? row : row.slice(0, colon);
      let value = colon === -1 ? '' : row.slice(colon + 1);
      if (value.startsWith(' ')) value = value.slice(1);
      if (field === 'event') this.name = value;
      else if (field === 'data') this.data.push(value);
    }

    return events;
  }
}

/**
 * Reads a stream of events until the server ends it. Rejects when the connection breaks or the
 * request is aborted.
 *
 * @param onBytes Called whenever anything arrives, an event or only a sign of life.
 */
export async function readEvents(
  body: ReadableStream<Uint8Array>,
  onEvent: (event: ServerEvent) => void,
  onBytes?: () => void,
): Promise<void> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  const parser = new EventParser();
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) return;
      onBytes?.();
      for (const event of parser.push(decoder.decode(value, { stream: true }))) onEvent(event);
    }
  } finally {
    reader.releaseLock();
  }
}
