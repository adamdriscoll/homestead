import { test, expect } from '../support/fixtures.mjs';

for (const javaScriptEnabled of [true, false]) {
  test.describe(`newsletter with JavaScript ${javaScriptEnabled ? 'enabled' : 'disabled'}`, () => {
    test.use({ javaScriptEnabled });

    test('configures private Brevo settings and renders responsive subscription forms in every theme', async ({ owner: page, site, browser }, testInfo) => {
      await page.goto('/admin/settings');
      await page.locator('#tab-about').click();
      await page.getByLabel('Author display name').fill('Owner');
      await page.locator('#tab-general').click();
      await page.getByLabel('Canonical URL').fill(site.baseURL);
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      await page.locator('#tab-newsletter').click();
      const newsletter = page.locator('#newsletter');
      await newsletter.getByLabel('Enable newsletter subscriptions and delivery').check();
      await newsletter.getByLabel('Sender email').fill('owner@example.test');
      await newsletter.getByLabel('Sender name').fill('Owner');
      await newsletter.getByRole('button', { name: 'Save newsletter settings' }).click();
      await expect(newsletter.getByText('Enter a Brevo API key', { exact: false })).toBeVisible();
      await newsletter.getByLabel('Brevo API key').fill('browser-private-brevo-key');
      await newsletter.getByRole('button', { name: 'Save newsletter settings' }).click();
      await expect(page.getByRole('status').first()).toContainText('Newsletter settings saved.');
      await expect(newsletter.getByLabel('Brevo API key')).toHaveValue('');
      if (javaScriptEnabled) await expect(page.locator('#settings-form')).toBeHidden();
      else await expect(page.locator('#settings-form')).toBeVisible();
      await newsletter.getByRole('button', { name: 'Save newsletter settings' }).click();
      expect(await page.content()).not.toContain('browser-private-brevo-key');

      const reader = await browser.newContext({ baseURL: site.baseURL, ignoreHTTPSErrors: true, javaScriptEnabled, viewport: page.viewportSize() });
      const subscription = await reader.newPage();
      try {
        for (const theme of ['porch', 'journal', 'ledger', 'folio', 'townsquare']) {
          await page.goto('/admin/settings#theme');
          await page.locator('#tab-theme').click();
          await page.getByLabel('Public theme', { exact: true }).selectOption(theme);
          await page.getByRole('button', { name: 'Save settings', exact: true }).click();
          await expect(page.getByRole('status').first()).toContainText('Settings saved');
          await subscription.goto('/newsletter/');
          await expect(subscription.locator('html')).toHaveAttribute('data-public-theme', theme);
          await expect(subscription.getByRole('heading', { name: 'Newsletter', exact: true })).toBeVisible();
          await expect(subscription.getByLabel('Email address')).toBeVisible();
          await subscription.getByLabel('Delivery', { exact: true }).selectOption('Weekly');
          await subscription.getByLabel('Notes', { exact: true }).uncheck();
          await expect(subscription.getByLabel('Notes', { exact: true })).not.toBeChecked();
          await expect(subscription.getByLabel('Check-ins', { exact: true })).toBeChecked();
          for (const colorScheme of ['light', 'dark']) {
            await subscription.emulateMedia({ colorScheme });
            await expect(subscription.getByRole('button', { name: 'Subscribe', exact: true })).toBeVisible();
            expect(await subscription.locator('body').evaluate(element => element.scrollWidth <= window.innerWidth)).toBe(true);
            await subscription.screenshot({ path: testInfo.outputPath(`${theme}-${colorScheme}.png`), fullPage: true });
          }
        }
        // Validate ordinary server forms without sending any emails to Brevo.
        const missingCsrf = await subscription.request.post('/newsletter/', { form: { 'Input.Email': 'invalid' } });
        expect(missingCsrf.status()).toBe(400);
        const csrf = await subscription.locator('input[name="__RequestVerificationToken"]').inputValue();
        const noTypes = await subscription.request.post('/newsletter/', { form: {
          __RequestVerificationToken: csrf, 'Input.Email': 'reader@example.test', 'Input.Frequency': 'Weekly'
        } });
        expect(noTypes.status()).toBe(200);
        expect(await noTypes.text()).toContain('Choose at least one post type.');
      } finally { await reader.close(); }
      await page.goto('/admin/settings#newsletter');
      await page.locator('#tab-newsletter').click();
      await newsletter.getByLabel('Enable newsletter subscriptions and delivery').uncheck();
      await newsletter.getByLabel('Clear saved API key').check();
      await newsletter.getByRole('button', { name: 'Save newsletter settings' }).click();
      await expect(newsletter.getByText('API key saved: No.', { exact: false })).toBeVisible();
      expect((await page.request.get('/newsletter/')).status()).toBe(404);
    });
  });
}
