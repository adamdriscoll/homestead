import { resolve } from 'node:path';
import { repoRoot } from '../support/docker.mjs';
import { test, expect, saved } from '../support/fixtures.mjs';

test('dashboard notes autosave, preview, reject stale tabs, and publish into recent cards', async ({ owner: page, context }, testInfo) => {
  await expect(page.getByRole('textbox', { name: 'Title', exact: true })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Create something' })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Recent posts' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'All posts', exact: true })).toHaveCount(0);
  await expect(page.locator('body > footer')).toContainText('Last successful build:');
  await expect(page.locator('main')).not.toContainText('Last successful build:');
  await page.getByLabel('Note', { exact: true }).fill('A quick dashboard note.');
  await saved(page);
  await expect(page).toHaveURL(/\/admin\?id=[\da-f-]+$/);
  await page.reload();
  await expect(page.getByLabel('Note', { exact: true })).toHaveValue('A quick dashboard note.');
  const stale = await context.newPage();
  await stale.goto(page.url());
  await page.getByLabel('Note', { exact: true }).fill('A public dashboard note.');
  await page.getByRole('tab', { name: 'Preview', exact: true }).click();
  await expect(page.frameLocator('#draft-preview').getByText('A public dashboard note.')).toBeVisible();
  await stale.getByLabel('Note', { exact: true }).fill('A stale dashboard edit.');
  await expect(stale.locator('#save-status')).toHaveAttribute('data-state', 'conflict');
  await stale.close();
  await page.getByRole('button', { name: 'Publish', exact: true }).click();
  await expect(page).toHaveURL(/\/admin$/);
  await expect(page.getByLabel('Note', { exact: true })).toHaveValue('');
  const cards = page.getByRole('region', { name: 'Recent posts' });
  await expect(cards.locator('article')).toHaveCount(1);
  await expect(cards.getByRole('link', { name: 'A public dashboard note.' })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath('dashboard.png'), fullPage: true });
  await cards.getByRole('link').click();
  await expect(page.getByRole('textbox', { name: 'Title', exact: true })).toHaveCount(0);
  const publicPath = await page.getByRole('link', { name: 'Public post', exact: true }).getAttribute('href');
  expect(await (await page.request.get(publicPath)).text()).toContain('A public dashboard note.');
});

test('dashboard photo arrangement keeps the note ID and other buttons open their editors', async ({ owner: page }) => {
  await page.getByRole('tab', { name: 'Images', exact: true }).click();
  await page.getByLabel('Photos', { exact: false }).setInputFiles(resolve(repoRoot, 'src/Homestead/wwwroot/icon-192.png'));
  await page.getByRole('button', { name: 'Upload photo', exact: true }).click();
  await expect(page.locator('#article-images > [data-media]')).toHaveCount(1);
  await saved(page);
  await page.getByRole('button', { name: 'Remove photo', exact: true }).click();
  await expect(page).toHaveURL(/\/admin\?id=[\da-f-]+$/);
  await page.getByRole('tab', { name: 'Images', exact: true }).click();
  await expect(page.locator('#article-images > [data-media]')).toHaveCount(0);
  for (const type of ['article', 'review', 'guide', 'gallery', 'check-in']) {
    await page.goto('/admin');
    await page.getByRole('button', { name: `New ${type}`, exact: true }).click();
    await expect(page.getByRole('heading', { name: `Your ${type}`, exact: true })).toBeVisible();
  }
});

test.describe('without JavaScript', () => {
  test.use({ javaScriptEnabled: false });
  test('dashboard ordinary forms save and publish a note', async ({ owner: page }) => {
    await page.getByLabel('Note', { exact: true }).fill('A dashboard note without JavaScript.');
    await page.getByRole('button', { name: 'Save draft', exact: true }).click();
    await expect(page).toHaveURL(/\/admin\?id=[\da-f-]+$/);
    await expect(page.getByLabel('Note', { exact: true })).toHaveValue('A dashboard note without JavaScript.');
    await page.getByRole('button', { name: 'Publish', exact: true }).click();
    await expect(page).toHaveURL(/\/admin$/);
    await expect(page.getByLabel('Note', { exact: true })).toHaveValue('');
    await expect(page.getByRole('region', { name: 'Recent posts' }).getByRole('link', { name: 'A dashboard note without JavaScript.' })).toBeVisible();
  });
});
