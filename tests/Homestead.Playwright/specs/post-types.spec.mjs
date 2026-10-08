import { test, expect, newPost, publish } from '../support/fixtures.mjs';

test('a text note publishes without a title and public navigation works without JavaScript', async ({ owner: page, browser, site }) => {
  await newPost(page, 'note');
  await page.getByLabel('Note (Markdown)', { exact: true }).fill('A quiet browser-tested afternoon.');
  await page.getByLabel('Tags (comma separated)').fill('Home');
  const publicPath = await publish(page);
  const reader = await browser.newContext({ baseURL: site.baseURL, ignoreHTTPSErrors: true, javaScriptEnabled: false });
  try {
    const publicPage = await reader.newPage();
    await publicPage.goto('/');
    await publicPage.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Notes', exact: true }).click();
    await expect(publicPage).toHaveURL(/\/notes\/$/);
    await expect(publicPage.getByText('A quiet browser-tested afternoon.')).toBeVisible();
    await publicPage.goto(publicPath);
    await publicPage.getByRole('navigation', { name: 'Post tags' }).getByRole('link', { name: 'Home', exact: true }).click();
    await expect(publicPage).toHaveURL(/\/tags\/home\/$/);
    await expect(publicPage.getByText('A quiet browser-tested afternoon.')).toBeVisible();
  } finally {
    await reader.close();
  }
});

test('review validation keeps an incomplete draft private, then publishes its rating', async ({ owner: page, context }) => {
  await newPost(page, 'review');
  await page.getByLabel('Subject', { exact: true }).fill('Browser coffee');
  await page.getByLabel('Review (Markdown)', { exact: true }).fill('A **warm** cup.');
  await page.getByRole('button', { name: 'Publish', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'A review needs' })).toContainText('rating');
  expect((await page.request.get('/posts/browser-coffee/')).status()).toBe(404);
  await page.getByLabel('Rating (1–5)', { exact: true }).fill('5');
  const publicPath = await publish(page);
  const publicPage = await context.newPage();
  await publicPage.goto(publicPath);
  await expect(publicPage.getByRole('heading', { name: 'Browser coffee', exact: true })).toBeVisible();
  await expect(publicPage.getByText('5/5', { exact: false })).toBeVisible();
  await expect(publicPage.locator('main strong')).toHaveText(['Browser coffee', 'warm']);
});

test('guide steps can be added and reordered before publication', async ({ owner: page, context }) => {
  await newPost(page, 'guide');
  await page.getByLabel('Title', { exact: true }).fill('Browser guide');
  await page.getByRole('button', { name: 'Add step', exact: true }).click();
  await page.getByRole('textbox', { name: 'Step 1 instructions (Markdown)', exact: true }).fill('Open the door.');
  await page.getByRole('button', { name: 'Add step', exact: true }).click();
  await page.getByRole('textbox', { name: 'Step 2 instructions (Markdown)', exact: true }).fill('Put the kettle on.');
  await page.getByRole('button', { name: 'Move step up', exact: true }).nth(1).click();
  await expect(page.getByRole('textbox', { name: 'Step 1 instructions (Markdown)', exact: true })).toHaveValue('Put the kettle on.');
  const publicPath = await publish(page);
  const publicPage = await context.newPage();
  await publicPage.goto(publicPath);
  await expect(publicPage.locator('main ol > li p')).toHaveText(['Put the kettle on.', 'Open the door.']);
});
