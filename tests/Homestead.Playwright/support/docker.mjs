import { execFile } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { request } from '@playwright/test';

const execute = promisify(execFile);
export const repoRoot = fileURLToPath(new URL('../../../', import.meta.url));

export async function docker(args, env = process.env, timeout = 120_000) {
  const { stdout } = await execute('docker', args, {
    cwd: repoRoot, env, encoding: 'utf8', timeout, maxBuffer: 16 * 1024 * 1024
  });
  return stdout.trim();
}

export class DockerSite {
  constructor() {
    this.name = `homestead-playwright-${randomBytes(8).toString('hex')}`;
    this.temp = mkdtempSync(join(tmpdir(), this.name));
    this.setupToken = randomBytes(32).toString('hex');
    this.email = 'owner@example.test';
    this.password = 'Browser-test-password-123!';
    this.env = { ...process.env, HOMESTEAD_PLAYWRIGHT_SETUP_TOKEN: this.setupToken };
    const caddyFile = join(this.temp, 'Caddyfile');
    writeFileSync(caddyFile, 'localhost {\n tls internal\n reverse_proxy homestead:8080\n}\n');
    const composeFile = join(this.temp, 'compose.json');
    writeFileSync(composeFile, JSON.stringify({
      services: {
        homestead: {
          image: process.env.HOMESTEAD_PLAYWRIGHT_IMAGE,
          pull_policy: 'never',
          environment: {
            HOMESTEAD_BASE_URL: 'https://localhost',
            HOMESTEAD_SETUP_TOKEN: '${HOMESTEAD_PLAYWRIGHT_SETUP_TOKEN}',
            HOMESTEAD_TRUSTED_PROXIES: '${HOMESTEAD_PLAYWRIGHT_PROXY_IP}',
            AllowedHosts: 'localhost'
          },
          volumes: ['homestead_data:/data'],
          security_opt: ['no-new-privileges:true'],
          cap_drop: ['ALL']
        },
        caddy: {
          image: 'caddy:2.10.2-alpine',
          ports: ['127.0.0.1::443'],
          volumes: [
            { type: 'bind', source: caddyFile, target: '/etc/caddy/Caddyfile', read_only: true },
            'caddy_data:/data', 'caddy_config:/config'
          ]
        }
      },
      volumes: { homestead_data: {}, caddy_data: {}, caddy_config: {} }
    }));
    this.args = ['compose', '-p', this.name, '-f', composeFile];
  }

  compose(args) {
    return docker([...this.args, ...args], this.env);
  }

  async start() {
    // Let Docker allocate a collision-free network, then trust only this proxy.
    this.env.HOMESTEAD_PLAYWRIGHT_PROXY_IP = '127.0.0.1';
    await this.compose(['up', '-d', 'caddy']);
    const proxyId = await this.compose(['ps', '-q', 'caddy']);
    const [proxy] = JSON.parse(await docker(['inspect', proxyId]));
    this.env.HOMESTEAD_PLAYWRIGHT_PROXY_IP = Object.values(proxy.NetworkSettings.Networks)[0].IPAddress;
    await this.compose(['up', '-d', 'homestead']);
    const mapping = await this.compose(['port', 'caddy', '443']);
    this.baseURL = `https://localhost:${mapping.split(':').at(-1)}`;
    const client = await request.newContext({ baseURL: this.baseURL, ignoreHTTPSErrors: true });
    try {
      const deadline = Date.now() + 60_000;
      while (Date.now() < deadline) {
        try {
          const response = await client.get('/health/ready', { timeout: 2000 });
          const ready = response.ok();
          await response.dispose();
          if (ready) return;
        } catch { /* The application and local TLS certificate are still starting. */ }
        await new Promise(resolveWait => setTimeout(resolveWait, 500));
      }
      throw new Error(`Test instance ${this.name} did not become ready.`);
    } finally {
      await client.dispose();
    }
  }

  async logs() {
    return (await this.compose(['logs', '--no-color', '--tail', '150'])).replaceAll(this.setupToken, '[redacted]');
  }

  async stop() {
    // This generated project owns only fresh test volumes; never use deploy/compose.yaml here.
    await this.compose(['down', '--volumes', '--remove-orphans']);
    const target = resolve(this.temp);
    if (dirname(target) !== resolve(tmpdir())) throw new Error('Refusing to remove a non-temporary test directory.');
    rmSync(target, { recursive: true, force: true });
  }
}
