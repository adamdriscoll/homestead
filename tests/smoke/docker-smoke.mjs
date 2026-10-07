import { execFileSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { randomBytes } from 'node:crypto';
import { request as httpsRequest } from 'node:https';
import assert from 'node:assert/strict';

// Runs the production Compose topology with local Caddy TLS and an isolated temporary volume.
const name = `homestead-smoke-${randomBytes(5).toString('hex')}`;
const temp = mkdtempSync(join(tmpdir(), name));
const secret = randomBytes(32).toString('hex');
const composeFile = resolve('deploy/compose.yaml');
const overrideFile = join(temp, 'override.json');
const caddyFile = join(temp, 'Caddyfile');
writeFileSync(caddyFile, 'localhost {\n tls internal\n reverse_proxy homestead:8080\n}\n');
writeFileSync(overrideFile, JSON.stringify({ services: {
  homestead: { image: 'homestead-smoke', pull_policy: 'never' },
  caddy: {
    ports: [{ target: 443, published: '0', host_ip: '127.0.0.1', protocol: 'tcp' }],
    volumes: [{ type: 'bind', source: caddyFile, target: '/etc/caddy/Caddyfile', read_only: true }]
  }
} }));
// Compose merges port lists, so reset the sample's fixed host ports in YAML.
const portReset = join(temp, 'ports.yaml');
writeFileSync(portReset, 'services:\n  caddy:\n    ports: !reset []\n');
const base = ['compose', '-p', name, '-f', composeFile, '-f', portReset, '-f', overrideFile];
const environment = { ...process.env, HOMESTEAD_DOMAIN: 'localhost', HOMESTEAD_SETUP_TOKEN: secret };
function docker(args, env = environment) {
  return execFileSync('docker', args, { env, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}
const cookies = new Map();
let port;
function http(path, method = 'GET', fields) {
  return new Promise((resolveResponse, reject) => {
    const body = fields ? new URLSearchParams(fields).toString() : undefined;
    const req = httpsRequest({ hostname: 'localhost', port, path, method,
      // Only this isolated smoke test uses Caddy's ephemeral internal CA.
      rejectUnauthorized: false,
      headers: { Cookie: [...cookies].map(([key, value]) => `${key}=${value}`).join('; '),
        ...(body ? { 'Content-Type': 'application/x-www-form-urlencoded', 'Content-Length': Buffer.byteLength(body) } : {}) }
    }, response => {
      for (const cookie of response.headers['set-cookie'] ?? []) {
        const first = cookie.split(';')[0]; const split = first.indexOf('=');
        cookies.set(first.slice(0, split), first.slice(split + 1));
      }
      let text = ''; response.setEncoding('utf8'); response.on('data', chunk => text += chunk);
      response.on('end', () => resolveResponse({ status: response.statusCode, headers: response.headers, text }));
    });
    req.setTimeout(3000, () => req.destroy(new Error('Request timed out'))); req.on('error', reject);
    req.end(body);
  });
}
async function ready() {
  for (let attempt = 0; attempt < 60; attempt++) {
    try { if ((await http('/health/ready')).status === 200) return; } catch { /* starting */ }
    await new Promise(resolveWait => setTimeout(resolveWait, 1000));
  }
  throw new Error('Container did not become ready');
}
async function form(pagePath, postPath, fields) {
  const page = await http(pagePath); assert.equal(page.status, 200);
  const token = page.text.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/);
  assert.ok(token, 'Antiforgery token must be present');
  return http(postPath, 'POST', { ...fields, __RequestVerificationToken: token[1] });
}
try {
  console.log('Starting isolated production Compose deployment with local HTTPS.');
  docker([...base, 'up', '-d', '--no-build']);
  port = Number(docker([...base, 'port', 'caddy', '443']).split(':').at(-1));
  await ready();
  assert.equal((await http('/admin')).status, 302);
  assert.equal((await http('/homestead.db')).status, 404);
  assert.equal((await http('/setup', 'POST', { 'Input.Token': secret })).status, 400);
  const credentials = { 'Input.Token': secret, 'Input.Email': 'owner@example.test',
    'Input.Password': 'Smoke-test-password-123!', 'Input.ConfirmPassword': 'Smoke-test-password-123!' };
  assert.equal((await form('/setup', '/setup', { ...credentials, 'Input.Token': 'wrong' })).status, 403);
  assert.equal((await form('/setup', '/setup', credentials)).status, 302);
  assert.equal((await http('/setup')).status, 404);
  const login = await form('/account/login', '/account/login', credentials);
  assert.equal(login.status, 302); assert.match(login.headers['set-cookie'].join(' '), /secure;.*httponly/i);
  assert.equal((await http('/admin')).status, 200);
  assert.equal((await http('/css/site.css')).status, 200);
  const settings = { 'Input.Title': 'Compose persistent home', 'Input.CanonicalBaseUrl': 'https://changed.example.test',
    'Input.DisplayAuthor': 'Smoke author', 'Input.TimeZone': 'America/Chicago', 'Input.AccentColor': 'Forest',
    'Input.AboutMarkdown': '# Persisted About', 'Input.Description': 'Persisted description' };
  assert.equal((await form('/admin/settings', '/admin/settings', settings)).status, 302);
  const id = docker([...base, 'ps', '-q', 'homestead']);
  assert.notEqual(docker(['exec', id, 'id', '-u']), '0');
  const keys = docker(['exec', id, 'sh', '-c', 'ls /data/keys']); assert.ok(keys.includes('.xml'));
  console.log('Recreating the application without the bootstrap secret; checking settings and session persistence.');
  docker([...base, 'up', '-d', '--no-build', '--force-recreate', 'homestead'], { ...environment, HOMESTEAD_SETUP_TOKEN: '' });
  await ready();
  assert.equal((await http('/admin')).status, 200);
  const saved = await http('/admin/settings'); assert.equal(saved.status, 200);
  assert.ok(saved.text.includes('Compose persistent home')); assert.ok(saved.text.includes('https://changed.example.test'));
  assert.equal((await http('/setup')).status, 404);
  assert.equal(keys, docker(['exec', docker([...base, 'ps', '-q', 'homestead']), 'sh', '-c', 'ls /data/keys']));
  assert.equal((await form('/admin', '/account/logout', {})).status, 302);
  assert.equal((await http('/admin')).status, 302);
  console.log('PASS: HTTPS setup/login/logout, private storage, CSS, non-root runtime, persisted DB/settings/session keys.');
} catch (error) {
  try { console.error(docker([...base, 'logs', '--tail', '60'])); } catch { /* original error wins */ }
  throw error;
} finally {
  // Remove only the uniquely named test project and its disposable volumes.
  try { docker([...base, 'down', '--volumes', '--remove-orphans']); } finally { rmSync(temp, { recursive: true, force: true }); }
}
