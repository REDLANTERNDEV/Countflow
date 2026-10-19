import { spawnSync } from 'node:child_process';
import { cp, mkdir, rm, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const output = join(root, 'dist');

// Resolve tools from each workspace, including pnpm's isolated node_modules.
// Keep this independent of shell syntax so the same command works on Windows.
for (const app of ['console', 'handheld']) {
  const cwd = join(root, 'apps', app);
  const require = createRequire(join(cwd, 'package.json'));
  for (const [pkg, binary, args] of [
    ['typescript', 'bin/tsc', ['--noEmit']],
    ['vite', 'bin/vite.js', ['build']],
  ]) {
    const tool = join(dirname(require.resolve(`${pkg}/package.json`)), binary);
    const result = spawnSync(process.execPath, [tool, ...args], {
      cwd,
      env: { ...process.env, VITE_DEMO_MODE: 'true' },
      stdio: 'inherit',
    });
    if (result.error) throw result.error;
    if (result.status !== 0) process.exit(result.status ?? 1);
  }
}

// Assemble only after both builds succeed. This directory is generated output.
await rm(output, { recursive: true, force: true });
await mkdir(output, { recursive: true });
await cp(join(root, 'apps/console/dist'), output, { recursive: true });
await cp(join(root, 'apps/handheld/dist'), join(output, 'handheld'), { recursive: true });
await mkdir(join(output, 'health'), { recursive: true });
await writeFile(join(output, 'health/ready.json'), '{"status":"Healthy"}\n');
await writeFile(join(output, '404.html'), '<!doctype html><html lang="en"><meta charset="utf-8"><title>Not found</title><h1>Not found</h1></html>\n');
console.log(`Static demo ready: ${output}`);
