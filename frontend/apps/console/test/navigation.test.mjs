import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const src = join(here, '..', 'src');

/**
 * Every page is reachable from the navigation, and every link goes somewhere.
 *
 * The rail renders `NAV_GROUPS` and nothing else, so a route added to the router
 * and forgotten here is a page that exists, builds, typechecks, has tests, and
 * cannot be found by anybody who does not already know its URL. That is not a
 * hypothetical: the bar this replaced was a hand-written list of fifteen
 * `<Link>`s in the layout, where the same mistake would have looked like nothing
 * at all.
 *
 * Both directions are checked, because the opposite mistake — a rail entry
 * pointing at a route that was renamed — is a 404 the type system does not catch
 * either, `NavItem.path` being a plain string.
 */

/** Route paths declared in the router, minus the ones that need a parameter. */
function routePaths(source) {
  return [...source.matchAll(/path:\s*'([^']+)'/g)]
    .map((match) => match[1])
    .filter((path) => !path.includes('$'));
}

/** Paths listed in the navigation config. */
function navPaths(source) {
  return [...source.matchAll(/path:\s*'([^']+)'/g)].map((match) => match[1]);
}

const routes = routePaths(readFileSync(join(src, 'router.tsx'), 'utf8'));
const nav = navPaths(readFileSync(join(src, 'components', 'navigation.ts'), 'utf8'));

test('the parsers still find something to compare', () => {
  // Both sides are read out of source text, so the failure that matters is a
  // regex quietly matching nothing and every assertion below passing on two
  // empty lists.
  assert.ok(routes.length >= 10, `only ${routes.length} routes parsed from router.tsx`);
  assert.ok(nav.length >= 10, `only ${nav.length} entries parsed from navigation.ts`);
});

test('every route is reachable from the navigation', () => {
  const missing = routes.filter((path) => !nav.includes(path));

  assert.deepEqual(
    missing,
    [],
    `these routes exist but appear nowhere in the rail, so nothing links to them: ${missing.join(', ')}`,
  );
});

test('every navigation entry points at a real route', () => {
  const dangling = nav.filter((path) => !routes.includes(path));

  assert.deepEqual(
    dangling,
    [],
    `the rail links to paths the router does not declare: ${dangling.join(', ')}`,
  );
});

test('no destination is listed in two groups', () => {
  const duplicated = nav.filter((path, index) => nav.indexOf(path) !== index);

  assert.deepEqual(duplicated, [], `listed more than once in the rail: ${duplicated.join(', ')}`);
});

test('the rail breakpoint agrees with the token the lg: variant is built from', () => {
  // Two places have to know where the rail replaces the drawer: the class names,
  // via Tailwind's `lg:`, and the layout, which closes the drawer when a rotating
  // tablet crosses it. The layout used to read the token at runtime; under Vite
  // the stylesheet is injected by JavaScript and that read came back empty often
  // enough to leave the listener unregistered. So it is a constant, and this is
  // what stops the constant drifting away from the stylesheet.
  const theme = readFileSync(
    join(here, '..', '..', '..', 'packages', 'ui', 'src', 'theme.css'),
    'utf8',
  );
  const declared = theme.match(/--breakpoint-lg:\s*([^;]+);/)?.[1]?.trim();

  assert.ok(declared, '--breakpoint-lg is no longer declared in theme.css');

  const constant = readFileSync(join(src, 'sidebar.ts'), 'utf8').match(
    /RAIL_BREAKPOINT\s*=\s*'([^']+)'/,
  )?.[1];

  assert.equal(
    constant,
    declared,
    `RAIL_BREAKPOINT is ${constant} but --breakpoint-lg is ${declared}`,
  );
});
