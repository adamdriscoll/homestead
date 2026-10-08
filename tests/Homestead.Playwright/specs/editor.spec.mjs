import { test, expect, newPost, saved, publish } from '../support/fixtures.mjs';

test('autosave, private preview, publication, update, unpublish and confirmed deletion', async ({ owner: page, context }) => {
  await newPost(page);
  const editorURL = page.url();
  await page.getByLabel('Title', { exact: false }).fill('Browser article');
  await page.getByLabel('Body').fill('The original **public story**.');
  await saved(page);
  await page.reload();
  await expect(page.getByLabel('Body')).toHaveValue('The original **public story**.');

  // Preview must save outstanding edits before loading its private iframe.
  await page.getByLabel('Body').fill('The preview **public story**.');
  await page.getByRole('tab', { name: 'Preview', exact: true }).click();
  await expect(page.frameLocator('#draft-preview').getByText('The preview public story.')).toBeVisible();
  expect((await page.request.get('/posts/browser-article/')).status()).toBe(404);
  await page.getByRole('tab', { name: 'Write', exact: true }).click();
  const publicPath = await publish(page);
  expect(publicPath).toBe('/posts/browser-article/');

  const publicPage = await context.newPage();
  await publicPage.goto(publicPath);
  await expect(publicPage.getByText('The preview public story.')).toBeVisible();
  await page.getByLabel('Body').fill('Private pending browser edits.');
  await saved(page);
  await publicPage.reload();
  await expect(publicPage.getByText('The preview public story.')).toBeVisible();
  await expect(publicPage.getByText('Private pending browser edits.')).toHaveCount(0);

  // Exercise publication both after autosave (reentrant-submit regression) and while dirty.
  await publish(page, 'Update published post');
  await publicPage.reload();
  await expect(publicPage.getByText('Private pending browser edits.')).toBeVisible();
  await page.getByLabel('Body').fill('The final published browser story.');
  await publish(page, 'Update published post');
  await publicPage.reload();
  await expect(publicPage.getByText('The final published browser story.')).toBeVisible();

  await page.getByRole('button', { name: 'Unpublish', exact: true }).click();
  await expect(page.getByRole('link', { name: 'Open public post' })).toHaveCount(0);
  expect((await page.request.get(publicPath)).status()).toBe(404);
  await page.getByRole('button', { name: 'Delete post', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).first().click();
  await expect(dialog).not.toBeVisible();
  await page.getByRole('button', { name: 'Delete post', exact: true }).click();
  await dialog.getByRole('button', { name: 'Delete post', exact: true }).click();
  await expect(page).toHaveURL(/\/admin\/posts$/);
  await expect(page.getByRole('link', { name: 'Browser article', exact: true })).toHaveCount(0);
  expect((await page.request.get(editorURL)).status()).toBe(404);
});

test('a stale tab reports conflict and preserves its unsaved text', async ({ owner: page, context }) => {
  await newPost(page);
  await page.getByLabel('Title', { exact: false }).fill('Concurrent browser article');
  await page.getByLabel('Body').fill('Starting draft.');
  await saved(page);
  const stale = await context.newPage();
  await stale.goto(page.url());
  await page.getByLabel('Body').fill('Newer draft from the first tab.');
  await saved(page);
  await stale.getByLabel('Body').fill('Keep this unsaved text in the stale tab.');
  await expect(stale.locator('#save-status')).toHaveAttribute('data-state', 'conflict');
  await expect(stale.getByRole('alert')).toContainText('Another tab saved this draft');
  await stale.getByRole('button', { name: 'Publish', exact: true }).click();
  await expect(stale.getByLabel('Body')).toHaveValue('Keep this unsaved text in the stale tab.');
  await page.reload();
  await expect(page.getByLabel('Body')).toHaveValue('Newer draft from the first tab.');
  expect((await page.request.get('/posts/concurrent-browser-article/')).status()).toBe(404);
});
