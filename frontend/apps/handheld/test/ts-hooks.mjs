/**
 * Lets `node --test` load the app's TypeScript sources unchanged.
 *
 * Node 24 strips types from a `.ts` file on its own; what it will not do is
 * follow the `./foo.js` specifiers the codebase writes for `./foo.ts`. Rewriting
 * the extension here is cheaper than either changing the convention in the
 * source to suit a test runner, or standing up a bundler-shaped test setup for
 * three files of logic.
 */
export async function resolve(specifier, context, next) {
  if (specifier.startsWith('.') && specifier.endsWith('.js')) {
    try {
      return await next(`${specifier.slice(0, -3)}.ts`, context);
    } catch {
      // Fall through: it really was a .js file.
    }
  }

  return next(specifier, context);
}
