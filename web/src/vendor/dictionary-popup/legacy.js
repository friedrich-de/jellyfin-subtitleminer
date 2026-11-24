// @ts-nocheck: plain JavaScript; src/index.d.ts says what it exports.
// The popup on an older browser. Hachidori is written for a current Chrome,
// and its stylesheet uses color-mix() and oklch() (Chrome 111), light-dark()
// (123) and the unprefixed mask (120); dictionaries' own stylesheets use
// those and nested rules (120). An Android WebView that was never updated is
// older than all of them (Android 13 ships WebView 109), and without them the
// popup has no backgrounds, no icons and no tag colours.
//
// Nothing here runs on a browser that has these. On one that doesn't, the
// stylesheets are left as Hachidori wrote them wherever the browser can read
// them, and each colour it can't work out is worked out here and handed to it
// as a plain rgba().
// SPDX-License-Identifier: GPL-3.0-or-later

const NAMED = {
  transparent: [0, 0, 0, 0], black: [0, 0, 0, 1], white: [255, 255, 255, 1], red: [255, 0, 0, 1], green: [0, 128, 0, 1], blue: [0, 0, 255, 1],
  lime: [0, 255, 0, 1], yellow: [255, 255, 0, 1], orange: [255, 165, 0, 1], purple: [128, 0, 128, 1], brown: [165, 42, 42, 1],
  goldenrod: [218, 165, 32, 1], gray: [128, 128, 128, 1], grey: [128, 128, 128, 1], silver: [192, 192, 192, 1], navy: [0, 0, 128, 1],
  teal: [0, 128, 128, 1], maroon: [128, 0, 0, 1], olive: [128, 128, 0, 1], aqua: [0, 255, 255, 1], cyan: [0, 255, 255, 1],
  fuchsia: [255, 0, 255, 1], magenta: [255, 0, 255, 1], pink: [255, 192, 203, 1], gold: [255, 215, 0, 1], crimson: [220, 20, 60, 1],
  tomato: [255, 99, 71, 1], coral: [255, 127, 80, 1], salmon: [250, 128, 114, 1], indigo: [75, 0, 130, 1], violet: [238, 130, 238, 1],
  orchid: [218, 112, 214, 1], khaki: [240, 230, 140, 1], tan: [210, 180, 140, 1], beige: [245, 245, 220, 1], ivory: [255, 255, 240, 1],
  darkred: [139, 0, 0, 1], darkgreen: [0, 100, 0, 1], darkblue: [0, 0, 139, 1], darkorange: [255, 140, 0, 1], darkgray: [169, 169, 169, 1],
  darkgrey: [169, 169, 169, 1], lightgray: [211, 211, 211, 1], lightgrey: [211, 211, 211, 1], dimgray: [105, 105, 105, 1], dimgrey: [105, 105, 105, 1],
  royalblue: [65, 105, 225, 1], steelblue: [70, 130, 180, 1], skyblue: [135, 206, 235, 1], dodgerblue: [30, 144, 255, 1], firebrick: [178, 34, 34, 1],
  seagreen: [46, 139, 87, 1], forestgreen: [34, 139, 34, 1], limegreen: [50, 205, 50, 1], slategray: [112, 128, 144, 1], slategrey: [112, 128, 144, 1],
  chocolate: [210, 105, 30, 1], sienna: [160, 82, 45, 1], peru: [205, 133, 63, 1], plum: [221, 160, 221, 1], turquoise: [64, 224, 208, 1],
};

