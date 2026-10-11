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
  await page.getByRole('textbox', { name: 'Note', exact: true }).fill('A quick dashboard note.');
  await saved(page);
  await expect(page).toHaveURL(/\/admin\?id=[\da-f-]+$/);
  await page.reload();
  await expect(page.getByRole('textbox', { name: 'Note', exact: true })).toHaveValue('A quick dashboard note.');
  const stale = await context.newPage();
  await stale.goto(page.url());
  await page.getByRole('textbox', { name: 'Note', exact: true }).fill('A public dashboard note.');
  await page.getByRole('tab', { name: 'Preview', exact: true }).click();
  await expect(page.frameLocator('#draft-preview').getByText('A public dashboard note.')).toBeVisible();
  await stale.getByRole('textbox', { name: 'Note', exact: true }).fill('A stale dashboard edit.');
  await expect(stale.locator('#save-status')).toHaveAttribute('data-state', 'conflict');
  await stale.getByRole('navigation', { name: 'Post types' }).getByRole('link', { name: 'Article', exact: true }).click();
  await expect(stale.getByRole('navigation', { name: 'Post types' }).getByRole('link', { name: 'Note', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(stale.getByRole('textbox', { name: 'Note', exact: true })).toHaveValue('A stale dashboard edit.');
  await stale.close();
  await page.getByRole('button', { name: 'Publish', exact: true }).click();
  await expect(page).toHaveURL(/\/admin$/);
  await expect(page.getByRole('textbox', { name: 'Note', exact: true })).toHaveValue('');
  const cards = page.getByRole('region', { name: 'Recent posts' });
  await expect(cards.locator('article')).toHaveCount(1);
  await expect(cards.getByRole('link', { name: 'A public dashboard note.' })).toBeVisible();
  await page.screenshot({ path: testInfo.outputPath('dashboard.png'), fullPage: true });
  await cards.getByRole('link').click();
  await expect(page.getByRole('textbox', { name: 'Title', exact: true })).toHaveCount(0);
  const publicPath = await page.getByRole('link', { name: 'Public post', exact: true }).getAttribute('href');
  expect(await (await page.request.get(publicPath)).text()).toContain('A public dashboard note.');
});

test('dashboard photo arrangement keeps the note ID and styled links open each routed editor', async ({ owner: page }) => {
  await page.getByRole('tab', { name: 'Images', exact: true }).click();
  await page.getByLabel('Photos', { exact: false }).setInputFiles(resolve(repoRoot, 'src/Homestead/wwwroot/icon-192.png'));
  await page.getByRole('button', { name: 'Upload photo', exact: true }).click();
  await expect(page.locator('#article-images > [data-media]')).toHaveCount(1);
  await saved(page);
  await page.getByRole('button', { name: 'Remove photo', exact: true }).click();
  await expect(page).toHaveURL(/\/admin\?id=[\da-f-]+&editorTab=images$/);
  await page.getByRole('tab', { name: 'Images', exact: true }).click();
  await expect(page.locator('#article-images > [data-media]')).toHaveCount(0);
  for (const [type, field] of [['Article', 'Title'], ['Review', 'Subject'], ['Guide', 'Introduction'], ['Gallery', 'Introduction'], ['Check-in', 'Visit'], ['Fitness activity', 'Message']]) {
    const tab = page.getByRole('navigation', { name: 'Post types' }).getByRole('link', { name: type, exact: true });
    await expect(tab.locator('svg use')).toHaveAttribute('href', /\/icons\/toolbar.svg#/);
    await tab.click();
    await expect(page).toHaveURL(/\/admin\/posts\/[\da-f-]+\/edit\?newPostType=\w+$/);
    await expect(tab).toHaveAttribute('aria-current', 'page');
    await expect(page.getByLabel(field, { exact: false }).first()).toBeVisible();
  }
});

test('routed type links save pending edits and saved drafts reopen through Posts', async ({ owner: page }, testInfo) => {
  const navigation = page.getByRole('navigation', { name: 'Post types' });
  await page.getByRole('textbox', { name: 'Note', exact: true }).fill('Keep this note while writing an article.');
  await navigation.getByRole('link', { name: 'Article', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Your article', exact: true })).toBeVisible();
  await page.getByRole('textbox', { name: 'Title', exact: false }).fill('An article from the dashboard');
  await page.getByLabel('Body', { exact: false }).fill('Independent article content.');
  await navigation.getByRole('link', { name: 'Note', exact: true }).click();
  await expect(page).toHaveURL(/\/admin$/);
  await expect(page.getByRole('textbox', { name: 'Note', exact: true })).toHaveValue('');
  await expect(page.getByRole('region', { name: 'Recent posts' }).getByRole('link', { name: 'Keep this note while writing an article.', exact: true })).toBeVisible();
  await page.goto('/admin/posts');
  await page.getByRole('link', { name: 'An article from the dashboard', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Title', exact: false })).toHaveValue('An article from the dashboard');
  await expect(page.getByLabel('Body', { exact: false })).toHaveValue('Independent article content.');
  const articleUrl = page.url();
  await navigation.getByRole('link', { name: 'Review', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByLabel('Subject', { exact: false }).first()).toBeVisible();
  await expect(navigation.getByRole('link', { name: 'Review', exact: true })).toHaveAttribute('aria-current', 'page');
  await expect(page.locator('body')).toHaveJSProperty('scrollWidth', await page.locator('body').evaluate(body => body.clientWidth));
  await page.screenshot({ path: testInfo.outputPath('dashboard-tabs.png'), fullPage: true });
  await page.goto(articleUrl);
  await page.getByRole('button', { name: 'Publish', exact: true }).click();
  await expect(page).toHaveURL(articleUrl);
  await expect(page.getByRole('link', { name: 'Public post', exact: true })).toBeVisible();
  await page.goto('/admin');
  await expect(page.getByRole('region', { name: 'Recent posts' }).getByRole('link', { name: 'An article from the dashboard', exact: true })).toBeVisible();
});

test('dashboard respects enabled types and restores imported fitness tracks in both appearances', async ({ owner: page }, testInfo) => {
  await page.goto('/admin/settings');
  await page.locator('#tab-about').click();
  await page.getByLabel('Author display name', { exact: true }).fill('Ada');
  await page.locator('#tab-posts').click();
  const types = page.getByRole('group', { name: 'Post types', exact: true });
  await types.getByRole('checkbox', { name: 'Notes', exact: true }).uncheck();
  await types.getByRole('checkbox', { name: 'Reviews', exact: true }).uncheck();
  await page.getByRole('button', { name: 'Save settings', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('Settings saved and public site rebuilt.');
  await page.goto('/admin');
  const tabs = page.getByRole('navigation', { name: 'Post types' });
  await expect(tabs.getByRole('link')).toHaveCount(5);
  await expect(tabs.getByRole('link', { name: 'Note', exact: true })).toHaveCount(0);
  await expect(tabs.getByRole('link', { name: 'Review', exact: true })).toHaveCount(0);
  await expect(page.locator('#article-editor')).toHaveCount(0);
  await tabs.getByRole('link', { name: 'Fitness activity', exact: true }).click();
  await page.getByLabel('GPX file', { exact: true }).setInputFiles(resolve(repoRoot, 'tests/Homestead.Playwright/fixtures/activity.gpx'));
  await page.getByRole('button', { name: 'Import GPX', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Download original GPX', exact: true })).toBeVisible();
  const fitnessUrl = page.url();
  await tabs.getByRole('link', { name: 'Article', exact: true }).click();
  await page.goto(fitnessUrl);
  await expect(page.getByRole('link', { name: 'Download original GPX', exact: true })).toBeVisible();
  await expect(page.locator('[data-fitness-activity]').getByRole('tab', { name: 'Map', exact: true })).toHaveAttribute('aria-selected', 'true');
  for (const appearance of ['Dark', 'Light']) {
    await page.getByLabel('Account menu', { exact: true }).click();
    await page.getByRole('button', { name: `${appearance} appearance`, exact: true }).click();
    await page.keyboard.press('Escape');
    await expect(page.locator('html')).toHaveAttribute('data-theme', appearance.toLowerCase());
    await expect(tabs.getByRole('link', { name: 'Fitness activity', exact: true })).toHaveAttribute('aria-current', 'page');
    await page.screenshot({ path: testInfo.outputPath(`dashboard-${appearance.toLowerCase()}.png`), fullPage: true });
  }
});

test.describe('without JavaScript', () => {
  test.use({ javaScriptEnabled: false });
  test('dashboard ordinary forms save and publish a note', async ({ owner: page }) => {
    await page.getByRole('textbox', { name: 'Note', exact: true }).fill('A dashboard note without JavaScript.');
    await page.getByRole('button', { name: 'Save draft', exact: true }).click();
    await expect(page).toHaveURL(/\/admin\?id=[\da-f-]+$/);
    await expect(page.getByRole('textbox', { name: 'Note', exact: true })).toHaveValue('A dashboard note without JavaScript.');
    await page.getByRole('button', { name: 'Publish', exact: true }).click();
    await expect(page).toHaveURL(/\/admin$/);
    await expect(page.getByRole('textbox', { name: 'Note', exact: true })).toHaveValue('');
    await expect(page.getByRole('region', { name: 'Recent posts' }).getByRole('link', { name: 'A dashboard note without JavaScript.' })).toBeVisible();
    await page.getByRole('navigation', { name: 'Post types' }).getByRole('link', { name: 'Article', exact: true }).click();
    await page.getByRole('textbox', { name: 'Title', exact: false }).fill('Dashboard article without JavaScript');
    await page.getByLabel('Body', { exact: false }).fill('Ordinary article form.');
    await page.getByRole('button', { name: 'Save draft', exact: true }).click();
    await expect(page).toHaveURL(/\/admin\/posts\/[\da-f-]+\/edit$/);
    await page.getByRole('button', { name: 'Publish', exact: true }).click();
    await expect(page).toHaveURL(/\/admin\/posts\/[\da-f-]+\/edit$/);
    await expect(page.getByRole('link', { name: 'Public post', exact: true })).toBeVisible();
    await page.goto('/admin');
    await expect(page.getByRole('region', { name: 'Recent posts' }).getByRole('link', { name: 'Dashboard article without JavaScript', exact: true })).toBeVisible();
  });
});
