import { resolve } from 'node:path';
import { repoRoot } from '../support/docker.mjs';
import { test, expect, newPost, saved, publish } from '../support/fixtures.mjs';

for (const javaScriptEnabled of [true, false]) {
  test.describe(`50-photo upload with JavaScript ${javaScriptEnabled}`, () => {
    test.use({ javaScriptEnabled });
    test('optimizes photos, retains originals on request, and edits alt text after upload', async ({ owner: page, browser }) => {
      test.setTimeout(180_000);
      await newPost(page, 'gallery');
      if (javaScriptEnabled) await page.getByRole('tab', { name: 'Images', exact: true }).click();
      await expect(page.getByLabel('Image description (alt text)')).toHaveCount(0);
      const retain = page.getByLabel('Retain original images privately');
      await expect(retain).not.toBeChecked();
      await retain.check();
      const photo = resolve(repoRoot, 'src/Homestead/wwwroot/icon-192.png');
      await page.getByLabel('Photos', { exact: false }).setInputFiles(Array(50).fill(photo));
      await page.getByRole('button', { name: 'Upload photo', exact: true }).click();
      const images = page.locator('#article-images > [data-media]');
      await expect(images).toHaveCount(50, { timeout: 120_000 });
      await expect(page.getByLabel('Image description (alt text)')).toHaveCount(50);
      const original = images.first().getByRole('link', { name: 'Download original', exact: true });
      await expect(original).toBeVisible();
      const url = await original.getAttribute('href');
      const response = await page.request.get(url);
      expect(response.status()).toBe(200);
      expect(response.headers()['cache-control']).toBe('no-store');
      expect(response.headers()['content-disposition']).toContain('attachment');
      const anonymous = await browser.newContext({ ignoreHTTPSErrors: true });
      try {
        expect((await anonymous.request.get(new URL(url, page.url()).href, { maxRedirects: 0 })).status()).toBe(302);
      } finally { await anonymous.close(); }
      if (javaScriptEnabled) await saved(page);
      await page.reload();
      await expect(retain).not.toBeChecked();
      if (javaScriptEnabled) await page.getByRole('tab', { name: 'Images', exact: true }).click();
      await expect(images.first().getByRole('link', { name: 'Download original', exact: true })).toBeVisible();
    });
  });
}

test('inline image upload and formatting appear in preview and become public only on publish', async ({ owner: page, browser, site }) => {
  await newPost(page);
  await page.getByLabel('Title', { exact: false }).fill('Browser image article');
  const body = page.getByLabel('Body');
  await body.fill('A browser photo.');
  await body.press('ControlOrMeta+End');
  await page.getByRole('button', { name: 'Bold (Ctrl/⌘+B)', exact: true }).click();
  await expect(body).toHaveValue('A browser photo.**bold text**');
  const picker = page.waitForEvent('filechooser');
  await page.getByRole('button', { name: 'Upload and insert image', exact: true }).click();
  await (await picker).setFiles(resolve(repoRoot, 'src/Homestead/wwwroot/icon-192.png'));
  await expect(page.locator('#inline-upload-status')).toHaveText('Uploaded and inserted into the draft.');
  await saved(page);
  const markdown = await body.inputValue();
  const mediaPath = markdown.match(/\]\((\/media\/[^)]+)\)/)[1];
  const anonymous = await browser.newContext({ baseURL: site.baseURL, ignoreHTTPSErrors: true, javaScriptEnabled: false });
  try {
    expect((await anonymous.request.get(mediaPath)).status()).toBe(404);
    const id = mediaPath.split('/')[2];
    const privateResponse = await anonymous.request.get(`/admin/media/${id}`, { maxRedirects: 0 });
    expect(privateResponse.status()).toBe(302);
    await page.getByRole('tab', { name: 'Preview', exact: true }).click();
    const previewImage = page.frameLocator('#draft-preview').locator('main img');
    await expect(previewImage).toBeVisible();
    await expect(previewImage).toHaveJSProperty('naturalWidth', 192);
    await page.getByRole('tab', { name: 'Write', exact: true }).click();
    const publicPath = await publish(page);
    const publicPage = await anonymous.newPage();
    await publicPage.goto(publicPath);
    await expect(publicPage.locator('main strong')).toHaveText('bold text');
    await expect(publicPage.locator('main img')).toHaveJSProperty('naturalWidth', 192);
    expect((await anonymous.request.get(mediaPath)).status()).toBe(200);
  } finally {
    await anonymous.close();
  }
});

test('gallery batch upload saves descriptions, captions and photo order', async ({ owner: page, context }) => {
  await newPost(page, 'gallery');
  await page.getByRole('tab', { name: 'Images', exact: true }).click();
  const photo = resolve(repoRoot, 'src/Homestead/wwwroot/icon-192.png');
  await page.getByLabel('Photos', { exact: false }).setInputFiles([photo, photo]);
  await page.getByRole('button', { name: 'Upload photo', exact: true }).click();
  const images = page.locator('#article-images > [data-media]');
  await expect(images).toHaveCount(2);
  await expect(page.getByLabel('Photos', { exact: false })).toHaveValue('');
  await saved(page);
  await page.getByRole('tab', { name: 'Write', exact: true }).click();
  await page.getByLabel('Title', { exact: true }).fill('Browser gallery');
  await page.getByRole('tab', { name: 'Images', exact: true }).click();
  await images.nth(0).getByLabel('Image description (alt text)').fill('First home');
  await images.nth(0).getByLabel('Caption').fill('First view');
  await images.nth(1).getByLabel('Image description (alt text)').fill('Second home');
  await images.nth(1).getByLabel('Caption').fill('Second view');
  await images.nth(1).getByRole('button', { name: 'Move photo up', exact: true }).click();
  await page.getByRole('tab', { name: 'Images', exact: true }).click();
  await expect(images.nth(0).getByLabel('Caption')).toHaveValue('Second view');
  const publicPath = await publish(page);
  const publicPage = await context.newPage();
  await publicPage.goto(publicPath);
  await expect(publicPage.locator('main figcaption')).toHaveText(['Second view', 'First view']);
  await expect(publicPage.getByRole('img', { name: 'Second home' })).toHaveJSProperty('naturalWidth', 192);
});
