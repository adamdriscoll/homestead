import { test, expect, createAdministrator, signIn } from '../support/fixtures.mjs';

test('setup closes after account creation and logout protects authoring', async ({ page, site }) => {
  await page.goto('/admin/posts');
  await expect(page).toHaveURL(/\/setup$/);
  await createAdministrator(page, site);
  expect((await page.request.get('/setup')).status()).toBe(404);
  await signIn(page, site);
  const cookie = (await page.context().cookies()).find(value => value.name === 'Homestead.Auth');
  expect(cookie).toMatchObject({ secure: true, httpOnly: true });
  await page.getByLabel('Account menu', { exact: true }).click();
  await page.getByRole('button', { name: 'Sign out' }).click();
  await page.goto('/admin/posts');
  await expect(page.getByRole('heading', { name: 'Welcome back' })).toBeVisible();
  expect((await page.request.get('/homestead.db')).status()).toBe(404);
  await page.goto('/');
  await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible();
});
