import { describe, expect, it } from 'vitest';

import { eventStream } from '@/testing/fakes';

import { EventParser, readEvents, type ServerEvent } from './eventStream';

describe('the reader of server-sent events', () => {
  it('takes events apart, whatever pieces they arrive in', () => {
    const parser = new EventParser();
    const text = 'event: watches\ndata: {"a":1}\n\n: beat\n\ndata: plain\ndata: two\n\n';
    const events: ServerEvent[] = [];
    for (const letter of text) events.push(...parser.push(letter));

    expect(events).toEqual([
      { name: 'watches', data: '{"a":1}' },
      { name: 'message', data: 'plain\ntwo' },
    ]);
  });

  it('reads every kind of line end, also one split between two pieces', () => {
    const parser = new EventParser();

    expect(parser.push('event: a\r\ndata: 1\r')).toEqual([]);
    expect(parser.push('\n\r\n')).toEqual([{ name: 'a', data: '1' }]);
    expect(parser.push('data:2\r\r\n')).toEqual([{ name: 'message', data: '2' }]);
  });

  it('skips comments and events without data, and keeps a colon inside the data', () => {
    const parser = new EventParser();

    expect(parser.push(': beat\n\nevent: empty\n\ndata: a: b\n\n')).toEqual([
      { name: 'message', data: 'a: b' },
    ]);
  });

  it('reads a stream to its end and says when anything arrived', async () => {
    const stream = eventStream();
    const events: ServerEvent[] = [];
    let arrivals = 0;
    const reading = readEvents(
      stream.response.body!,
      (event) => events.push(event),
      () => (arrivals += 1),
    );

    stream.send([]);
    stream.write(': beat\n\n');
    stream.close();
    await reading;

    expect(events).toEqual([{ name: 'watches', data: '{"Watches":[]}' }]);
    expect(arrivals).toBe(2);
  });

  it('decodes letters whose bytes arrive in two pieces', () => {
    const parser = new EventParser();
    const decoder = new TextDecoder();
    const bytes = new TextEncoder().encode('data: 昨日\n\n');

    const first = parser.push(decoder.decode(bytes.slice(0, 8), { stream: true }));
    const second = parser.push(decoder.decode(bytes.slice(8), { stream: true }));

    expect([...first, ...second]).toEqual([{ name: 'message', data: '昨日' }]);
  });
});
