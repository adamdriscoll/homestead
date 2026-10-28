import { test, expect, newPost, publish } from '../support/fixtures.mjs';
import { createServer } from 'node:http';
import { unzipSync } from 'fflate';

const body = '```mermaid\nflowchart LR\naccTitle: Publishing flow\naccDescr: Draft goes to preview then publish.\nA[Draft] --> B[Preview] --> C[Publish]\n```\n\n' +
  '```mermaid\nsequenceDiagram\nAlice->>Bob: Hello\nBob-->>Alice: Welcome\n```\n\n' +
  '```mermaid\ninvalid diagram source\n```\n\n```js\nconst ordinary = true;\n```';

for (const theme of ['porch', 'journal', 'ledger', 'folio', 'townsquare']) {
  test(`${theme} Mermaid renders public posts and private previews in both appearances`, async ({ owner: page, browser, site }, testInfo) => {
    test.setTimeout(120_000);
    await newPost(page, 'note');
    await page.getByLabel('Note', { exact: true }).fill(body);
    const publicPath = await publish(page);
    const previewPath = new URL(page.url()).pathname.replace(/\/edit$/, '/preview');
    await page.goto('/admin/settings#theme');
    await page.locator('#tab-about').click();
    await page.getByLabel('Author display name', { exact: true }).fill('Ada');
    await page.locator('#tab-theme').click();
    await page.getByLabel('Public theme', { exact: true }).selectOption(theme);
    await page.getByRole('button', { name: 'Save settings', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('Settings saved and public site rebuilt.');
    for (const colorScheme of ['light', 'dark']) {
      await page.emulateMedia({ colorScheme });
      for (const path of ['/', '/notes/', publicPath, previewPath]) {
        await page.goto(path);
        const diagrams = page.locator('[data-mermaid]');
        await expect(diagrams).toHaveCount(3);
        await expect(diagrams.nth(0).getByRole('img')).toBeVisible();
        await expect(diagrams.nth(1).getByRole('img')).toBeVisible();
        await expect(diagrams.nth(2).getByRole('status')).toContainText('could not be rendered');
        await expect(diagrams.nth(2).locator('code')).toBeVisible();
        await expect(page.locator('code.language-js')).toContainText('const ordinary');
        expect(await diagrams.nth(0).getByRole('img').evaluate(image => image.complete && image.naturalWidth > 0)).toBe(true);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        await diagrams.nth(0).locator('summary').focus();
        await page.keyboard.press('Enter');
        await expect(diagrams.nth(0).locator('code')).toBeVisible();
      }
      await page.screenshot({ path: testInfo.outputPath(`${theme}-${colorScheme}-preview.png`), fullPage: true });
    }
    const context = await browser.newContext({ baseURL: site.baseURL, javaScriptEnabled: false, ignoreHTTPSErrors: true });
    const plain = await context.newPage();
    await plain.goto(publicPath);
    await expect(plain.locator('[data-mermaid] code').first()).toBeVisible();
    await plain.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Notes', exact: true }).click();
    await expect(plain).toHaveURL(/\/notes\/$/);
    await context.close();
    if (theme === 'porch') {
      await page.goto('/admin/maintenance');
      const token = await page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
      const response = await page.request.post('/admin/maintenance?handler=Static', { form: { __RequestVerificationToken: token } });
      expect(response.ok()).toBe(true);
      const files = unzipSync(await response.body());
      const server = createServer((request, result) => {
        const path = new URL(request.url, 'http://localhost').pathname;
        const file = files[path.slice(1) + (path.endsWith('/') ? 'index.html' : '')];
        result.writeHead(file ? 200 : 404, { 'Content-Type': path.endsWith('.mjs') ? 'text/javascript' : path.endsWith('.css') ? 'text/css' : 'text/html' });
        result.end(file);
      });
      await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
      try {
        await page.goto(`http://127.0.0.1:${server.address().port}${publicPath}`);
        await expect(page.locator('[data-mermaid] img')).toHaveCount(2);
        await expect(page.locator('[data-mermaid] img').first()).toBeVisible();
      } finally { await new Promise(resolve => server.close(resolve)); }
    }
  });
}

test('diagram directives cannot enable HTML, click actions, or image requests and oversized sources stay readable', async ({ owner: page }) => {
  await newPost(page, 'note');
  await page.getByLabel('Note', { exact: true }).fill(
    '```mermaid\n%%{init: {"securityLevel":"loose","htmlLabels":true,"flowchart":{"htmlLabels":true},"themeCSS":"@import url(https://diagram.invalid/style.css);"}}%%\n' +
    'flowchart LR\nA["<img src=https://diagram.invalid/pixel.png onerror=alert(1)>"] --> B[Safe]\nclick B "javascript:alert(1)"\n```\n\n' +
    '```mermaid\nflowchart LR\nA@{ img: "https://diagram.invalid/shape.png" }\n```\n\n' +
    '```mermaid\nflowchart LR\n%%' + 'x'.repeat(50000) + '\nA --> B\n```');
  const publicPath = await publish(page);
  const requests = [];
  await page.route('https://diagram.invalid/**', route => { requests.push(route.request().url()); return route.abort(); });
  await page.goto(publicPath);
  const figures = page.locator('[data-mermaid]');
  await expect(figures.nth(0).getByRole('img')).toBeVisible();
  await expect(figures.nth(1).getByRole('status')).toContainText('could not be rendered');
  await expect(figures.nth(2).getByRole('status')).toContainText('could not be rendered');
  const unsafeNodes = await figures.nth(0).getByRole('img').evaluate(image => {
    const svg = decodeURIComponent(image.src.slice(image.src.indexOf(',') + 1));
    return new DOMParser().parseFromString(svg, 'image/svg+xml')
      .querySelectorAll('foreignObject, image, a, script, [onerror], [onclick]').length;
  });
  expect(unsafeNodes).toBe(0);
  expect(requests).toEqual([]);
});
