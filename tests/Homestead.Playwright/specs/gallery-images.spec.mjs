import { resolve } from 'node:path';
import { repoRoot } from '../support/docker.mjs';
import { test, expect, newPost, publish } from '../support/fixtures.mjs';

test.use({ javaScriptEnabled: false });

for (const theme of ['porch', 'journal', 'ledger', 'folio', 'townsquare']) {
  test(`${theme} gallery photos open full-size pages without JavaScript`, async ({ owner: page }, testInfo) => {
    test.setTimeout(120_000);
    await newPost(page, 'gallery');
    await page.getByLabel('Title', { exact: false }).fill('Views from home');
    await page.getByRole('button', { name: 'Save draft', exact: true }).click();
    await page.getByLabel('Photos', { exact: false }).setInputFiles([
      resolve(repoRoot, 'src/Homestead/wwwroot/social-default.png'),
      resolve(repoRoot, 'src/Homestead/wwwroot/icon-192.png')
    ]);
    await page.getByRole('button', { name: 'Upload photo', exact: true }).click();
    const photos = page.locator('#article-images > [data-media]');
    await expect(photos).toHaveCount(2);
    await photos.nth(0).getByLabel('Image description (alt text)').fill('Wide view');
    await photos.nth(0).getByLabel('Caption', { exact: true }).fill('A quiet evening at home');
    await photos.nth(1).getByLabel('Image description (alt text)').fill('Small view');
    await photos.nth(1).getByLabel('Caption', { exact: true }).fill('Another view');
    const publicPath = await publish(page);
    const previewPath = new URL(page.url()).pathname.replace(/\/edit$/, '/preview');
    await page.goto('/admin/settings');
    await page.getByLabel('Author display name', { exact: true }).fill('Ada');
    await page.getByLabel('Public theme', { exact: true }).selectOption(theme);
    await page.getByRole('button', { name: 'Save settings', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('Settings saved and public site rebuilt.');

    for (const colorScheme of ['light', 'dark']) {
      await page.emulateMedia({ colorScheme });
      for (const path of ['/', '/galleries/', publicPath, previewPath]) {
        await page.goto(path);
        expect(await page.locator('script').count()).toBe(0);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        if (theme === 'townsquare') {
          await page.locator('[data-post-photo] a').first().focus();
          expect(await page.locator('[data-post-photo]').first().evaluate(el => getComputedStyle(el).outlineStyle)).toBe('solid');
          const caption = page.locator('[data-post-photo] figcaption').first();
          await expect(caption).toBeVisible();
          expect(await caption.evaluate(el => {
            const figure = el.closest('figure').getBoundingClientRect();
            const image = el.closest('figure').querySelector('img').getBoundingClientRect();
            const box = el.getBoundingClientRect();
            return getComputedStyle(el).position === 'absolute' && Math.abs(box.bottom - figure.bottom) < 2 && Math.abs(box.bottom - image.bottom) < 2 && box.top >= figure.top;
          })).toBe(true);
        }
        await page.screenshot({ path: testInfo.outputPath(`${theme}-${colorScheme}-${path === '/' ? 'timeline' : path === '/galleries/' ? 'listing' : path === publicPath ? 'post' : 'preview'}.png`), fullPage: true });
        await page.locator('main a:has(img)').first().click();
        await expect(page.locator('[data-gallery-image]')).toBeVisible();
        await expect(page.getByRole('link', { name: 'Previous photo', exact: true })).toHaveCount(0);
        const image = page.locator('[data-gallery-image] img');
        await expect(image).toBeVisible();
        expect(await image.evaluate(el => {
          const box = el.getBoundingClientRect();
          return getComputedStyle(el).maxHeight === 'none' && Math.abs(box.width / box.height - el.naturalWidth / el.naturalHeight) < 0.01;
        })).toBe(true);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await page.getByRole('link', { name: 'Next photo', exact: true }).click();
        await expect(page.locator('[data-gallery-image] img')).toHaveAttribute('alt', 'Small view');
        await expect(page.getByRole('link', { name: 'Next photo', exact: true })).toHaveCount(0);
        await page.getByRole('link', { name: 'Previous photo', exact: true }).click();
        await page.screenshot({ path: testInfo.outputPath(`${theme}-${colorScheme}-image-${path === previewPath ? 'private' : 'public'}.png`), fullPage: true });
        await page.getByRole('link', { name: 'Back to gallery', exact: true }).click();
        await expect(page).toHaveURL(new RegExp((path === previewPath ? previewPath : publicPath) + '$'));
      }
    }
  });
}
