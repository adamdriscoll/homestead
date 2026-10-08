import { test, expect, newPost, saved, publish } from '../support/fixtures.mjs';

test('untouched editors and empty previews leave no drafts', async ({ owner: page }) => {
  for (const type of ['article', 'note', 'review', 'guide', 'gallery', 'check-in']) {
    await newPost(page, type);
    await expect(page.locator('#post-state')).toHaveText('Draft');
    await expect(page.getByRole('tab', { name: 'Details', exact: true })).toBeVisible();
    await expect(page.locator('#editor-details')).toBeHidden();
    await expect(page.locator('#save-status')).toHaveAttribute('data-state', 'empty');
    await page.getByRole('tab', { name: 'Preview', exact: true }).click();
    await expect(page.getByRole('alert')).toContainText('Add content to preview');
    await page.goto('/admin/posts');
    await expect(page.getByText('No posts here yet. Choose a post type to start.')).toBeVisible();
  }
  await newPost(page);
  await page.getByLabel('Body').fill('   ');
  await expect(page.locator('#save-status')).toHaveAttribute('data-state', 'empty');
  await page.goto('/admin/posts');
  await expect(page.getByText('No posts here yet. Choose a post type to start.')).toBeVisible();
  await newPost(page, 'guide');
  await page.getByRole('button', { name: 'Add step', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Step 1 instructions (required to publish)', exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Your guide', exact: true })).toBeVisible();
  await page.goto('/admin/posts');
  await expect(page.getByText('No posts here yet. Choose a post type to start.')).toBeVisible();
});

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
  await page.getByRole('link', { name: 'Public post', exact: true }).click();
  await expect(page).toHaveURL(new RegExp(publicPath + '$'));
  await expect(page.getByText('The preview public story.')).toBeVisible();
  await page.goBack();
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
  await expect(page.getByRole('link', { name: 'Public post', exact: true })).toHaveCount(0);
  await expect(page.locator('#post-state')).toHaveText('Draft');
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

test('Details supports keyboard navigation and saves metadata across editor tabs', async ({ owner: page }, testInfo) => {
  await newPost(page);
  await page.getByLabel('Title', { exact: false }).fill('Editor details');
  await page.getByLabel('Body').fill('A focused writing experience.');
  const write = page.getByRole('tab', { name: 'Write', exact: true });
  const details = page.getByRole('tab', { name: 'Details', exact: true });
  await write.focus();
  await page.keyboard.press('End');
  await expect(details).toBeFocused();
  await expect(details).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('#editor-write')).toBeHidden();
  await expect(page.locator('#editor-details')).toBeVisible();
  await page.getByLabel('Tags (comma separated)').fill('Home, Writing');
  await page.getByLabel('Place name', { exact: true }).fill('Quiet café');
  await page.getByLabel('Latitude', { exact: true }).fill('41.88');
  await page.getByLabel('Longitude', { exact: true }).fill('-87.63');
  await saved(page);
  await page.reload();
  await expect(page.getByLabel('Body')).toHaveValue('A focused writing experience.');
  await details.click();
  expect((await page.getByLabel('Tags (comma separated)').inputValue()).split(', ').sort()).toEqual(['Home', 'Writing']);
  await expect(page.getByLabel('Place name', { exact: true })).toHaveValue('Quiet café');
  await expect(page.getByLabel('Cover image')).toBeVisible();
  await details.focus();
  await page.keyboard.press('ArrowRight');
  await expect(write).toBeFocused();
  await expect(write).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('#post-state')).toHaveText('Draft');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  const widths = await page.locator('#article-editor').evaluate(form => ({
    form: form.getBoundingClientRect().width,
    editor: form.querySelector('[aria-label="Post editor"]').getBoundingClientRect().width
  }));
  expect(widths.editor).toBeCloseTo(widths.form, 0);
  await page.screenshot({ path: testInfo.outputPath('focused-editor.png'), fullPage: true });
  await details.click();
  await page.screenshot({ path: testInfo.outputPath('editor-details.png'), fullPage: true });
  // Native validation must reveal an invalid field even when its tab is hidden.
  await page.getByLabel('Latitude', { exact: true }).fill('91');
  await write.click();
  await page.getByRole('button', { name: 'Save draft', exact: true }).click();
  await expect(details).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByLabel('Latitude', { exact: true })).toBeFocused();
  await page.getByLabel('Latitude', { exact: true }).fill('41.88');
  await page.getByRole('tab', { name: 'Preview', exact: true }).click();
  await expect(page.frameLocator('#draft-preview').getByText('Location:', { exact: false })).toContainText('Quiet café');
  await expect(page.locator('#post-state')).toHaveText('Draft');
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

