import { execFileSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { randomBytes } from 'node:crypto';
import { request as httpsRequest } from 'node:https';
import { request as httpRequest } from 'node:http';
import assert from 'node:assert/strict';

// Runs the production Compose topology with local Caddy TLS and an isolated temporary volume.
const name = `homestead-smoke-${randomBytes(5).toString('hex')}`;
const restoreVolume = `${name}_restore`;
const staticContainer = `${name}-static`;
let restoreCreated = false;
let staticCreated = false;
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
function http(path, method = 'GET', fields, contentType = 'application/x-www-form-urlencoded', binary = false) {
  return new Promise((resolveResponse, reject) => {
    const body = fields ? Buffer.isBuffer(fields) ? fields : new URLSearchParams(fields).toString() : undefined;
    const req = httpsRequest({ hostname: 'localhost', port, path, method,
      // Only this isolated smoke test uses Caddy's ephemeral internal CA.
      rejectUnauthorized: false,
      headers: { Cookie: [...cookies].map(([key, value]) => `${key}=${value}`).join('; '),
        ...(body ? { 'Content-Type': contentType, 'Content-Length': Buffer.byteLength(body) } : {}) }
    }, response => {
      for (const cookie of response.headers['set-cookie'] ?? []) {
        const first = cookie.split(';')[0]; const split = first.indexOf('=');
        cookies.set(first.slice(0, split), first.slice(split + 1));
      }
      const chunks = []; response.on('data', chunk => chunks.push(chunk));
      response.on('end', () => {
        const bytes = Buffer.concat(chunks);
        resolveResponse({ status: response.statusCode, headers: response.headers, text: binary ? bytes : bytes.toString('utf8') });
      });
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
async function form(pagePath, postPath, fields, binary = false) {
  const page = await http(pagePath); assert.equal(page.status, 200);
  const token = page.text.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/);
  assert.ok(token, 'Antiforgery token must be present');
  return http(postPath, 'POST', { ...fields, __RequestVerificationToken: token[1] }, 'application/x-www-form-urlencoded', binary);
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
  assert.equal((await http('/js/article-editor.js')).status, 200);
  assert.equal((await http('/manifest.webmanifest')).status, 200);
  const created = await form('/admin/posts', '/admin/posts?handler=New', {});
  assert.equal(created.status, 302);
  const editor = created.headers.location;
  const editorPage = await http(editor);
  const token = editorPage.text.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/)[1];
  const boundary = `homestead-${randomBytes(12).toString('hex')}`;
  const photo = readFileSync(resolve('src/Homestead/wwwroot/icon-192.png'));
  const multipart = Buffer.concat([
    Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="__RequestVerificationToken"\r\n\r\n${token}\r\n--${boundary}\r\nContent-Disposition: form-data; name="UploadAlt"\r\n\r\nA home icon\r\n--${boundary}\r\nContent-Disposition: form-data; name="Upload"; filename="home.png"\r\nContent-Type: image/png\r\n\r\n`),
    photo, Buffer.from(`\r\n--${boundary}--\r\n`)
  ]);
  assert.equal((await http(editor + '?handler=Upload', 'POST', multipart, `multipart/form-data; boundary=${boundary}`)).status, 302);
  const withImage = await http(editor);
  const imageUrl = withImage.text.match(/data-insert-image="([^"]+)"/)[1];
  assert.equal((await http(imageUrl)).status, 404);
  const article = { 'Input.Title': 'Compose article', 'Input.BodyMarkdown': `Our original story.\n\n![Home](${imageUrl})`,
    'Input.Summary': 'Written from our home', 'Input.Tags': 'Home, Smoke', 'Input.Version': '0' };
  assert.equal((await form(editor, editor + '?handler=Publish', article)).status, 302);
  assert.ok((await http(editor.replace('/edit', '/preview'))).text.includes('/admin/media/'));
  const publicPath = '/posts/compose-article/';
  const published = await http(publicPath);
  assert.equal(published.status, 200); assert.ok(published.text.includes('https://changed.example.test/posts/compose-article/'));
  assert.equal((await http(imageUrl)).status, 200);
  assert.equal((await form(editor, editor + '?handler=Save', { ...article, 'Input.Version': '1', 'Input.BodyMarkdown': 'Private pending edits' })).status, 302);
  assert.equal((await http(publicPath)).text, published.text);
  assert.equal((await form(editor, editor + '?handler=Publish', { ...article, 'Input.Version': '2', 'Input.BodyMarkdown': `Updated story.\n\n![Home](${imageUrl})` })).status, 302);
  assert.ok((await http(publicPath)).text.includes('Updated story.'));
  const noteCreated = await form('/admin/posts', '/admin/posts?handler=New', { type: 'Microblog' });
  assert.equal(noteCreated.status, 302);
  const noteEditor = noteCreated.headers.location;
  const noteFields = { 'Input.Title': 'A porch note', 'Input.BodyMarkdown': 'A quiet afternoon & a cup of tea.',
    'Input.Tags': 'Home, Moments', 'Input.Version': '0' };
  assert.equal((await form(noteEditor, noteEditor + '?handler=Publish', noteFields)).status, 302);
  for (const [type, slug, fields] of [
    ['Review', 'compose-coffee', { 'Input.Subject': 'Compose coffee', 'Input.Rating': '5', 'Input.BodyMarkdown': 'A **warm** cup.', 'Input.ExternalUrl': 'https://example.test/coffee' }],
    ['Guide', 'compose-guide', { 'Input.Title': 'Compose guide', 'Input.BodyMarkdown': 'Make yourself at home.',
      'Input.Steps[0].Id': '85895aa3-2ee7-4698-acee-14554be33490', 'Input.Steps[0].BodyMarkdown': 'Open the **door**.',
      'Input.Steps[1].Id': 'fa194c9a-3f88-45fc-9c3a-066903db75f3', 'Input.Steps[1].BodyMarkdown': 'Put the kettle on.' }],
    ['Gallery', 'compose-gallery', { 'Input.Title': 'Compose gallery', 'Input.BodyMarkdown': 'Home in pictures.' }]
  ]) {
    const createdType = await form('/admin/posts', '/admin/posts?handler=New', { type });
    assert.equal(createdType.status, 302);
    const typeEditor = createdType.headers.location;
    if (type === 'Gallery') {
      const uploadPage = await http(typeEditor);
      const uploadToken = uploadPage.text.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/)[1];
      const batch = Buffer.concat([
        Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="__RequestVerificationToken"\r\n\r\n${uploadToken}\r\n`),
        ...['first.png', 'second.png'].flatMap(filename => [
          Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="Uploads"; filename="${filename}"\r\nContent-Type: image/png\r\n\r\n`), photo, Buffer.from('\r\n')
        ]), Buffer.from(`--${boundary}--\r\n`)
      ]);
      assert.equal((await http(typeEditor + '?handler=Upload', 'POST', batch, `multipart/form-data; boundary=${boundary}`)).status, 302);
      const galleryEditor = await http(typeEditor);
      assert.ok(galleryEditor.text.includes('Some photos are missing alt text'));
      const images = [...galleryEditor.text.matchAll(/data-media="([^"]+)"/g)].map(match => match[1]);
      assert.equal(images.length, 2);
      for (const image of images) assert.equal((await http(image)).status, 404);
      const secondId = images[1].split('/')[2];
      fields[`Input.Captions[${secondId}]`] = 'The second view'; fields[`Input.AltText[${secondId}]`] = 'A home';
      fields['Input.MediaOrder[0]'] = secondId; fields['Input.MediaOrder[1]'] = images[0].split('/')[2];
    }
    assert.equal((await form(typeEditor, typeEditor + '?handler=Publish', { ...fields, 'Input.Version': '0', 'Input.Tags': 'Home' })).status, 302);
    const output = await http(`/posts/${slug}/`); assert.equal(output.status, 200);
    assert.ok((await http('/rss.xml')).text.includes(`/posts/${slug}/`));
    assert.ok((await http('/sitemap.xml')).text.includes(`/posts/${slug}/`));
    if (type === 'Review') assert.ok(output.text.includes('5/5'));
    if (type === 'Guide') assert.ok(output.text.includes('<ol'));
    if (type === 'Gallery') assert.ok(output.text.includes('<figcaption>The second view</figcaption>'));
  }
  for (const path of ['/', '/notes/', '/articles/', '/reviews/', '/guides/', '/galleries/', '/about/', '/tags/home/']) {
    const page = await http(path); assert.equal(page.status, 200);
    assert.ok(page.text.includes('property="og:image"')); assert.match(page.text, /type="application\/rss(?:\+|&#x2B;)xml"/i);
    assert.ok(!page.text.includes('<script'), 'Public pages work without authoring JavaScript');
  }
  assert.ok((await http('/notes/')).text.includes('A porch note'));
  assert.ok((await http('/about/')).text.includes('Persisted About'));
  assert.ok((await http('/rss.xml')).text.includes('A porch note'));
  for (const slug of ['compose-coffee', 'compose-guide', 'compose-gallery']) assert.equal((await http(`/posts/${slug}/`)).status, 200);
  assert.ok((await http('/sitemap.xml')).text.includes('https://changed.example.test/posts/a-porch-note/'));
  assert.ok((await http('/robots.txt')).text.includes('Sitemap: https://changed.example.test/sitemap.xml'));
  assert.equal((await http('/assets/social-default.png')).status, 200);
  const id = docker([...base, 'ps', '-q', 'homestead']);
  assert.notEqual(docker(['exec', id, 'id', '-u']), '0');
  const keys = docker(['exec', id, 'sh', '-c', 'ls /data/keys']); assert.ok(keys.includes('.xml'));
  for (const handler of ['Static', 'Hugo', 'Backup']) {
    assert.equal((await http(`/admin/maintenance?handler=${handler}`, 'POST', {})).status, 400);
    const archive = await form('/admin/maintenance', `/admin/maintenance?handler=${handler}`, {}, true);
    assert.equal(archive.status, 200); assert.equal(archive.headers['content-type'], 'application/zip');
    assert.equal(archive.headers['cache-control'], 'no-store');
    assert.equal(archive.text.subarray(0, 2).toString(), 'PK');
    writeFileSync(join(temp, `${handler}.zip`), archive.text);
  }
  assert.equal(docker(['exec', id, 'sh', '-c', 'ls -A /data/backups']), '');
  console.log('Serving the extracted static ZIP with Caddy while Homestead is stopped.');
  docker(['run', '-d', '--name', staticContainer, '-p', '127.0.0.1::8080', '-v', `${temp}:/archives:ro`,
    '--entrypoint', 'sh', 'caddy:2.10.2-alpine', '-c', 'mkdir /site && unzip -q /archives/Static.zip -d /site && caddy file-server --root /site --listen :8080']);
  staticCreated = true;
  const staticPort = Number(docker(['port', staticContainer, '8080']).split(':').at(-1));
  docker([...base, 'stop', 'homestead']);
  async function staticHttp(path) {
    return new Promise((resolveResponse, reject) => {
      const req = httpRequest({ hostname: '127.0.0.1', port: staticPort, path }, response => {
        const chunks = []; response.on('data', chunk => chunks.push(chunk));
        response.on('end', () => resolveResponse({ status: response.statusCode, text: Buffer.concat(chunks).toString('utf8') }));
      });
      req.on('error', reject); req.end();
    });
  }
  for (let attempt = 0; attempt < 30; attempt++) {
    try { if ((await staticHttp('/')).status === 200) break; } catch { /* static server starting */ }
    await new Promise(resolveWait => setTimeout(resolveWait, 1000));
  }
  const resources = new Set(['/', '/notes/', '/articles/', '/reviews/', '/guides/', '/galleries/', '/about/', '/tags/home/',
    '/rss.xml', '/sitemap.xml', '/robots.txt', '/assets/site.css', '/assets/social-default.png', publicPath, imageUrl,
    '/posts/a-porch-note/', '/posts/compose-coffee/', '/posts/compose-guide/', '/posts/compose-gallery/']);
  for (const path of resources) {
    const response = await staticHttp(path); assert.equal(response.status, 200, path);
    assert.ok(!response.text.includes('Private pending edits'));
    if (path.endsWith('/'))
      for (const match of response.text.matchAll(/(?:href|src)="(\/[^"?#]*)"/g)) resources.add(match[1]);
  }
  docker([...base, 'start', 'homestead']); await ready();
  console.log('Restoring the downloaded backup into a fresh Docker volume and rebuilding public output.');
  docker(['volume', 'create', restoreVolume]);
  restoreCreated = true;
  try {
    // Mount the file directly: Linux mkdtemp directories are 0700 and belong to the runner, not the image's app UID.
    docker(['run', '--rm', '-v', `${restoreVolume}:/data`, '-v', `${join(temp, 'Backup.zip')}:/backup.zip:ro`, 'homestead-smoke', 'restore', '/backup.zip', '/data']);
    // Mount a fresh data volume in the same HTTPS topology; the original volume remains untouched.
    const restoreOverride = join(temp, 'restore.json');
    writeFileSync(restoreOverride, JSON.stringify({ services: { homestead: { volumes: [{ type: 'volume', source: 'restore_data', target: '/data' }] } },
      volumes: { restore_data: { external: true, name: restoreVolume } } }));
    docker([...base, '-f', restoreOverride, 'up', '-d', '--no-build', '--force-recreate', 'homestead'], { ...environment, HOMESTEAD_SETUP_TOKEN: '' });
    await ready();
    assert.equal((await http('/admin')).status, 200, 'Restored keys retain the author session');
    assert.equal((await http('/setup')).status, 404);
    assert.ok((await http(publicPath)).text.includes('Updated story.'));
    assert.equal((await http(imageUrl)).status, 200);
    for (const slug of ['compose-coffee', 'compose-guide', 'compose-gallery']) assert.equal((await http(`/posts/${slug}/`)).status, 200);
  } finally {
    docker([...base, 'up', '-d', '--no-build', '--force-recreate', 'homestead'], { ...environment, HOMESTEAD_SETUP_TOKEN: '' });
    await ready();
    docker(['volume', 'rm', restoreVolume]);
    restoreCreated = false;
  }
  console.log('Recreating the application without the bootstrap secret; checking settings and session persistence.');
  docker([...base, 'up', '-d', '--no-build', '--force-recreate', 'homestead'], { ...environment, HOMESTEAD_SETUP_TOKEN: '' });
  await ready();
  assert.equal((await http('/admin')).status, 200);
  const saved = await http('/admin/settings'); assert.equal(saved.status, 200);
  assert.ok(saved.text.includes('Compose persistent home')); assert.ok(saved.text.includes('https://changed.example.test'));
  assert.equal((await http('/setup')).status, 404);
  assert.ok((await http(publicPath)).text.includes('Updated story.'));
  assert.equal((await http(imageUrl)).status, 200);
  assert.ok((await http(editor)).text.includes('Updated story.'));
  assert.ok((await http('/posts/a-porch-note/')).text.includes('A quiet afternoon'));
  assert.ok((await http('/rss.xml')).text.includes('A porch note'));
  assert.equal((await form(noteEditor, noteEditor + '?handler=Unpublish', { ...noteFields, 'Input.Version': '1' })).status, 302);
  assert.equal((await http('/posts/a-porch-note/')).status, 404);
  assert.ok(!(await http('/rss.xml')).text.includes('A porch note'));
  assert.ok(!(await http('/sitemap.xml')).text.includes('/posts/a-porch-note/'));
  assert.equal(keys, docker(['exec', docker([...base, 'ps', '-q', 'homestead']), 'sh', '-c', 'ls /data/keys']));
  assert.equal((await form('/admin', '/account/logout', {})).status, 302);
  assert.equal((await http('/admin')).status, 302);
  assert.equal((await http(publicPath)).status, 200);
  assert.equal((await http(imageUrl)).status, 200);
  const privateImage = imageUrl.replace(/\/media\/([^/]+)\/[^/]+/, '/admin/media/$1');
  assert.equal((await http(privateImage)).status, 302);
  console.log('PASS: HTTPS authoring, all five types, exports, full offline backup restore/rebuild, draft isolation, non-root runtime, and restart persistence.');
} catch (error) {
  try { console.error(docker([...base, 'logs', '--tail', '60'])); } catch { /* original error wins */ }
  throw error;
} finally {
  // Remove only the uniquely named test project and its disposable volumes.
  try { docker([...base, 'down', '--volumes', '--remove-orphans']); } finally {
    if (staticCreated) docker(['rm', '-f', staticContainer]);
    if (restoreCreated) docker(['volume', 'rm', restoreVolume]);
    rmSync(temp, { recursive: true, force: true });
  }
}
