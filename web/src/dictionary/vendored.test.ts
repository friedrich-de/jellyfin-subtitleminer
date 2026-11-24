import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';

import { describe, expect, it } from 'vitest';

// Vitest runs from the page's folder.
const ROOT = join(process.cwd(), 'src/vendor/dictionary-popup/');

function scripts(): [string, string][] {
  return readdirSync(ROOT, { recursive: true, withFileTypes: true })
    .filter((entry) => entry.isFile() && entry.name.endsWith('.js'))
    .map((entry) => {
      const path = `${entry.parentPath}/${entry.name}`;
      return [relative(ROOT, path).replace(/\\/g, '/'), readFileSync(path, 'utf8')];
    });
}

describe('the dictionary popup the page carries', () => {
  it('makes no <style> element, which the page’s content policy refuses', () => {
    const found = scripts().filter(([, code]) =>
      /createElement\(\s*["'`]style["'`]\s*\)|insertAdjacentHTML|outerHTML\s*=|\.innerHTML\s*=|setAttribute\(\s*["'`]style["'`]/.test(
        code,
      ),
    );
    expect(found.map(([path]) => path)).toEqual([]);
  });

  it('adopts its stylesheets instead, where the page changed it', () => {
    const code = Object.fromEntries(scripts());
    expect(code['index.js']).toContain('root.adoptedStyleSheets = [base, custom];');
    expect(code['hachidori/glossary.js']).toContain('host.adoptedStyleSheets = [');
  });

  it('runs no code from text and fetches nothing but the pictures it is given', () => {
    const found = scripts().filter(([, code]) =>
      /\beval\(|new Function\(|importScripts\(|XMLHttpRequest|WebSocket\(/.test(code),
    );
    expect(found.map(([path]) => path)).toEqual([]);
  });
});
