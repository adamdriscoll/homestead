import { test, expect, newPost, publish } from '../support/fixtures.mjs';

for (const javaScriptEnabled of [true, false]) {
  test.describe(`post type settings with JavaScript ${javaScriptEnabled ? 'enabled' : 'disabled'}`, () => {
    test.use({ javaScriptEnabled });

    test('hides a published type and restores its saved snapshot without publishing draft edits', async ({ owner: page }) => {
      await newPost(page);
      await page.getByLabel('Title', { exact: false }).fill('A preserved article');
      await page.getByLabel('Body').fill('Original published content.');
      const publicPath = await publish(page);
      await page.getByLabel('Body').fill('Private pending edits.');
      await page.getByRole('button', { name: 'Save draft', exact: true }).click();
      await expect(page.getByRole('status').filter({ hasText: 'Draft saved.' })).toBeVisible();

      await page.goto('/admin/settings');
      await page.locator('#tab-about').click();
      await page.getByLabel('Author display name', { exact: true }).fill('Ada');
      await page.locator('#tab-posts').click();
      const types = page.getByRole('group', { name: 'Post types', exact: true });
      await expect(types.getByRole('checkbox')).toHaveCount(6);
      for (const checkbox of await types.getByRole('checkbox').all()) await expect(checkbox).toBeChecked();
      await types.getByRole('checkbox', { name: 'Articles', exact: true }).uncheck();
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('Settings saved and public site rebuilt.');
      if (javaScriptEnabled) await expect(page).toHaveURL(/#posts$/);
      await expect(types.getByRole('checkbox', { name: 'Articles', exact: true })).not.toBeChecked();
      await page.reload();
      await expect(types.getByRole('checkbox', { name: 'Articles', exact: true })).not.toBeChecked();
      await page.goto('/admin/posts');
      await expect(page.getByRole('button', { name: 'New article', exact: true })).toHaveCount(0);
      await expect(page.getByRole('link', { name: 'A preserved article', exact: true })).toHaveCount(0);
      await expect(page.getByRole('button', { name: 'New note', exact: true })).toBeVisible();
      expect((await page.request.get(publicPath)).status()).toBe(404);
      expect((await page.request.get('/articles/')).status()).toBe(404);
      await page.goto('/');
      await expect(page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Articles', exact: true })).toHaveCount(0);

      await page.goto('/admin/settings');
      await page.locator('#tab-posts').click();
      await types.getByRole('checkbox', { name: 'Articles', exact: true }).check();
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('Settings saved and public site rebuilt.');
      await page.goto(publicPath);
      await expect(page.getByText('Original published content.', { exact: true })).toBeVisible();
      await expect(page.getByText('Private pending edits.', { exact: true })).toHaveCount(0);
      await page.goto('/admin/posts');
      await page.getByRole('link', { name: 'A preserved article', exact: true }).click();
      await expect(page.getByLabel('Body')).toHaveValue('Private pending edits.');
    });
  });
}
