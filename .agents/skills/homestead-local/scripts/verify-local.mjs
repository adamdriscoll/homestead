import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync, rmSync, existsSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { randomBytes } from 'node:crypto';
import { request } from 'node:https';
import assert from 'node:assert/strict';

// Exercise the runner in temporary environments, never the user's saved instances.
// Usage from the checkout: node <this-file> [baseline-image] [upgrade-image]
const repo = process.cwd();
const runner = resolve('.agents/skills/homestead-local/scripts/homestead-local.ps1');
const root = mkdtempSync(join(tmpdir(), 'homestead-local-verify-'));
const baseline = process.argv[2] ?? 'homestead-smoke';
const candidate = process.argv[3] ?? 'homestead:local';
const cookieJar = new Map();
const port = 5544;
function docker(...args) { return execFileSync('docker', args, { encoding: 'utf8' }).trim(); }
function run(action, environment = 'Persistent', ...extra) {
  try {
    return execFileSync('pwsh', ['-NoProfile', '-File', runner, '-RepoPath', repo, '-StateRoot', root,
      '-Environment', environment, '-Action', action, ...extra], { encoding: 'utf8', timeout: 180_000, stdio: ['ignore', 'pipe', 'pipe'] });
  } catch (error) {
    // The runner does not print secrets during any actions used by this test.
    throw new Error(`Runner ${environment}/${action} failed: ${error.stdout ?? ''}\n${error.stderr ?? ''}`);
  }
}
const state = environment => JSON.parse(readFileSync(join(root, environment.toLowerCase(), 'state.json'), 'utf8'));
function http(path, method = 'GET', fields, type = 'application/x-www-form-urlencoded') {
  return new Promise((resolveResponse, reject) => {
    const body = fields ? Buffer.isBuffer(fields) ? fields : new URLSearchParams(fields).toString() : undefined;
    const req = request({ hostname: '127.0.0.1', port, path, method, rejectUnauthorized: false, agent: false,
      headers: { Cookie: [...cookieJar].map(([key, value]) => `${key}=${value}`).join('; '),
        ...(body ? { 'Content-Type': type, 'Content-Length': Buffer.byteLength(body) } : {}) }
    }, response => {
      for (const cookie of response.headers['set-cookie'] ?? []) {
        const first = cookie.split(';')[0]; const index = first.indexOf('=');
        cookieJar.set(first.slice(0, index), first.slice(index + 1));
      }
      let text = ''; response.setEncoding('utf8'); response.on('data', chunk => text += chunk);
      response.on('end', () => resolveResponse({ status: response.statusCode, headers: response.headers, text }));
    });
    req.on('error', reject); req.setTimeout(5000, () => req.destroy(new Error('HTTP timeout'))); req.end(body);
  });
}
async function form(pagePath, postPath, fields) {
  const page = await http(pagePath); assert.equal(page.status, 200);
  const token = page.text.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/);
  assert.ok(token);
  return http(postPath, 'POST', { ...fields, __RequestVerificationToken: token[1] });
}
try {
  run('Start', 'Persistent', '-Image', baseline, '-Port', String(port), '-Subnet', '172.30.83.0/24');
  run('Start', 'Sandbox', '-Image', baseline, '-Port', '5543', '-Subnet', '172.30.82.0/24');
  const original = state('Persistent');
  assert.notEqual(state('Sandbox').Volume, original.Volume);
  const credentials = { 'Input.Token': original.SetupToken, 'Input.Email': 'upgrade@example.test',
    'Input.Password': 'Local-upgrade-verification-123!', 'Input.ConfirmPassword': 'Local-upgrade-verification-123!' };
  assert.equal((await form('/setup', '/setup', credentials)).status, 302);
  assert.equal((await form('/account/login', '/account/login', credentials)).status, 302);
  const settings = { 'Input.Title': 'Persistent upgrade verification', 'Input.CanonicalBaseUrl': original.Url,
    'Input.DisplayAuthor': 'Upgrade verifier', 'Input.TimeZone': 'America/Chicago', 'Input.AccentColor': 'Forest',
    'Input.AboutMarkdown': 'Saved through upgrades', 'Input.Description': 'Retained settings' };
  assert.equal((await form('/admin/settings', '/admin/settings', settings)).status, 302);
  const created = await form('/admin/posts', '/admin/posts?handler=New', {});
  assert.equal(created.status, 302);
  const editor = created.headers.location;
  const page = await http(editor);
  const token = page.text.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/)[1];
  const boundary = `verify-${randomBytes(10).toString('hex')}`;
  const photo = readFileSync(resolve('src/Homestead/wwwroot/icon-192.png'));
  const upload = Buffer.concat([
    Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="__RequestVerificationToken"\r\n\r\n${token}\r\n--${boundary}\r\nContent-Disposition: form-data; name="UploadAlt"\r\n\r\nRetained home icon\r\n--${boundary}\r\nContent-Disposition: form-data; name="Upload"; filename="home.png"\r\nContent-Type: image/png\r\n\r\n`),
    photo, Buffer.from(`\r\n--${boundary}--\r\n`)
  ]);
  assert.equal((await http(editor + '?handler=Upload', 'POST', upload, `multipart/form-data; boundary=${boundary}`)).status, 302);
  const withImage = await http(editor);
  const imagePath = withImage.text.match(/data-insert-image="([^"]+)"/)[1];
  const fields = { 'Input.Title': 'Upgrade fixture', 'Input.BodyMarkdown': `Retained publication\n\n![Home](${imagePath})`,
    'Input.Summary': 'A persistent article', 'Input.Tags': 'Upgrade', 'Input.Version': '0' };
  assert.equal((await form(editor, editor + '?handler=Publish', fields)).status, 302);
  assert.equal((await form(editor, editor + '?handler=Save', { ...fields, 'Input.Version': '1', 'Input.BodyMarkdown': 'Retained private draft' })).status, 302);
  const publicPath = '/posts/upgrade-fixture/';
  const published = (await http(publicPath)).text;
  async function verifyContent() {
    assert.equal((await http('/health/ready')).status, 200);
    assert.equal((await http('/admin')).status, 200, 'Existing login must survive recreation');
    assert.equal((await http('/setup')).status, 404);
    assert.ok((await http('/admin/settings')).text.includes('Persistent upgrade verification'));
    assert.ok((await http(editor)).text.includes('Retained private draft'));
    assert.equal((await http(publicPath)).text, published);
    assert.equal((await http(imagePath)).status, 200);
  }
  run('CompleteSetup'); assert.equal(state('Persistent').SetupToken, ''); await verifyContent();
  console.log('Verified setup, published article/media, private draft, and retained session after token removal.');
  run('Stop'); run('Start'); assert.equal(state('Persistent').Image, original.Image); await verifyContent();
  run('Backup'); assert.ok(existsSync(join(state('Persistent').LastBackup, 'data.tar'))); await verifyContent();
  run('Upgrade', 'Persistent', '-Image', candidate);
  const upgraded = state('Persistent');
  assert.equal(upgraded.Volume, original.Volume);
  assert.equal(upgraded.Image, docker('image', 'inspect', '--format', '{{.Id}}', candidate));
  assert.equal(JSON.parse(readFileSync(join(upgraded.LastBackup, 'metadata.json'), 'utf8')).State.Image, original.Image);
  await verifyContent();
  console.log('Verified persistent restart, stopped-volume backup, and upgrade with retained content/session.');
  // An after-upgrade marker distinguishes restored state from merely changing the image.
  const marker = join(root, 'after-upgrade.txt'); writeFileSync(marker, 'newer data retained');
  const appId = docker('compose', '-p', upgraded.Project, '-f', join(root, 'persistent', 'compose.json'), 'ps', '-q', 'homestead');
  docker('cp', marker, `${appId}:/data/after-upgrade.txt`);
  run('Rollback');
  const restored = state('Persistent');
  assert.notEqual(restored.Volume, original.Volume); assert.equal(restored.Image, original.Image);
  await verifyContent();
  const restoredId = docker('compose', '-p', restored.Project, '-f', join(root, 'persistent', 'compose.json'), 'ps', '-q', 'homestead');
  docker('exec', restoredId, 'sh', '-c', 'test ! -e /data/after-upgrade.txt');
  docker('run', '--rm', '--user', '0', '--entrypoint', 'sh', '--mount', `type=volume,src=${original.Volume},dst=/data,readonly`,
    'caddy:2.10.2-alpine', '-c', 'test -f /data/after-upgrade.txt');
  const sandboxBefore = state('Sandbox');
  run('Reset', 'Sandbox', '-Image', candidate);
  assert.notEqual(state('Sandbox').Volume, sandboxBefore.Volume);
  assert.notEqual(state('Sandbox').SetupToken, sandboxBefore.SetupToken);
  await verifyContent();
  assert.throws(() => run('Reset', 'Persistent'));
  assert.throws(() => run('Start', 'Persistent', '-Image', candidate));
  assert.throws(() => run('Rollback', 'Persistent', '-BackupPath', join(root, 'sandbox')));
  // Reproduce a pruned saved image without deleting any shared Docker images.
  run('Stop');
  const beforeMissing = state('Persistent');
  const missing = { ...beforeMissing, Image: `sha256:${'0'.repeat(64)}` };
  writeFileSync(join(root, 'persistent', 'state.json'), JSON.stringify(missing));
  assert.throws(() => run('Upgrade', 'Persistent', '-Image', candidate), /Saved image .* is unavailable.*Recover/s);
  assert.throws(() => run('Start'), /Saved image .* is unavailable.*Recover/s);
  assert.throws(() => run('Backup'), /Saved image .* is unavailable.*Recover/s);
  assert.deepEqual(state('Persistent'), missing, 'Rejected upgrade must preserve saved state');
  const originalDatabaseHash = docker('run', '--rm', '--user', '0', '--entrypoint', 'sh', '--mount', `type=volume,src=${beforeMissing.Volume},dst=/data,readonly`,
    'caddy:2.10.2-alpine', '-c', 'sha256sum /data/homestead.db');
  run('Recover', 'Persistent', '-Image', candidate);
  const recovered = state('Persistent');
  assert.notEqual(recovered.Volume, beforeMissing.Volume, 'Recovery must use a copy of the data');
  assert.equal(recovered.Image, docker('image', 'inspect', '--format', '{{.Id}}', candidate));
  const recoveryBackup = JSON.parse(readFileSync(join(recovered.LastBackup, 'metadata.json'), 'utf8'));
  assert.equal(recoveryBackup.ImagePin, null, 'Do not claim an unavailable image is pinned');
  assert.equal(recoveryBackup.State.Image, missing.Image);
  assert.equal(recoveryBackup.State.Volume, beforeMissing.Volume);
  await verifyContent();
  assert.throws(() => run('Rollback'), /no paired image.*preserved/s);
  assert.deepEqual(state('Persistent'), recovered, 'Unavailable rollback must leave the recovered app running');
  await verifyContent();
  assert.equal(docker('run', '--rm', '--user', '0', '--entrypoint', 'sh', '--mount', `type=volume,src=${beforeMissing.Volume},dst=/data,readonly`,
    'caddy:2.10.2-alpine', '-c', 'sha256sum /data/homestead.db'), originalDatabaseHash, 'Original database must remain unchanged');
  assert.throws(() => run('Recover', 'Persistent', '-Image', candidate), /still available.*Upgrade/s);
  console.log('Verified missing-image preflight, recovery on copied data, retained content/session, preserved original, and honest rollback refusal.');
  console.log('PASS: isolated sandbox reset; persistent restart, setup-token removal, backup, upgrade, session/settings/draft/publication/media retention, paired image/data rollback, and preservation of newer data.');
} finally {
  // Delete only volumes and backup pins belonging to these verified temporary project identities.
  for (const environment of ['Sandbox', 'Persistent']) {
    if (!existsSync(join(root, environment.toLowerCase(), 'state.json'))) continue;
    const saved = state(environment);
    try {
      run('Stop', environment);
      for (const volume of docker('volume', 'ls', '--format', '{{.Name}}').split('\n').filter(name => name.startsWith(saved.Project + '_'))) {
        docker('volume', 'rm', volume);
      }
      const imagePrefix = `homestead-local:backup-${saved.Project.split('-')[2]}-${environment.toLowerCase()}-`;
      for (const image of docker('image', 'ls', '--format', '{{.Repository}}:{{.Tag}}').split('\n').filter(name => name.startsWith(imagePrefix))) docker('image', 'rm', image);
    } catch (error) { console.error(`Temporary cleanup failed: ${error.message}`); }
  }
  const absolute = resolve(root);
  if (!absolute.startsWith(resolve(tmpdir()) + '\\') && !absolute.startsWith(resolve(tmpdir()) + '/')) throw new Error('Unexpected cleanup path');
  rmSync(absolute, { recursive: true, force: true });
}
