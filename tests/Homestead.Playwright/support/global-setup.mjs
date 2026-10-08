import { randomBytes } from 'node:crypto';
import { docker } from './docker.mjs';

export default async function globalSetup() {
  await docker(['info', '--format', '{{.OSType}}']).then(os => {
    if (os !== 'linux') throw new Error('Playwright tests require Docker with Linux containers.');
  });
  await docker(['compose', 'version']);
  let builtTag;
  if (!process.env.HOMESTEAD_PLAYWRIGHT_IMAGE) {
    builtTag = `homestead-playwright:${randomBytes(8).toString('hex')}`;
    console.log('Building the current checkout for isolated browser tests…');
    await docker(['build', '-f', 'deploy/Dockerfile', '-t', builtTag, '.'], process.env, 900_000);
  }
  // Pin the immutable image for every test, including when a caller supplies a tag.
  process.env.HOMESTEAD_PLAYWRIGHT_IMAGE = await docker([
    'image', 'inspect', '--format', '{{.Id}}', builtTag ?? process.env.HOMESTEAD_PLAYWRIGHT_IMAGE
  ]);
  return async () => {
    if (builtTag) await docker(['image', 'rm', builtTag]);
  };
}
