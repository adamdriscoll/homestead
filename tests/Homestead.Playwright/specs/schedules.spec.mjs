import { test, expect, newPost, saved } from '../support/fixtures.mjs';

for (const javaScriptEnabled of [true, false]) {
  test.describe(javaScriptEnabled ? 'enhanced scheduling' : 'ordinary scheduling forms', () => {
    test.use({ javaScriptEnabled });
    test('schedule saves the draft, replaces its frozen copy and cancels privately', async ({ owner: page }) => {
      await newPost(page);
      await page.getByLabel('Title', { exact: false }).fill('Browser scheduled story');
      await page.getByLabel('Body').fill('Frozen browser content');
      if (javaScriptEnabled) await page.getByRole('tab', { name: 'Details', exact: true }).click();
      const future = new Date(Date.now() + 86400000).toISOString().slice(0, 16);
      await page.getByLabel('Publication date and time').fill(future);
      await page.getByRole('button', { name: 'Schedule post', exact: true }).click();
      await expect(page.getByRole('status').filter({ hasText: 'Post scheduled.' })).toBeVisible();
      if (javaScriptEnabled) await page.getByRole('tab', { name: 'Details', exact: true }).click();
      await expect(page.getByRole('button', { name: 'Replace schedule', exact: true })).toBeVisible();
      expect((await page.request.get('/posts/browser-scheduled-story/')).status()).toBe(404);
      const editorURL = page.url();
      await page.goto('/admin/posts?scheduled=true');
      await expect(page.getByText('Scheduled for', { exact: false })).toBeVisible();
      await page.goto(editorURL);
      if (javaScriptEnabled) await page.getByRole('tab', { name: 'Write', exact: true }).click();
      await page.getByLabel('Body').fill('Replacement browser content');
      if (javaScriptEnabled) await page.getByRole('tab', { name: 'Details', exact: true }).click();
      await page.getByRole('button', { name: 'Replace schedule', exact: true }).click();
      await expect(page.getByRole('status').filter({ hasText: 'Post scheduled.' })).toBeVisible();
      for (const colorScheme of ['dark', 'light']) {
        await page.emulateMedia({ colorScheme });
        if (javaScriptEnabled) await page.getByRole('tab', { name: 'Details', exact: true }).click();
        await expect(page.getByLabel('Publication date and time')).toBeVisible();
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      }
      await page.getByRole('button', { name: 'Cancel schedule', exact: true }).click();
      await expect(page.getByRole('status').filter({ hasText: 'Schedule cancelled.' })).toBeVisible();
      await expect(page.getByLabel('Body')).toHaveValue('Replacement browser content');
      await page.goto('/admin/posts?scheduled=true');
      await expect(page.getByText('No posts here yet. Choose a post type to start.')).toBeVisible();
      expect((await page.request.get('/posts/browser-scheduled-story/')).status()).toBe(404);
    });
  });
}

test('the hosted worker publishes due content while later autosaves remain private', async ({ owner: page }) => {
  test.setTimeout(180000);
  await newPost(page);
  await page.getByLabel('Title', { exact: false }).fill('Worker scheduled story');
  await page.getByLabel('Body').fill('Frozen worker content');
  await page.getByRole('tab', { name: 'Details', exact: true }).click();
  // Site settings default to UTC; select the next minute and allow one worker tick.
  const due = new Date(Math.floor(Date.now() / 60000) * 60000 + 60000).toISOString().slice(0, 16);
  await page.getByLabel('Publication date and time').fill(due);
  await page.getByRole('button', { name: 'Schedule post', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Post scheduled.' })).toBeVisible();
  await page.getByRole('tab', { name: 'Write', exact: true }).click();
  await page.getByLabel('Body').fill('PRIVATE later worker edits');
  await saved(page);
  await expect.poll(async () => {
    const response = await page.request.get('/posts/worker-scheduled-story/');
    return response.status();
  }, { timeout: 120000, intervals: [1000, 3000, 5000] }).toBe(200);
  const response = await page.request.get('/posts/worker-scheduled-story/');
  const html = await response.text();
  expect(html).toContain('Frozen worker content');
  expect(html).not.toContain('PRIVATE later worker edits');
  await page.reload();
  await expect(page.getByRole('link', { name: 'Public post', exact: true })).toBeVisible();
  await expect(page.getByLabel('Body')).toHaveValue('PRIVATE later worker edits');
});
