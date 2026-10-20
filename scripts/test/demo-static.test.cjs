const { execFileSync } = require('node:child_process');
const assert = require('node:assert/strict');
const { test } = require('node:test');
const path = require('node:path');

test('static demo resolves to one bounded web service without secrets, host ports, or persistent volumes', () => {
  const config = JSON.parse(execFileSync('docker', ['compose', '-f', 'compose.demo-static.yaml', 'config', '--format', 'json'], {
    cwd: path.resolve(__dirname, '../..'), env: { ...process.env, COMPOSE_DISABLE_ENV_FILE: '1' }, encoding: 'utf8',
  }));
  assert.deepEqual(Object.keys(config.services), ['web']);
  const web = config.services.web;
  assert.equal(web.build.target, 'static-demo');
  assert.equal(web.build.args.VITE_DEMO_MODE, 'true');
  assert.equal(Number(web.mem_limit), 128 * 1024 * 1024);
  assert.ok(!web.ports?.length && !web.volumes?.length && !web.environment);
  assert.equal(web.logging.options['max-file'], '2');
});
