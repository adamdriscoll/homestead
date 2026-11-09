import { test, expect } from '../support/fixtures.mjs';

const browserAgent = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36';

for (const javaScriptEnabled of [true, false]) {
  test.describe(`analytics with JavaScript ${javaScriptEnabled ? 'enabled' : 'disabled'}`, () => {
    test.use({ javaScriptEnabled });

    test('opts in, counts anonymous pages, excludes authors and bots, and retains disabled counts', async ({ owner: page, browser, baseURL }, testInfo) => {
      await expect(page.getByRole('region', { name: 'Analytics', exact: true })).toContainText('Analytics is off.');
      await page.getByRole('link', { name: 'Configure analytics', exact: true }).click();
      await expect(page.getByLabel('Count public page views')).not.toBeChecked();
      await page.getByLabel('Count public page views').check();
      await page.getByRole('button', { name: 'Save analytics settings', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('Analytics enabled.');
      const visitor = await browser.newContext({ baseURL, ignoreHTTPSErrors: true, javaScriptEnabled, userAgent: browserAgent });
      try {
        const publicPage = await visitor.newPage();
        await publicPage.goto('/?discard=this-query');
        await publicPage.goto('/index.html');
        await publicPage.goto('/about/');
        await visitor.request.get('/', { headers: { 'User-Agent': 'GPTBot/1.2' } });
        await visitor.request.get('/', { headers: { 'User-Agent': 'ClaudeBot/1.0' } });
        await visitor.request.head('/');
        await page.goto('/'); // An authenticated author visit must not count.
        await page.goto('/admin/analytics');
        await expect(page.locator('.stat-value')).toHaveText('3');
        await expect(page.getByRole('row', { name: '/ 2', exact: true })).toBeVisible();
        await expect(page.getByRole('row', { name: '/about/ 1', exact: true })).toBeVisible();
        await expect(page.getByRole('table')).not.toContainText('discard');
        await page.goto('/admin');
        await expect(page.getByRole('region', { name: 'Analytics', exact: true })).toContainText('3');
        await page.getByRole('link', { name: 'View analytics by page', exact: true }).click();
        for (const appearance of ['light', 'dark']) {
          await page.emulateMedia({ colorScheme: appearance });
          await expect(page.getByRole('table')).toBeVisible();
          expect(await page.locator('body').evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
          await page.screenshot({ path: testInfo.outputPath(`analytics-${appearance}.png`), fullPage: true });
        }
        await page.getByLabel('Count public page views').uncheck();
        await page.getByRole('button', { name: 'Save analytics settings', exact: true }).click();
        await expect(page.getByRole('status')).toContainText('Analytics disabled.');
        await publicPage.goto('/');
        await page.reload();
        await expect(page.locator('.stat-value')).toHaveText('3');
        await expect(page.getByLabel('Count public page views')).not.toBeChecked();
      } finally { await visitor.close(); }
    });
  });
}