test('Add Location autosaves coordinates and keeps later location changes private', async ({ owner: page, context }, testInfo) => {
  await context.grantPermissions(['geolocation']);
  await context.setGeolocation({ latitude: 41.88, longitude: -87.63 });
  await newPost(page, 'note');
  await page.getByRole('button', { name: 'Add Location', exact: true }).click();
  await expect(page.locator('#location-status')).toHaveText('Location added.');
  await saved(page);
  await expect(page.getByRole('tab', { name: 'Write', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.reload();
  await page.getByRole('tab', { name: 'Details', exact: true }).click();
  await expect(page.getByLabel('Latitude', { exact: true })).toHaveValue('41.88');
  await expect(page.getByLabel('Longitude', { exact: true })).toHaveValue('-87.63');
  await expect(page.locator('#post-state')).toHaveText('Draft');
  await page.getByRole('tab', { name: 'Write', exact: true }).click();
  await page.getByLabel('Note', { exact: true }).fill('A note with browser coordinates.');
  const publicPath = await publish(page);
  expect(await (await page.request.get(publicPath)).text()).toContain('41.88, -87.63');
  await context.setGeolocation({ latitude: 0, longitude: 0 });
  await page.getByRole('button', { name: 'Add Location', exact: true }).click();
  await expect(page.locator('#location-status')).toHaveText('Location added.');
  await saved(page);
  await page.getByRole('tab', { name: 'Details', exact: true }).click();
  await expect(page.getByLabel('Latitude', { exact: true })).toHaveValue('0');
  await expect(page.getByLabel('Longitude', { exact: true })).toHaveValue('0');
  expect(await (await page.request.get(publicPath)).text()).toContain('41.88, -87.63');
  await page.screenshot({ path: testInfo.outputPath('editor-location.png'), fullPage: true });
});

for (const [code, message] of [[1, 'Location permission was denied.'], [2, 'Your location is unavailable.'], [3, 'Finding your location timed out.'], [0, 'Location is unavailable in this browser.']]) {
  test(`Add Location preserves existing coordinates when geolocation fails (${code})`, async ({ owner: page }) => {
    await page.addInitScript(code => {
      Object.defineProperty(navigator, 'geolocation', {
        value: code ? { getCurrentPosition: (_success, failure) => failure({ code }) } : undefined
      });
    }, code);
    await newPost(page);
    await page.getByRole('tab', { name: 'Details', exact: true }).click();
    await page.getByLabel('Latitude', { exact: true }).fill('10');
    await page.getByLabel('Longitude', { exact: true }).fill('20');
    await saved(page);
    await page.getByRole('tab', { name: 'Write', exact: true }).click();
    const button = page.getByRole('button', { name: 'Add Location', exact: true });
    await button.click();
    await expect(page.locator('#location-status')).toContainText(message);
    await expect(button).toBeEnabled();
    await button.click();
    await expect(page.locator('#location-status')).toContainText(message);
    await page.reload();
    await page.getByRole('tab', { name: 'Details', exact: true }).click();
    await expect(page.getByLabel('Latitude', { exact: true })).toHaveValue('10');
    await expect(page.getByLabel('Longitude', { exact: true })).toHaveValue('20');
  });
}
