import type { Locator, Page } from '@playwright/test';

/** Taps a character of a line's said text, as a finger or a mouse would. */
export async function tapCharacter(page: Page, line: Locator, character: string): Promise<void> {
  const said = line.locator('.said');
  const point = await said.evaluate((element, wanted) => {
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node !== null; node = walker.nextNode()) {
      const at = (node as Text).data.indexOf(wanted);
      if (at === -1) continue;
      const range = document.createRange();
      range.setStart(node, at);
      range.setEnd(node, at + 1);
      const box = range.getBoundingClientRect();
      const outer = element.getBoundingClientRect();
      return {
        x: box.left + box.width * 0.6 - outer.left,
        y: box.top + box.height / 2 - outer.top,
      };
    }
    throw new Error(`No ${wanted} in the line`);
  }, character);
  const touch = await page.evaluate(() => navigator.maxTouchPoints > 0);
  if (touch) await said.tap({ position: point });
  else await said.click({ position: point });
}
