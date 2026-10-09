import { readFileSync, readdirSync } from 'node:fs';
import { zipSync } from 'fflate';
import { test, expect, newPost, publish } from '../support/fixtures.mjs';

const exampleRoot = new URL('../../../examples/themes/razor-garden/', import.meta.url);
const files = Object.fromEntries(readdirSync(exampleRoot).map(name => [name, readFileSync(new URL(name, exampleRoot))]));
const exampleZip = Buffer.from(zipSync(files));

for (const javaScriptEnabled of [true, false]) {
  test.describe(`Razor theme with JavaScript ${javaScriptEnabled ? 'enabled' : 'disabled'}`, () => {
    test.use({ javaScriptEnabled });

    test('selects built-in Town Square and reads its social feed and posts', async ({ owner: page }, testInfo) => {
      await newPost(page, 'article');
      await page.getByLabel('Title', { exact: false }).fill('Small rituals, lasting stories');
      await page.getByLabel('Body').fill('## A little room to think\n\nThe best stories start with noticing the everyday.');
      const articlePath = await publish(page);
      await newPost(page, 'note');
      await page.getByLabel('Note', { exact: true }).fill('A small observation from the garden today.');
      const notePath = await publish(page);
      const editorPath = new URL(page.url()).pathname;
      await page.getByLabel('Note', { exact: true }).fill('PRIVATE TOWN SQUARE DRAFT');
      await page.getByRole('button', { name: 'Save draft', exact: true }).click();
      await expect(page.getByRole('status').filter({ hasText: 'Draft saved.' })).toBeVisible();

      await page.goto('/admin/settings');
      await page.locator('#tab-general').click();
      await page.getByLabel('Site title', { exact: true }).fill('Everyday Ada');
      await page.getByLabel('Description', { exact: true }).fill('Little moments, good reads, and places worth remembering.');
      await page.locator('#tab-about').click();
      await page.getByLabel('Author display name', { exact: true }).fill('Ada');
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      await page.locator('#tab-theme').click();
      await page.getByLabel('Accent preset', { exact: true }).selectOption('Porch');
      await page.getByLabel('Public theme', { exact: true }).selectOption('townsquare');
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('Settings saved and public site rebuilt.');

      await page.goto('/');
      await expect(page.locator('html')).toHaveAttribute('data-public-theme', 'townsquare');
      await expect(page.locator('[data-town-header]').getByRole('link', { name: 'Everyday Ada' })).toBeVisible();
      await expect(page.getByRole('heading', { level: 1, name: 'Ada', exact: true })).toBeVisible();
      await expect(page.locator('[data-town-post]')).toHaveCount(2);
      await expect(page.getByText('A small observation from the garden today.', { exact: true })).toBeVisible();
      await expect(page.getByText('PRIVATE TOWN SQUARE DRAFT', { exact: true })).toHaveCount(0);
      await page.keyboard.press('Tab');
      await expect(page.getByRole('link', { name: 'Skip to content', exact: true })).toBeFocused();
      await page.keyboard.press('Enter');
      await expect(page).toHaveURL(/\/#main$/);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      await page.screenshot({ path: testInfo.outputPath('townsquare-home.png'), fullPage: true });
      await page.getByRole('heading', { name: 'Small rituals, lasting stories', exact: true }).getByRole('link').click();
      await expect(page).toHaveURL(new RegExp(articlePath + '$'));
      await expect(page.getByRole('heading', { name: 'A little room to think', exact: true })).toBeVisible();
      await expect(page.locator('[data-town-author]')).toHaveText('Ada');
      await page.screenshot({ path: testInfo.outputPath('townsquare-article.png'), fullPage: true });
      await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Notes', exact: true }).click();
      await page.getByRole('link', { name: 'Open post', exact: false }).click();
      await expect(page).toHaveURL(new RegExp(notePath + '$'));
      await expect(page.getByText('A small observation from the garden today.', { exact: true })).toBeVisible();
      await page.emulateMedia({ colorScheme: 'dark' });
      await page.goto('/');
      expect(await page.locator('[data-town-post]').first().evaluate(el => getComputedStyle(el).backgroundColor)).toBe('rgb(36, 37, 38)');
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      await page.screenshot({ path: testInfo.outputPath('townsquare-dark.png'), fullPage: true });
      expect((await page.request.get('/assets/theme/Main.razor')).status()).toBe(404);
      await page.goto('/admin/settings#theme');
      await page.getByLabel('Primary color', { exact: true }).fill('#245734');
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      await page.goto('/');
      expect(await page.locator('[data-town-wordmark]').evaluate(el => getComputedStyle(el).color)).toBe('rgb(36, 87, 52)');
      await page.goto(editorPath.replace(/\/edit$/, '/preview'));
      await expect(page.getByText('Private draft preview', { exact: false })).toBeVisible();
      await expect(page.getByText('PRIVATE TOWN SQUARE DRAFT', { exact: true })).toBeVisible();
      expect(await page.locator('script').count()).toBe(0);
    });

    test('selects built-in Folio and reads its cover, feed and posts', async ({ owner: page }, testInfo) => {
      await newPost(page, 'article');
      await page.getByLabel('Title', { exact: false }).fill('Small rituals, lasting stories');
      await page.getByLabel('Body').fill('## A little room to think\n\nThe best stories start with noticing the everyday.\n\nA quiet morning, a warm cup, and a page waiting for words.');
      const articlePath = await publish(page);
      await newPost(page, 'note');
      await page.getByLabel('Note', { exact: true }).fill('A small observation from the garden today.');
      const notePath = await publish(page);
      const editorPath = new URL(page.url()).pathname;
      await page.getByLabel('Note', { exact: true }).fill('PRIVATE FOLIO DRAFT');
      await page.getByRole('button', { name: 'Save draft', exact: true }).click();
      await expect(page.getByRole('status').filter({ hasText: 'Draft saved.' })).toBeVisible();

      await page.goto('/admin/settings');
      await page.locator('#tab-general').click();
      await page.getByLabel('Site title', { exact: true }).fill('Fieldnotes');
      await page.getByLabel('Description', { exact: true }).fill('Stories from a slower life. Notes, discoveries, and things worth keeping.');
      await page.locator('#tab-about').click();
      await page.getByLabel('Author display name', { exact: true }).fill('Ada');
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      await page.locator('#tab-theme').click();
      await page.getByLabel('Accent preset', { exact: true }).selectOption('Porch');
      await page.getByLabel('Public theme', { exact: true }).selectOption('folio');
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('Settings saved and public site rebuilt.');

      await page.goto('/');
      await expect(page.locator('html')).toHaveAttribute('data-public-theme', 'folio');
      await expect(page.locator('[data-folio-header]').getByRole('link', { name: /^Fieldnotes/ })).toBeVisible();
      expect(await page.locator('[data-folio-header] [data-folio-wordmark]').evaluate(el => getComputedStyle(el).color)).toBe('rgb(255, 255, 255)');
      await expect(page.getByRole('heading', { level: 1, name: 'Fieldnotes', exact: true })).toBeVisible();
      await expect(page.getByText('A small observation from the garden today.', { exact: true })).toBeVisible();
      await expect(page.getByText('PRIVATE FOLIO DRAFT', { exact: true })).toHaveCount(0);
      await page.keyboard.press('Tab');
      await expect(page.getByRole('link', { name: 'Skip to content', exact: true })).toBeFocused();
      await page.getByRole('link', { name: 'Explore the journal', exact: false }).click();
      await expect(page).toHaveURL(/\/#main$/);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      await page.screenshot({ path: testInfo.outputPath('folio-home.png'), fullPage: true });
      await page.getByRole('heading', { name: 'Small rituals, lasting stories', exact: true }).getByRole('link').click();
      await expect(page).toHaveURL(new RegExp(articlePath + '$'));
      await expect(page.getByRole('heading', { name: 'A little room to think', exact: true })).toBeVisible();
      await expect(page.getByText('By Ada', { exact: true })).toBeVisible();
      await page.screenshot({ path: testInfo.outputPath('folio-article.png'), fullPage: true });
      await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Notes', exact: true }).click();
      await page.getByRole('heading', { name: /^Note ·/ }).getByRole('link').click();
      await expect(page).toHaveURL(new RegExp(notePath + '$'));
      await expect(page.getByText('A small observation from the garden today.', { exact: true })).toBeVisible();
      await page.emulateMedia({ colorScheme: 'dark' });
      await page.goto('/');
      expect(await page.evaluate(() => getComputedStyle(document.body).backgroundColor)).toBe('rgb(28, 29, 32)');
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      await page.screenshot({ path: testInfo.outputPath('folio-dark.png'), fullPage: true });
      expect((await page.request.get('/assets/theme/Main.razor')).status()).toBe(404);
      await page.goto('/admin/settings#theme');
      await page.getByLabel('Primary color', { exact: true }).fill('#245734');
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      await page.goto('/');
      expect(await page.locator('[data-folio-cover]').evaluate(el => getComputedStyle(el).backgroundColor)).toBe('rgb(36, 87, 52)');
      await page.goto(editorPath.replace(/\/edit$/, '/preview'));
      await expect(page.getByText('Private draft preview', { exact: false })).toBeVisible();
      await expect(page.getByText('PRIVATE FOLIO DRAFT', { exact: true })).toBeVisible();
    });

    test('installs the example in the runtime image and renders notes and navigation', async ({ owner: page }, testInfo) => {
      await newPost(page, 'note');
      await page.getByLabel('Note', { exact: true }).fill('Published garden words.');
      const publicPath = await publish(page);
      const editorPath = new URL(page.url()).pathname;
      await page.getByLabel('Note', { exact: true }).fill('Private garden draft.');
      await page.getByRole('button', { name: 'Save draft', exact: true }).click();
      await expect(page.getByRole('status').filter({ hasText: 'Draft saved.' })).toBeVisible();

      await page.goto('/admin/settings');
      await page.locator('#tab-about').click();
      await page.getByLabel('Author display name', { exact: true }).fill('Ada');
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      await page.locator('#tab-theme').click();
      await page.getByLabel('Theme ZIP', { exact: true }).setInputFiles({ name: 'razor-garden.zip', mimeType: 'application/zip', buffer: exampleZip });
      await page.getByRole('button', { name: 'Install theme', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('installed. Select');
      const select = page.getByLabel('Public theme', { exact: true });
      const id = await select.locator('option').filter({ hasText: 'Razor Garden' }).getAttribute('value');
      await select.selectOption(id);
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('Settings saved and public site rebuilt.');

      await page.goto('/');
      await expect(page.locator('html')).toHaveAttribute('data-public-theme', id);
      await expect(page.getByText('From the garden', { exact: false })).toBeVisible();
      await expect(page.getByText('Published garden words.', { exact: true })).toBeVisible();
      await expect(page.getByText('Private garden draft.', { exact: true })).toHaveCount(0);
      await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Notes', exact: true }).click();
      await page.getByRole('heading', { name: /^Note ·/ }).getByRole('link').click();
      await expect(page).toHaveURL(new RegExp(publicPath + '$'));
      await expect(page.getByRole('heading', { level: 1, name: /^Note ·/ })).toBeVisible();
      await expect(page.getByText('Published garden words.', { exact: true })).toBeVisible();
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      expect((await page.request.get('/assets/theme/Note.razor')).status()).toBe(404);
      await page.screenshot({ path: testInfo.outputPath('razor-theme.png'), fullPage: true });

      await page.goto(editorPath.replace(/\/edit$/, '/preview'));
      await expect(page.getByText('Private draft preview', { exact: false })).toBeVisible();
      await expect(page.getByText('Private garden draft.', { exact: true })).toBeVisible();
      expect(await page.locator('script').count()).toBe(0);
    });
  });
}
