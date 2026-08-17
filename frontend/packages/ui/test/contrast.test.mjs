import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync, readdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const src = join(here, '..', 'src');

/**
 * Every filled control is legible in both themes, checked rather than reviewed.
 *
 * The design system says it in prose already — terracotta and the danger red are
 * *fill* colours, and the `-text` variants beside them are the darkened (or
 * lightened) values that reach AA **on a surface**. Prose did not stop the
 * primary button from wearing `--color-accent-text` on `--color-accent`, which is
 * 1.34:1 in light and 1.50:1 in dark: the main action in the console, unreadable
 * in both themes, for as long as the button has existed. A person had to notice.
 *
 * So the pairs are derived from the component source rather than listed here — a
 * new filled variant, or a token nudged for some other reason, is checked without
 * anybody remembering to add it. The two things this cannot see are a colour
 * written outside the token system (asserted against separately, below) and a
 * fill whose contrast is fine but whose *meaning* is wrong.
 */

/** WCAG 2.1 relative luminance. */
function luminance(hex) {
  const h = hex.replace('#', '');
  const channels = [0, 2, 4]
    .map((i) => parseInt(h.slice(i, i + 2), 16) / 255)
    .map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));

  return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
}

function contrast(a, b) {
  const [x, y] = [luminance(a), luminance(b)];
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
}

/** Pulls `--token: #hex;` declarations out of one CSS block. */
function tokensIn(css, selector) {
  const start = css.indexOf(selector);
  assert.notEqual(start, -1, `theme.css no longer contains the block "${selector}"`);

  const open = css.indexOf('{', start);
  const close = css.indexOf('}', open);
  const block = css.slice(open, close);

  return Object.fromEntries(
    [...block.matchAll(/(--[\w-]+)\s*:\s*(#[0-9a-fA-F]{6})\s*;/g)].map((m) => [m[1], m[2]]),
  );
}

const themeCss = readFileSync(join(src, 'theme.css'), 'utf8');

// Dark is light overridden by dark, which is what the cascade actually does:
// `[data-theme='dark']` redefines most tokens but anything it omits still comes
// from `:root`. Reading the dark block alone would test a theme nobody renders.
const light = tokensIn(themeCss, ":root,\n[data-theme='light']");
const dark = { ...light, ...tokensIn(themeCss, "[data-theme='dark']") };

const THEMES = { light, dark };

/**
 * Reads the filled variants out of a component.
 *
 * A variant is "filled" when it sets its background from a token: those are the
 * ones whose foreground has to be checked against something this file can know.
 * `bg-transparent` is skipped for its resting state — what sits behind a ghost
 * button is whatever surface it was placed on, which is the page's business.
 */
function filledVariants(source, file) {
  const pairs = [];

  for (const [, name, classes] of source.matchAll(
    /^\s{8}(\w+):\s*\n?\s*((?:'[^']*',?\s*)+)/gm,
  )) {
    const grab = (pattern) => classes.match(pattern)?.[1];

    const background = grab(/(?<!hover:)bg-\[var\((--[\w-]+)\)\]/);
    const foreground = grab(/(?<!hover:)text-\[var\((--[\w-]+)\)\]/);
    const hoverBackground = grab(/hover:bg-\[var\((--[\w-]+)\)\]/);
    const hoverForeground = grab(/hover:text-\[var\((--[\w-]+)\)\]/);

    // The trap that hid the unreadable danger button: a token fill under a
    // foreground written as a literal, which no token audit would ever look at.
    if (background && !foreground) {
      assert.ok(
        !/(?<!hover:)text-(white|black|\[#)/.test(classes),
        `${file}: variant "${name}" fills with ${background} but writes its ` +
          `foreground outside the token system. Give it a \`-on\` token so its ` +
          `contrast is checkable in both themes.`,
      );
    }

    if (background && foreground) {
      pairs.push({ file, name, state: 'resting', bg: background, fg: foreground });
    }

    if (hoverBackground && (hoverForeground ?? foreground)) {
      pairs.push({
        file,
        name,
        state: 'hover',
        bg: hoverBackground,
        fg: hoverForeground ?? foreground,
      });
    }
  }

  return pairs;
}

const components = readdirSync(join(src, 'components')).filter((f) => f.endsWith('.tsx'));

const pairs = components.flatMap((file) =>
  filledVariants(readFileSync(join(src, 'components', file), 'utf8'), file),
);

test('the component set still has filled variants to check', () => {
  // Guards the parser, not the palette. These regexes read a hand-written class
  // list; if the components are reformatted into a shape they no longer match,
  // every assertion below passes by finding nothing, which is the failure mode a
  // derived test has and a hardcoded list does not.
  assert.ok(
    pairs.length >= 6,
    `only ${pairs.length} filled variant/state pairs were found across ` +
      `${components.length} components — the parser has probably stopped ` +
      `matching rather than the buttons having stopped existing`,
  );

  assert.ok(
    pairs.some((p) => p.file === 'Button.tsx' && p.name === 'primary'),
    "Button.tsx's primary variant was not picked up",
  );
});

test('every filled variant reaches AA against its own fill, in both themes', () => {
  const failures = [];

  for (const { file, name, state, bg, fg } of pairs) {
    for (const [theme, tokens] of Object.entries(THEMES)) {
      const background = tokens[bg];
      const foreground = tokens[fg];

      assert.ok(background, `${bg} is not defined in the ${theme} theme`);
      assert.ok(foreground, `${fg} is not defined in the ${theme} theme`);

      const ratio = contrast(foreground, background);

      // 4.5:1 — controls carry labels at --text-sm (13px in the console) and
      // --text-xs on the small size, so the large-text allowance never applies.
      if (ratio < 4.5) {
        failures.push(
          `${file} ${name} (${state}, ${theme}): ${fg} ${foreground} on ${bg} ` +
            `${background} is ${ratio.toFixed(2)}:1, under AA's 4.5:1`,
        );
      }
    }
  }

  assert.deepEqual(failures, []);
});

test('a solid-fill control is still a visible shape against the page', () => {
  // The other half, and the reason a fill cannot simply be darkened until its
  // label passes: 3:1 is WCAG's threshold for the boundary of a control you have
  // to be able to find. Scoped to resting states whose foreground is an `-on`
  // token, which is exactly the set of "solid, deliberately loud" fills — a
  // badge's muted tint is *meant* to be a whisper (it has a border and a text
  // label), and a hover fill is a state change on a control already identified.
  // Widening this past that set would need an exemption list, and an exemption
  // list is the second set of rules that nobody keeps current.
  const failures = [];
  const solid = pairs.filter((p) => p.state === 'resting' && p.fg.endsWith('-on'));

  assert.ok(solid.length >= 2, 'no solid-fill variants were found to check');

  for (const { file, name, bg } of solid) {
    for (const [theme, tokens] of Object.entries(THEMES)) {
      const ratio = contrast(tokens[bg], tokens['--color-surface-base']);

      if (ratio < 3) {
        failures.push(
          `${file} ${name} (${theme}): fill ${bg} ${tokens[bg]} is ` +
            `${ratio.toFixed(2)}:1 against --color-surface-base ` +
            `${tokens['--color-surface-base']}, under 3:1`,
        );
      }
    }
  }

  assert.deepEqual(failures, []);
});
