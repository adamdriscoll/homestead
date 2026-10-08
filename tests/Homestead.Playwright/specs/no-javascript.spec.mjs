import { test, expect, newPost, publish } from '../support/fixtures.mjs';

test.use({ javaScriptEnabled: false });

test('an empty form does not save a draft without JavaScript', async ({ owner: page }) => {
  await newPost(page);
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Add content to save a draft.' })).toBeVisible();
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Your article', exact: true })).toBeVisible();
  await page.goto('/admin/posts');
  await expect(page.getByText('No posts here yet. Choose a post type to start.')).toBeVisible();
});

test('ordinary forms save, preview and publish an article without JavaScript', async ({ owner: page, context }) => {
  await newPost(page);
  await expect(page.getByRole('tablist')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Add Location', exact: true })).toHaveCount(0);
  await page.getByLabel('Title', { exact: false }).fill('Browser forms article');
  await page.getByLabel('Body').fill('Written without JavaScript.');
  await page.getByLabel('Tags (comma separated)').fill('Home');
  await page.getByLabel('Place name', { exact: true }).fill('Quiet café');
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Draft saved.' })).toBeVisible();
  const popup = page.waitForEvent('popup');
  await page.getByRole('link', { name: 'Preview saved draft', exact: true }).click();
  const preview = await popup;
  await expect(preview.getByText('Written without JavaScript.')).toBeVisible();
  expect((await page.request.get('/posts/browser-forms-article/')).status()).toBe(404);
  const publicPath = await publish(page);
  const publicPage = await context.newPage();
  await publicPage.goto(publicPath);
  await expect(publicPage.getByText('Written without JavaScript.')).toBeVisible();
  await expect(publicPage.getByText('Location:', { exact: false })).toContainText('Quiet café');
  await expect(publicPage.getByRole('navigation', { name: 'Post tags' }).getByRole('link', { name: 'Home', exact: true })).toBeVisible();
});
