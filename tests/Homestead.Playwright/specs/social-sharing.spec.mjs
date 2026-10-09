import { test, expect, newPost, publish } from '../support/fixtures.mjs';

const services = ['Bluesky', 'Facebook', 'X', 'LinkedIn', 'Reddit', 'Email'];
const themes = ['porch', 'journal', 'ledger', 'folio', 'townsquare'];

for (const javaScriptEnabled of [true, false]) {
  test.describe(`social sharing with JavaScript ${javaScriptEnabled ? 'enabled' : 'disabled'}`, () => {
    test.use({ javaScriptEnabled });

    test('selects sharing services and renders accessible links across all themes', async ({ owner: page }, testInfo) => {
      test.setTimeout(180_000);
      await newPost(page);
      await page.getByLabel('Title', { exact: false }).fill('Café & a quiet day');
      await page.getByLabel('Body').fill('Published article words.');
      const articlePath = await publish(page);
      const editorPath = new URL(page.url()).pathname;
      await page.getByLabel('Title', { exact: false }).fill('PRIVATE sharing title');
      await page.getByRole('button', { name: 'Save draft', exact: true }).click();
      await expect(page.getByRole('status').filter({ hasText: 'Draft saved.' })).toBeVisible();
      await newPost(page, 'note');
      await page.getByLabel('Note', { exact: true }).fill('A public note.');
      const notePath = await publish(page);
      await page.goto('/');
      await expect(page.locator('[data-social-sharing]')).toHaveCount(0);

      await page.goto('/admin/settings');
      await page.locator('#tab-about').click();
      await page.getByLabel('Author display name', { exact: true }).fill('Ada');
      await page.locator('#tab-social').click();
      if (javaScriptEnabled) {
        await expect(page.locator('#tab-social')).toHaveAttribute('aria-selected', 'true');
        await page.locator('#tab-social').focus();
        await page.keyboard.press(testInfo.project.name === 'chromium' ? 'ArrowDown' : 'ArrowRight');
        await expect(page.locator('#tab-theme')).toBeFocused();
        await page.locator('#tab-social').click();
      }
      const choices = page.getByRole('group', { name: 'Social sharing', exact: true });
      for (const service of services) {
        const checkbox = choices.getByRole('checkbox', { name: service, exact: true });
        await expect(checkbox).not.toBeChecked();
        await checkbox.check();
      }
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('Settings saved and public site rebuilt.');
      if (javaScriptEnabled) await expect(page).toHaveURL(/#social$/);
      await page.reload();
      await page.locator('#tab-social').click();
      for (const service of services) await expect(choices.getByRole('checkbox', { name: service, exact: true })).toBeChecked();

      for (const theme of themes) {
        await page.goto('/admin/settings#theme');
        await page.getByLabel('Public theme', { exact: true }).selectOption(theme);
        await page.getByRole('button', { name: 'Save settings', exact: true }).click();
        await expect(page.getByRole('status')).toContainText('Settings saved and public site rebuilt.');
        for (const colorScheme of ['light', 'dark']) {
          await page.emulateMedia({ colorScheme });
          for (const path of ['/', '/articles/', articlePath, notePath]) {
            await page.goto(path);
            const sharing = page.locator('[data-social-sharing]').first();
            await expect(sharing).toBeVisible();
            await expect(sharing.getByRole('link')).toHaveCount(6);
            for (const service of services) {
              const link = sharing.getByRole('link', { name: `Share via ${service}`, exact: true });
              await expect(link).toBeVisible();
              expect(await link.getAttribute('href')).not.toContain('PRIVATE');
              expect(await link.evaluate(el => el.getBoundingClientRect().height)).toBeGreaterThanOrEqual(44);
            }
            const email = sharing.getByRole('link', { name: 'Share via Email', exact: true });
            const query = new URLSearchParams((await email.getAttribute('href')).split('?')[1]);
            expect(query.get('body')).toContain('/posts/');
            if (path === articlePath) {
              expect(query.get('subject')).toBe('Café & a quiet day');
              expect(query.get('body')).toBe(await page.locator('link[rel="canonical"]').getAttribute('href'));
              await email.focus();
              await expect(email).toBeFocused();
              await page.screenshot({ path: testInfo.outputPath(`${theme}-${colorScheme}-sharing.png`), fullPage: true });
            }
            expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
            await expect(page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Home', exact: true })).toBeVisible();
          }
        }
        await page.goto(editorPath.replace(/\/edit$/, '/preview'));
        await expect(page.getByRole('heading', { name: 'PRIVATE sharing title', exact: true })).toBeVisible();
        await expect(page.locator('[data-social-sharing]')).toHaveCount(0);
      }

      await page.goto('/admin/settings#social');
      for (const service of services) {
        if (service !== 'Email') await choices.getByRole('checkbox', { name: service, exact: true }).uncheck();
      }
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      await page.goto(articlePath);
      await expect(page.locator('[data-social-sharing] a')).toHaveCount(1);
      await expect(page.getByRole('link', { name: 'Share via Email', exact: true })).toBeVisible();
      await page.goto('/admin/settings#social');
      await choices.getByRole('checkbox', { name: 'Email', exact: true }).uncheck();
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      await page.goto(articlePath);
      await expect(page.locator('[data-social-sharing]')).toHaveCount(0);
    });
  });
}
