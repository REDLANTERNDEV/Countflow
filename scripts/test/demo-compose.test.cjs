const { test } = require('node:test');
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const { resolve } = require('node:path');

function configuration(encryptionKey = Buffer.alloc(32, 7).toString('base64'), dokploy = false) {
  // Synthetic process settings only: these tests never read credential files.
  return spawnSync('docker', ['compose', '--env-file', '/dev/null', ...(dokploy ? ['-f', 'compose.dokploy.yaml'] : ['-f', 'compose.yaml', '-f', 'compose.demo.yaml']), 'config', '--format', 'json'], {
    cwd: resolve(__dirname, '../..'), encoding: 'utf8',
    env: { ...process.env, POSTGRES_PASSWORD: 'synthetic-database-secret', JWT_SIGNING_KEY: 'synthetic-signing-key-for-compose-tests',
      SEQ_ADMIN_PASSWORD: 'synthetic-log-secret', DEMO_PASSWORD: 'synthetic-public-password',
      DEMO_ADMIN_API_KEY: 'cfk_synthetic-admin', DEMO_SUPERVISOR_API_KEY: 'cfk_synthetic-supervisor', DEMO_OPERATOR_API_KEY: 'cfk_synthetic-operator',
      CADDY_SITE_ADDRESS: 'demo.example.invalid', WEBHOOKS_ENCRYPTION_KEY: encryptionKey },
  });
}

test('Dokploy routes the web service internally without host port conflicts', () => {
  const result = configuration(undefined, true);
  assert.equal(result.status, 0, result.stderr);
  const config = JSON.parse(result.stdout);
  for (const service of Object.values(config.services)) assert.equal(service.ports?.length ?? 0, 0);
  assert.equal(config.services.web.environment.CADDY_SITE_ADDRESS, ':80');
  assert.ok(config.services.web.expose.includes('80'));
});

test('demo requires its dedicated webhook encryption key before startup', () => {
  const result = configuration('');
  assert.notEqual(result.status, 0, 'missing key must refuse the demo stack');
  assert.match(result.stderr, /WEBHOOKS_ENCRYPTION_KEY/);
});

test('demo isolates data and publishes only the TLS gateway', () => {
  const result = configuration(); assert.equal(result.status, 0, result.stderr);
  const config = JSON.parse(result.stdout);
  assert.equal(config.name, 'countflow-demo');
  for (const name of ['postgres', 'api', 'seq', 'migrator', 'simulator']) assert.equal(config.services[name].ports?.length ?? 0, 0, `${name} must stay private`);
  assert.deepEqual(config.services.web.ports.map(port => Number(port.published)).sort((a, b) => a - b), [80, 443]);
  assert.equal(config.services.api.environment.ASPNETCORE_ENVIRONMENT, 'Production');
  assert.equal(Buffer.from(config.services.api.environment.Webhooks__EncryptionKey, 'base64').length, 32);
  assert.equal(config.services.migrator.environment.Seed__DemoUsers, 'true');
  assert.equal(config.services.migrator.environment.Seed__DemoData, 'true');
  for (const volume of Object.values(config.volumes)) assert.ok(volume.name.startsWith('countflow-demo_'));
  assert.ok(config.services.web.volumes.some(volume => volume.target === '/data'), 'TLS state is persistent');
});

test('Dokploy file is independently deployable from its Compose Path', () => {
  const merged = configuration(); assert.equal(merged.status, 0, merged.stderr);
  const baseline = JSON.parse(merged.stdout);
  delete baseline.services.web.ports;
  baseline.services.web.expose = ['80'];
  baseline.services.web.environment.CADDY_SITE_ADDRESS = ':80';
  const result = configuration(undefined, true);
  assert.equal(result.status, 0, result.stderr); const config = JSON.parse(result.stdout);
  assert.deepEqual(Object.keys(config.services).sort(), Object.keys(baseline.services).sort());
  for (const name of Object.keys(config.services)) {
    assert.deepEqual(config.services[name], baseline.services[name], `${name} must retain the demo contract`);
  }
});
