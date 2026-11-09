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
        if (path === publicPath || path === previewPath) {
          const photos = page.locator('[data-gallery-photos] figure');
          await expect(photos).toHaveCount(2);
          const [first, second] = await Promise.all([photos.nth(0).boundingBox(), photos.nth(1).boundingBox()]);
          expect(second.y).toBeGreaterThanOrEqual(first.y + first.height);
        }
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

test('Town Square limits a gallery mosaic and links the overflow tile to the full gallery', async ({ owner: page }) => {
  test.setTimeout(120_000);
  await newPost(page, 'gallery');
  await page.getByLabel('Title', { exact: true }).fill('Seven views');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await page.getByLabel('Photos', { exact: false }).setInputFiles(
    Array(7).fill(resolve(repoRoot, 'src/Homestead/wwwroot/social-default.png'))
  );
  await page.getByRole('button', { name: 'Upload photo', exact: true }).click();
  await expect(page.locator('#article-images > [data-media]')).toHaveCount(7);
  const publicPath = await publish(page);
  await page.goto('/galleries/');

  const mosaic = page.locator('[data-town-gallery="5"]');
  const tiles = mosaic.locator('figure');
  await expect(tiles).toHaveCount(5);
  const [first, second, third, fourth, fifth] = await Promise.all(
    Array.from({ length: 5 }, (_, index) => tiles.nth(index).boundingBox())
  );
  expect(second.x).toBeCloseTo(first.x, 0);
  expect(third.y).toBeCloseTo(first.y, 0);
  expect(third.x - (first.x + first.width)).toBeCloseTo(2, 0);
  expect(fourth.y - (third.y + third.height)).toBeCloseTo(2, 0);
  expect(fifth.y + fifth.height).toBeCloseTo(second.y + second.height, 0);
  const firstImage = await tiles.first().locator('img').boundingBox();
  expect(firstImage.height).toBeCloseTo(first.height, 0);
  expect(firstImage.width).toBeCloseTo(first.width, 0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);

  const overflow = mosaic.getByRole('link', { name: 'View gallery, 2 more photos' });
  await expect(overflow).toHaveText('+2');
  await overflow.focus();
  await overflow.press('Enter');
  await expect(page).toHaveURL(new RegExp(publicPath + '$'));
  await expect(page.locator('[data-gallery-photos] figure')).toHaveCount(7);
});
