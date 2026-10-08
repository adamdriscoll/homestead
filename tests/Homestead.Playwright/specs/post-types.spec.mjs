import { test, expect, newPost, publish, saved } from '../support/fixtures.mjs';

test('a check-in autosaves its location, previews privately, and publishes without text or a title', async ({ owner: page, browser, site }) => {
  await newPost(page, 'check-in');
  await page.getByRole('button', { name: 'Publish', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'A check-in needs' })).toContainText('place name');
  await page.getByLabel('Place name (required to publish)', { exact: true }).fill('Browser café');
  await page.getByLabel('Address (optional)', { exact: true }).fill('123 Quiet Street');
  await page.getByLabel('Latitude (optional)', { exact: true }).fill('41.88');
  await page.getByLabel('Longitude (optional)', { exact: true }).fill('-87.63');
  await saved(page);
  await page.reload();
  await expect(page.getByLabel('Place name (required to publish)', { exact: true })).toHaveValue('Browser café');
  await page.getByRole('tab', { name: 'Preview', exact: true }).click();
  await expect(page.frameLocator('#draft-preview').getByText('Location:', { exact: false })).toContainText('123 Quiet Street');
  const publicPath = await publish(page);
  const reader = await browser.newContext({ baseURL: site.baseURL, ignoreHTTPSErrors: true, javaScriptEnabled: false });
  try {
    const publicPage = await reader.newPage();
    await publicPage.goto('/');
    await publicPage.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Check-ins', exact: true }).click();
    await expect(publicPage).toHaveURL(/\/checkins\/$/);
    await expect(publicPage.getByText('Location:', { exact: false })).toContainText('41.88, -87.63');
    await publicPage.getByRole('link', { name: 'Check-in at Browser café', exact: true }).click();
    await expect(publicPage).toHaveURL(site.baseURL + publicPath);
    await expect(publicPage.getByRole('heading', { name: 'Check-in at Browser café', exact: true })).toBeVisible();
  } finally {
    await reader.close();
  }
});

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
