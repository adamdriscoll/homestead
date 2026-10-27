import { execFileSync, spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import assert from 'node:assert/strict';

// Exercise real Linux ownership with the production image, independently of host permissions.
const image = process.env.HOMESTEAD_SMOKE_IMAGE ?? 'homestead-smoke';
const prefix = `homestead-permissions-${randomBytes(5).toString('hex')}`;
const secret = randomBytes(32).toString('hex');
const volumes = [];
const containers = [];
function docker(args) {
  return execFileSync('docker', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}
function logs(name) {
  const result = spawnSync('docker', ['logs', name], { encoding: 'utf8' });
  assert.equal(result.status, 0);
  return result.stdout + result.stderr;
}
function start(suffix, prepare) {
  const name = `${prefix}-${suffix}`;
  docker(['volume', 'create', name]);
  volumes.push(name);
  const mount = `type=volume,source=${name},target=/var/data`;
  docker(['run', '--rm', '--user', 'root', '--entrypoint', 'sh', '--mount', mount, image, '-c', prepare]);
  docker(['create', '--name', name, '--ulimit', 'core=0', '--mount', mount, '-p', '127.0.0.1::8080',
    '-e', 'HOMESTEAD_DATA_PATH=/var/data', '-e', 'HOMESTEAD_BASE_URL=https://example.test',
    '-e', `HOMESTEAD_SETUP_TOKEN=${secret}`, image]);
  containers.push(name);
  docker(['start', name]);
  return name;
}
async function ready(name) {
  const port = docker(['port', name, '8080/tcp']).split(':').at(-1);
  for (let attempt = 0; attempt < 60; attempt++) {
    assert.equal(docker(['inspect', '--format', '{{.State.Running}}', name]), 'true',
      `Startup failed: ${logs(name)}`);
    try {
      const response = await fetch(`http://127.0.0.1:${port}/health/ready`, { signal: AbortSignal.timeout(2000) });
      if (response.status === 200) return;
    } catch { /* starting */ }
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  assert.fail(`Readiness failed: ${logs(name)}`);
}
async function rejected(name, path) {
  for (let attempt = 0; attempt < 60; attempt++) {
    if (docker(['inspect', '--format', '{{.State.Running}}', name]) === 'false') {
      assert.notEqual(docker(['inspect', '--format', '{{.State.ExitCode}}', name]), '0');
      assert.ok(logs(name).includes(`Access to the path '${path}' is denied.`));
      return;
    }
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  assert.fail('Inaccessible private storage must prevent startup.');
}
try {
  // The app can write here, but only root can chmod this platform-managed mount.
  const writable = start('writable', 'chmod 1777 /var/data');
  await ready(writable);
  const modes = docker(['exec', writable, 'stat', '-c', '%a', '/var/data', '/var/data/keys',
    '/var/data/themes', '/var/data/media/originals', '/var/data/site/builds', '/var/data/backups']);
  assert.deepEqual(modes.split(/\r?\n/), ['1777', '700', '700', '700', '700', '700']);
  docker(['exec', writable, 'sh', '-c', 'printf preserved > /var/data/marker']);
  docker(['restart', writable]);
  await ready(writable);
  assert.equal(docker(['exec', writable, 'cat', '/var/data/marker']), 'preserved');
  assert.equal(docker(['exec', writable, 'stat', '-c', '%a', '/var/data/keys']), '700');

  const owned = start('owned', 'chown app:app /var/data; chmod 777 /var/data');
  await ready(owned);
  assert.equal(docker(['exec', owned, 'stat', '-c', '%a', '/var/data']), '700');

  const unwritable = start('unwritable', 'chmod 555 /var/data');
  await rejected(unwritable, '/var/data/keys');
  const privateDirectory = start('private', 'chmod 1777 /var/data; mkdir /var/data/keys; chmod 1777 /var/data/keys');
  await rejected(privateDirectory, '/var/data/keys');
  console.log('Storage permissions passed: managed mount, restart, private modes, and inaccessible storage.');
} finally {
  for (const name of containers) docker(['rm', '-f', name]);
  for (const name of volumes) docker(['volume', 'rm', name]);
}
