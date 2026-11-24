/** A box on the screen, as getBoundingClientRect gives it. */
export interface Box {
  left: number;
  top: number;
  right: number;
  bottom: number;
}

interface Caret {
  node: Node;
  offset: number;
}

function caretAt(x: number, y: number): Caret | undefined {
  const position = document.caretPositionFromPoint?.(x, y);
  if (position !== null && position !== undefined) {
    return { node: position.offsetNode, offset: position.offset };
  }
  // Safari before 26 has only the older one.
  const range = (
    document as Document & { caretRangeFromPoint?: (x: number, y: number) => Range | null }
  ).caretRangeFromPoint?.(x, y);
  return range === null || range === undefined
    ? undefined
    : { node: range.startContainer, offset: range.startOffset };
}

function boxOf(node: Text, offset: number): DOMRect | undefined {
  const range = document.createRange();
  range.setStart(node, offset);
  range.setEnd(node, offset + 1);
  return range.getClientRects()[0];
}

function holds(box: DOMRect | undefined, x: number, y: number): box is DOMRect {
  return box !== undefined && x >= box.left && x <= box.right && y >= box.top && y <= box.bottom;
}

/**
 * The character of an element's text at a point: its place in the whole text and its box. A
 * point between characters, or beside the text, has none.
 */
export function characterAt(
  element: Element,
  x: number,
  y: number,
): { offset: number; box: Box } | undefined {
  const caret = caretAt(x, y);
  if (caret === undefined || !(caret.node instanceof Text) || !element.contains(caret.node)) {
    return undefined;
  }
  const { node } = caret;
  // The caret falls between characters: a tap on the right half of one is still that one.
  let offset: number | undefined;
  let box: DOMRect | undefined;
  for (const candidate of [caret.offset, caret.offset - 1]) {
    if (candidate < 0 || candidate >= node.data.length) continue;
    const found = boxOf(node, candidate);
    if (holds(found, x, y)) {
      [offset, box] = [candidate, found];
      break;
    }
  }
  if (offset === undefined || box === undefined) return undefined;

  let before = 0;
  const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
  for (let text = walker.nextNode(); text !== null && text !== node; text = walker.nextNode()) {
    before += (text as Text).data.length;
  }
  return {
    offset: before + offset,
    box: { left: box.left, top: box.top, right: box.right, bottom: box.bottom },
  };
}

/** Whether a word can start at a character: not at a space, a mark or a symbol. */
export function startsWord(character: string | undefined): boolean {
  return character !== undefined && character !== '' && !/^[\s\p{P}\p{S}]/u.test(character);
}
