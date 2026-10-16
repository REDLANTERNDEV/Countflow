const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const { resolve } = require('node:path');
const { spawnSync } = require('node:child_process');
function reset(t, settings = {}) {
  const root = fs.mkdtempSync(resolve(os.tmpdir(), 'countflow-demo-reset-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const log = resolve(root, 'calls.jsonl');
  fs.writeFileSync(resolve(root, 'docker'), `#!/usr/bin/env node
const fs = require('node:fs'); const args = process.argv.slice(2);
fs.appendFileSync(process.env.DEMO_TEST_LOG, JSON.stringify(args)+'\\n');
if (args[0] === 'volume' && args[1] === 'ls') console.log(process.env.DEMO_TEST_VOLUME);
if (args.includes(process.env.DEMO_TEST_FAIL_STEP)) process.exit(1);
`);
  fs.chmodSync(resolve(root, 'docker'), 0o755);
  return {
    run(...args) { return spawnSync('bash', [resolve(__dirname, '../demo-reset.sh'), ...args], { encoding: 'utf8',
      env: { ...process.env, PATH: root + ':' + process.env.PATH, DEMO_TEST_LOG: log,
        DEMO_PROJECT_NAME: 'test-demo', DEMO_TEST_VOLUME: 'test-demo_postgres-data', ...settings } }); },
    calls() { return fs.existsSync(log) ? fs.readFileSync(log, 'utf8').trim().split('\n').map(JSON.parse) : []; },
  };
}
test('reset requires explicit confirmation before touching Docker', t => {
  const demo = reset(t); assert.notEqual(demo.run().status, 0); assert.deepEqual(demo.calls(), []);
});
test('reset removes only a labelled demo database and keeps TLS volumes', t => {
  const demo = reset(t); const result = demo.run('--confirm-demo-reset', '--env-file', '/dev/null');
  assert.equal(result.status, 0, result.stderr); const calls = demo.calls();
  assert.equal(calls.length, 4);
  assert.ok(calls[0].includes('label=countflow.demo=true'));
  assert.ok(calls[0].includes('label=com.docker.compose.project=test-demo'));
  assert.ok(calls[0].includes('label=com.docker.compose.volume=postgres-data'));
  assert.deepEqual(calls[1].slice(0, 3), ['compose', '--project-name', 'test-demo']);
  assert.ok(calls[1].includes('compose.yaml')); assert.ok(calls[1].includes('compose.demo.yaml'));
  assert.ok(!calls[1].includes('compose.override.yaml'));
  assert.equal(calls[1].at(-1), 'down'); assert.ok(!calls[1].includes('-v'));
  assert.deepEqual(calls[2], ['volume', 'rm', 'test-demo_postgres-data']);
  assert.deepEqual(calls[3].slice(-3), ['up', '-d', '--build']);
});
test('reset refuses an unlabelled or ambiguous database', t => {
  for (const volume of ['', 'first\nsecond']) {
    const demo = reset(t, { DEMO_TEST_VOLUME: volume });
    assert.notEqual(demo.run('--confirm-demo-reset').status, 0); assert.equal(demo.calls().length, 1);
  }
});
test('reset never deletes database state when stopping the stack fails', t => {
  const demo = reset(t, { DEMO_TEST_FAIL_STEP: 'down' });
  assert.notEqual(demo.run('--confirm-demo-reset').status, 0);
  assert.equal(demo.calls().length, 2);
});
test('reset never discovers the development overlay through default Compose loading', t => {
  const demo = reset(t); const result = demo.run('--confirm-demo-reset'); assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(demo.calls()[1], ['compose', '--project-name', 'test-demo', '-f', 'compose.yaml', '-f', 'compose.demo.yaml', 'down']);
});
test('Dokploy reset leaves restart to Dokploy so injected domains and networks survive', t => {
  const demo = reset(t); const result = demo.run('--confirm-demo-reset', '--dokploy');
  assert.equal(result.status, 0, result.stderr); const calls = demo.calls();
  assert.equal(calls.length, 3);
  assert.deepEqual(calls[1], ['compose', '--project-name', 'test-demo', '-f', 'compose.dokploy.yaml', 'down']);
  assert.match(result.stdout, /redeploy.*Dokploy/i);
});
test('reset refuses options that could override the verified project or deployment files', t => {
  for (const args of [['-p', 'other-project'], ['--project-name', 'other-project'], ['--project-name=other-project'], ['-f', 'compose.prod.yaml'], ['--file', 'compose.prod.yaml']]) {
    const demo = reset(t); assert.notEqual(demo.run('--confirm-demo-reset', ...args).status, 0);
    assert.deepEqual(demo.calls(), []);
  }
});