/** The functions an older browser doesn't know. */
const FUNCTIONS = /(?<![\w-])(color-mix|light-dark|oklch)\(/;

/** Where the bracket opened at `open` closes. */
function closing(text, open) {
  let depth = 0;
  for (let i = open; i < text.length; i += 1) {
    if (text[i] === "(") depth += 1;
    else if (text[i] === ")" && (depth -= 1) === 0) return i;
  }
  return -1;
}

/** `text` cut at each `separator` that isn't inside brackets. */
function split(text, separator) {
  const parts = [];
  let [depth, from] = [0, 0];
  for (let i = 0; i < text.length; i += 1) {
    if (text[i] === "(") depth += 1;
    else if (text[i] === ")") depth -= 1;
    else if (depth === 0 && text[i] === separator) {
      parts.push(text.slice(from, i));
      from = i + 1;
    }
  }
  parts.push(text.slice(from));
  return parts.map((part) => part.trim());
}

/** A percentage as a fraction: "34%", or the calc(85% * 0.15) Hachidori's gradients use. */
function fraction(text) {
  const value = text.trim();
  const product = /^calc\(\s*([\d.]+)%\s*\*\s*([\d.]+)\s*\)$/.exec(value) ?? /^calc\(\s*([\d.]+)\s*\*\s*([\d.]+)%\s*\)$/.exec(value);
  if (product) return (Number(product[1]) * Number(product[2])) / 100;
  return /^[\d.]+%$/.test(value) ? Number(value.slice(0, -1)) / 100 : null;
}

function oklch(l, c, h) {
  const [a, b] = [c * Math.cos((h * Math.PI) / 180), c * Math.sin((h * Math.PI) / 180)];
  const cube = (v) => v * v * v;
  const [L, M, S] = [cube(l + 0.3963377774 * a + 0.2158037573 * b), cube(l - 0.1055613458 * a - 0.0638541728 * b), cube(l - 0.0894841775 * a - 1.291485548 * b)];
  const linear = [4.0767416621 * L - 3.3077115913 * M + 0.2309699292 * S, -1.2684380046 * L + 2.6097574011 * M - 0.3413193965 * S, -0.0041960863 * L - 0.7034186147 * M + 1.707614701 * S];
  // Not kept to 0..255 here: a colour outside sRGB is mixed as it is, and only what comes of it is clipped.
  return linear.map((v) => 255 * Math.sign(v) * (Math.abs(v) <= 0.0031308 ? 12.92 * Math.abs(v) : 1.055 * Math.abs(v) ** (1 / 2.4) - 0.055));
}

/**
 * A CSS colour as [red, green, blue, alpha] (0 to 255, alpha 0 to 1), or null
 * when it isn't one this can work out (currentColor, a system colour, a var()
 * left in it). `scheme` is "light" or "dark", for light-dark().
 */
export function colour(text, scheme = "dark") {
  const value = String(text).trim().toLowerCase();
  if (value in NAMED) return NAMED[value];
  if (/^#[0-9a-f]{3,8}$/.test(value)) {
    const hex = value.length < 6 ? [...value.slice(1)].map((c) => c + c).join("") : value.slice(1);
    if (hex.length !== 6 && hex.length !== 8) return null;
    const part = (i) => parseInt(hex.slice(i, i + 2), 16);
    return [part(0), part(2), part(4), hex.length === 8 ? part(6) / 255 : 1];
  }
  const call = /^([\w-]+)\(/.exec(value);
  if (!call || closing(value, call[0].length - 1) !== value.length - 1) return null;
  const inside = value.slice(call[0].length, -1);
  if (call[1] === "light-dark") {
    const [light, dark] = split(inside, ",");
    return dark === undefined ? null : colour(scheme === "light" ? light : dark, scheme);
  }
  if (call[1] === "color-mix") {
    const [space, first, second] = split(inside, ",");
    if (second === undefined || !/^in\s+srgb$/.test(space)) return null;
    const read = (part) => {
      const words = split(part, " ").filter(Boolean);
      if (words.length > 1 && fraction(words[words.length - 1]) !== null) return [colour(words.slice(0, -1).join(" "), scheme), fraction(words[words.length - 1])];
      if (words.length > 1 && fraction(words[0]) !== null) return [colour(words.slice(1).join(" "), scheme), fraction(words[0])];
      return [colour(part, scheme), null];
    };
    let [[a, p], [b, q]] = [read(first), read(second)];
    if (!a || !b) return null;
    if (p === null && q === null) [p, q] = [0.5, 0.5];
    else if (p === null) p = 1 - q;
    else if (q === null) q = 1 - p;
    const sum = p + q;
    if (sum <= 0) return null;
    [p, q] = [p / sum, q / sum];
    // Mixed with each colour's own alpha weighing it, as color-mix() does.
    const alpha = a[3] * p + b[3] * q;
    const channel = (i) => (alpha === 0 ? 0 : (a[i] * a[3] * p + b[i] * b[3] * q) / alpha);
    return [channel(0), channel(1), channel(2), alpha * Math.min(1, sum)];
  }
  const numbers = inside.replace(/[,/]/g, " ").split(/\s+/).filter(Boolean);
  const number = (word, whole = 1) => (word.endsWith("%") ? (Number(word.slice(0, -1)) / 100) * whole : Number(word));
  if (numbers.some((word) => Number.isNaN(number(word.replace(/deg$/, ""))))) return null;
  const alpha = numbers[3] === undefined ? 1 : number(numbers[3]);
  if (call[1] === "rgb" || call[1] === "rgba") return numbers.length < 3 ? null : [number(numbers[0], 255), number(numbers[1], 255), number(numbers[2], 255), alpha];
  if (call[1] === "oklch") return numbers.length < 3 ? null : [...oklch(number(numbers[0]), number(numbers[1], 0.4), number(numbers[2].replace(/deg$/, ""))), alpha];
  return null;
}

/** [red, green, blue, alpha] as CSS any browser reads. */
export function rgba(c) {
  const round = (v) => Math.round(Math.min(255, Math.max(0, v)));
  return `rgba(${round(c[0])}, ${round(c[1])}, ${round(c[2])}, ${Math.round(Math.min(1, Math.max(0, c[3])) * 1000) / 1000})`;
}

/**
 * `value` with each color-mix(), light-dark() and oklch() in it worked out.
 * One that can't be is left as it is. `lookup(name)` gives a custom property's
 * value, for a var() inside one; without it they're left.
 */
export function plainColours(value, scheme = "dark", lookup = null) {
  let [out, rest] = ["", String(value)];
  for (let found = FUNCTIONS.exec(rest); found; found = FUNCTIONS.exec(rest)) {
    const end = closing(rest, found.index + found[0].length - 1);
    if (end < 0) break;
    let expression = rest.slice(found.index, end + 1);
    if (lookup) expression = withValues(expression, lookup);
    const made = colour(expression, scheme);
    out += rest.slice(0, found.index) + (made ? rgba(made) : rest.slice(found.index, end + 1));
    rest = rest.slice(end + 1);
  }
  return out + rest;
}

/** `text` with each var(--name, fallback) replaced by the property's value, or its fallback. */
function withValues(text, lookup, depth = 0) {
  let [out, rest] = ["", text];
  for (let at = rest.indexOf("var("); at >= 0 && depth < 8; at = rest.indexOf("var(")) {
    const end = closing(rest, at + 3);
    if (end < 0) break;
    const [name, ...fallback] = split(rest.slice(at + 4, end), ",");
    const value = String(lookup(name) ?? "").trim() || fallback.join(", ");
    out += rest.slice(0, at) + (value ? withValues(value, lookup, depth + 1) : rest.slice(at, end + 1));
    rest = rest.slice(end + 1);
  }
  return out + rest;
}

/** The start of the declaration or rule that the character at `index` is in. */
function startOf(css, index) {
  for (let i = index - 1; i >= 0; i -= 1) if (css[i] === ";" || css[i] === "{" || css[i] === "}") return i + 1;
  return 0;
}

/**
 * Hachidori's stylesheet for a browser without these functions. A custom
 * property may hold anything, so those declarations stay as they are. Anywhere
 * else a colour function becomes a custom property of the popup's
 * (--dictionary-popup-colour-1 …) that holds it, which `settle` then works
 * out with the rest. The unprefixed mask gets its -webkit- twin.
 */
export function olderSheet(css) {
  const text = css.replace(/\/\*[\s\S]*?\*\//g, "");
  const made = [];
  const find = new RegExp(FUNCTIONS.source, "g");
  let [out, from] = ["", 0];
  for (let found = find.exec(text); found; found = find.exec(text)) {
    const end = closing(text, found.index + found[0].length - 1);
    if (end < 0) break;
    find.lastIndex = end + 1;
    // A custom property may hold anything: its declaration stays as it is.
    if (/^\s*--[\w-]+\s*:/.test(text.slice(startOf(text, found.index), found.index))) continue;
    const expression = text.slice(found.index, end + 1);
    let index = made.indexOf(expression);
    if (index < 0) index = made.push(expression) - 1;
    out += `${text.slice(from, found.index)}var(--dictionary-popup-colour-${index + 1})`;
    from = end + 1;
  }
  out += text.slice(from);
  out = out.replace(/(?<![\w-])mask(\s*:\s*)([^;}]+)/g, "-webkit-mask$1$2; mask$1$2");
  return `${out}\n.gsm-hoshidicts-popup {\n${made.map((expression, i) => `  --dictionary-popup-colour-${i + 1}: ${expression};`).join("\n")}\n}\n`;
}

/** Every custom property a stylesheet sets. */
export function customProperties(css) {
  return [...new Set(css.match(/(?<![\w-])--[\w-]+(?=\s*:)/g) ?? [])];
}

/**
 * Works out, for the palette the popup has now, every custom property whose
 * value the browser can't: reads each as the cascade left it (its var()s
 * already filled in), and sets it on the popup itself as a plain rgba().
 * `names` is what `customProperties` found; `set` is what the last call set.
 */
export function settle(popup, names, set = []) {
  for (const name of set) popup.style.removeProperty(name);
  const computed = popup.ownerDocument.defaultView.getComputedStyle(popup);
  const scheme = computed.getPropertyValue("--hoshidicts-palette-color-scheme").trim() === "light" ? "light" : "dark";
  const changed = [];
  for (const name of names) {
    const value = computed.getPropertyValue(name);
    if (!FUNCTIONS.test(value)) continue;
    const plain = plainColours(value, scheme);
    if (plain !== value) changed.push([name, plain]);
  }
  for (const [name, plain] of changed) popup.style.setProperty(name, plain);
  return changed.map(([name]) => name);
}

/** A list of selectors, each inside `parent`: "& b" and "b" under "a" are both ":is(a) b". */
function under(parent, selectors) {
  return split(selectors, ",")
    .map((selector) => (selector.includes("&") ? selector.replace(/&/g, `:is(${parent})`) : `:is(${parent}) ${selector}`))
    .join(", ");
}

/** What's between `{` and its `}`: declarations, and rules nested among them. */
function blocks(body) {
  const items = [];
  let [depth, from, quote] = [0, 0, ""];
  for (let i = 0; i < body.length; i += 1) {
    const c = body[i];
    if (quote) {
      if (c === "\\") i += 1;
      else if (c === quote) quote = "";
    } else if (c === '"' || c === "'") quote = c;
    else if (c === "{") depth += 1;
    else if (c === "}" && (depth -= 1) === 0) {
      const open = body.indexOf("{", from);
      items.push({ head: body.slice(from, open).trim(), body: body.slice(open + 1, i) });
      from = i + 1;
    } else if (c === ";" && depth === 0) {
      const declaration = body.slice(from, i).trim();
      if (declaration) items.push({ declaration });
      from = i + 1;
    }
  }
  const last = body.slice(from).trim();
  if (last) items.push({ declaration: last });
  return items;
}

function flat(items, parent) {
  let out = "";
  const declarations = items.filter((item) => item.declaration).map((item) => `${item.declaration};`);
  if (parent && declarations.length) out += `${parent} { ${declarations.join(" ")} }\n`;
  for (const item of items) {
    if (item.declaration !== undefined) continue;
    if (/^@(media|supports|container|layer)\b/.test(item.head)) out += `${item.head} {\n${flat(blocks(item.body), parent)}}\n`;
    else if (item.head.startsWith("@")) out += `${item.head} {${item.body}}\n`;
    else out += flat(blocks(item.body), parent ? under(parent, item.head) : item.head);
  }
  return out;
}

/** A stylesheet with its nested rules written out one by one, for a browser that drops them. */
export function unnested(css) {
  return flat(blocks(css.replace(/\/\*[\s\S]*?\*\//g, "")), "");
}

/**
 * A dictionary's own stylesheet for an older browser: nested rules written
 * out, and the colours it can't work out made plain, with the popup's own
 * values for a var() inside one.
 */
export function olderDictionarySheet(css, { scheme = "dark", lookup = null, nesting = true, colours = true } = {}) {
  let out = nesting ? unnested(css) : css;
  if (colours) out = plainColours(out, scheme, lookup);
  return out;
}

/** Whether the browser keeps a rule nested in another. (CSS.supports("selector(&)") says yes before it's so.) */
function nests(window) {
  try {
    const sheet = new window.CSSStyleSheet();
    sheet.replaceSync("a { b { color: red } }");
    return sheet.cssRules[0]?.cssRules?.length === 1;
  } catch {
    return false;
  }
}

/** What this browser is missing of what the popup's stylesheets use. */
export function missing(window) {
  const supports = (...of) => {
    try {
      return window.CSS?.supports?.(...of) === true;
    } catch {
      return false;
    }
  };
  // Not a browser at all (a test's DOM): nothing to work around.
  if (typeof window.CSS?.supports !== "function") return { colours: false, nesting: false };
  return {
    colours: !supports("color", "color-mix(in srgb, red, blue)") || !supports("color", "light-dark(red, blue)") || !supports("color", "oklch(50% 0.1 0)") || !supports("mask", "none"),
    nesting: !nests(window),
  };
}
