import { test, expect, newPost, publish, saved } from '../support/fixtures.mjs';

async function saveDraft(page) {
  await Promise.all([
    page.waitForEvent('domcontentloaded'),
    page.getByRole('button', { name: 'Save draft', exact: true }).click()
  ]);
  await expect(page.getByRole('status').filter({ hasText: 'Draft saved.' })).toBeVisible();
}

for (const javaScriptEnabled of [true, false]) {
  test.describe(`review stars with JavaScript ${javaScriptEnabled ? 'enabled' : 'disabled'}`, () => {
    test.use({ javaScriptEnabled });

    test('selects, changes, and displays ratings across all themes', async ({ owner: page }, testInfo) => {
      test.setTimeout(180_000);
      await newPost(page, 'review');
      const rating = page.getByRole('group', { name: 'Rating (1–5) (required to publish)', exact: true });
      await expect(rating.getByRole('radio')).toHaveCount(5);
      await expect(rating.locator('input:checked')).toHaveCount(0);
      await expect(page.getByText('Clear rating', { exact: true })).toHaveCount(0);
      await page.getByLabel('Subject (required to publish)', { exact: true }).fill('Star coffee');
      await page.getByLabel('Review (required to publish)', { exact: true }).fill('A warm cup.');
      await saveDraft(page);
      const unratedEditorPath = new URL(page.url()).pathname;
      await page.goto(unratedEditorPath.replace(/\/edit$/, '/preview'));
      await expect(page.getByText('Not rated', { exact: true })).toBeVisible();
      await page.goto(unratedEditorPath);
      await page.getByRole('button', { name: 'Publish', exact: true }).click();
      await expect(page.getByRole('status').filter({ hasText: 'A review needs' })).toContainText('rating');
      await rating.getByRole('radio', { name: '3 stars', exact: true }).check();
      await page.keyboard.press('ArrowRight');
      await expect(rating.getByRole('radio', { name: '4 stars', exact: true })).toBeChecked();
      if (javaScriptEnabled) await saved(page);
      else await saveDraft(page);
      await page.reload();
      await expect(rating.getByRole('radio', { name: '4 stars', exact: true })).toBeChecked();
      for (const colorScheme of ['light', 'dark']) {
        await page.emulateMedia({ colorScheme });
        await expect(rating).toBeVisible();
        const stars = rating.locator('.mask-star');
        for (const star of await stars.all()) {
          const box = await star.boundingBox();
          expect(box.width).toBeGreaterThanOrEqual(44);
          expect(box.height).toBeGreaterThanOrEqual(44);
        }
        await page.screenshot({ path: testInfo.outputPath(`editor-${colorScheme}.png`), fullPage: true });
      }
      if (javaScriptEnabled) await page.getByRole('tab', { name: 'Details', exact: true }).click();
      await page.getByLabel('Tags (comma separated)').fill('Coffee');
      const publicPath = await publish(page);
      const editorPath = new URL(page.url()).pathname;
      if (javaScriptEnabled) await page.getByRole('tab', { name: 'Write', exact: true }).click();
      await rating.getByRole('radio', { name: '2 stars', exact: true }).check();
      await saveDraft(page);
      await page.reload();
      await expect(rating.getByRole('radio', { name: '2 stars', exact: true })).toBeChecked();
      await rating.getByRole('radio', { name: '1 star', exact: true }).check();
      await page.keyboard.press('ArrowLeft');
      await expect(rating.getByRole('radio', { name: '5 stars', exact: true })).toBeChecked();
      await rating.getByRole('radio', { name: '2 stars', exact: true }).check();
      await saveDraft(page);

      for (const theme of ['porch', 'journal', 'ledger', 'folio', 'townsquare']) {
        await page.goto('/admin/settings#theme');
        await page.locator('#tab-about').click();
        await page.getByLabel('Author display name', { exact: true }).fill('Ada');
        await page.locator('#tab-theme').click();
        await page.getByLabel('Public theme', { exact: true }).selectOption(theme);
        await page.getByRole('button', { name: 'Save settings', exact: true }).click();
        await expect(page.getByRole('status')).toContainText('Settings saved and public site rebuilt.');
        for (const colorScheme of ['light', 'dark']) {
          await page.emulateMedia({ colorScheme });
          for (const path of ['/', '/reviews/', '/tags/coffee/', publicPath, editorPath.replace(/\/edit$/, '/preview')]) {
            await page.goto(path);
            const score = path.endsWith('/preview') ? 2 : 4;
            const display = page.getByRole('img', { name: `Rated ${score} out of 5`, exact: true });
            await expect(display).toBeVisible();
            await expect(display.locator('.mask-star')).toHaveCount(5);
            await expect(display.locator('[aria-current="true"]')).toHaveCount(1);
            const opacity = await display.locator('.mask-star').evaluateAll(stars => stars.map(star => getComputedStyle(star).opacity));
            expect(opacity).toEqual(Array.from({ length: 5 }, (_, i) => i < score ? '1' : '0.2'));
            await expect(display.getByRole('radio')).toHaveCount(0);
            expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
            await expect(page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Home', exact: true })).toBeVisible();
            if (path === publicPath) await page.screenshot({ path: testInfo.outputPath(`${theme}-${colorScheme}.png`), fullPage: true });
          }
        }
      }
    });
  });
}
