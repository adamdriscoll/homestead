import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { test, expect, newPost, publish } from '../support/fixtures.mjs';
import { repoRoot } from '../support/docker.mjs';

const fixture = resolve(repoRoot, 'tests/Homestead.Playwright/fixtures/activity.gpx');
const tile = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64');

async function expectRouteMap(page, javaScriptEnabled, privateMap = false) {
  const activity = page.locator('[data-fitness-activity]');
  const mapTab = activity.getByRole('tab', { name: 'Map', exact: true });
  const speedTab = activity.getByRole('tab', { name: 'Speed', exact: true });
  const heartTab = activity.getByRole('tab', { name: 'Heart rate', exact: true });
  if (javaScriptEnabled) {
    await expect(mapTab).toHaveAttribute('aria-selected', 'true');
    await expect(activity.locator('[data-fitness-panel="speed"]')).toBeHidden();
    await expect(activity.locator('[data-fitness-panel="heart-rate"]')).toBeHidden();
    await speedTab.click();
    await expect(activity.locator('[data-fitness-panel="map"]')).toBeHidden();
    await expect(activity.getByText(/^Speed \((km|mi)\/h\)$/)).toBeVisible();
    await speedTab.press('ArrowRight');
    await expect(heartTab).toBeFocused();
    await expect(heartTab).toHaveAttribute('aria-selected', 'true');
    await expect(activity.getByText('Heart rate (bpm)', { exact: true })).toBeVisible();
    await heartTab.press('Home');
    await expect(mapTab).toBeFocused();
    await mapTab.press('End');
    await expect(heartTab).toBeFocused();
    await heartTab.press('ArrowRight');
    await expect(mapTab).toBeFocused();
  } else {
    await expect(activity.locator('[data-fitness-tabs]')).toBeHidden();
    await expect(activity.getByText(/^Speed \((km|mi)\/h\)$/)).toBeVisible();
    await expect(activity.getByText('Heart rate (bpm)', { exact: true })).toBeVisible();
  }
  for (const panel of await activity.locator('[data-fitness-panel]:not([data-fitness-panel="map"])').all()) {
    if (javaScriptEnabled) await activity.locator(`[data-fitness-tab="${await panel.getAttribute('data-fitness-panel')}"]`).click();
    expect(await panel.locator('figure').evaluate(el => {
      const rect = el.getBoundingClientRect();
      const diagram = el.querySelector('svg').getBoundingClientRect();
      const caption = el.querySelector('figcaption').getBoundingClientRect();
      return diagram.width >= rect.width * 0.9 && caption.bottom <= diagram.top + 1;
    })).toBe(true);
  }
  if (javaScriptEnabled) await mapTab.click();
  if (!javaScriptEnabled) {
    await expect(page.locator('[data-fitness-map]')).toBeHidden();
    await expect(page.getByRole('toolbar', { name: 'Route map controls' })).toBeHidden();
    await expect(page.getByText('Enable JavaScript to view the route map.', { exact: true })).toBeVisible();
  } else {
    if (privateMap) {
      await expect(page.locator('[data-fitness-map]')).toBeHidden();
      await page.getByRole('button', { name: 'Show route map', exact: true }).click();
    } else await page.locator('[data-fitness-route]').scrollIntoViewIfNeeded();
    await expect(page.getByRole('region', { name: 'Activity route map', exact: true })).toBeVisible();
    await expect(page.locator('.fitness-map-line')).toHaveCount(2);
    await expect(page.locator('.leaflet-tile-loaded').first()).toBeVisible();
    await expect(page.getByRole('link', { name: 'OpenStreetMap', exact: true })).toBeVisible();
    await page.locator('.leaflet-marker-icon[title="Start"]').focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('.leaflet-popup-content')).toHaveText('Start');
    const toolbar = page.getByRole('toolbar', { name: 'Route map controls', exact: true });
    const fitRoute = toolbar.getByRole('button', { name: 'Fit route', exact: true });
    await expect(fitRoute).toHaveText('');
    await expect(fitRoute.locator('svg')).toHaveCount(1);
    const toolbarBox = await toolbar.boundingBox();
    const mapBox = await page.locator('[data-fitness-map]').boundingBox();
    expect(toolbarBox.y + toolbarBox.height).toBeLessThanOrEqual(mapBox.y + 1);
    await fitRoute.press('Enter');
    const pathsBeforeZoom = await page.locator('.fitness-map-line').evaluateAll(paths => paths.map(path => path.getAttribute('d')));
    await page.getByRole('button', { name: 'Zoom in', exact: true }).click();
    await expect.poll(() => page.locator('.fitness-map-line').evaluateAll(paths => paths.map(path => path.getAttribute('d')))).not.toEqual(pathsBeforeZoom);
    await fitRoute.press('Enter');
    await expect.poll(() => page.locator('.fitness-map-line').evaluateAll(paths => paths.map(path => path.getAttribute('d')))).toEqual(pathsBeforeZoom);
    await speedTab.click();
    await mapTab.click();
    await expect(page.locator('[data-fitness-map]')).toBeVisible();
    await fitRoute.click();
    await expect.poll(() => page.locator('.fitness-map-line').evaluateAll(paths => paths.map(path => path.getAttribute('d')))).toEqual(pathsBeforeZoom);
  }
  await expect(page.locator('[data-fitness-diagram]')).toHaveCount(0);
  await expect(page.getByText('Route diagram', { exact: true })).toHaveCount(0);
  for (const text of ['Recorded route', 'Separate track segments are not joined.', 'Diagrams are sampled for display.', 'Distance follows recorded GPS points.']) {
    await expect(activity).not.toContainText(text);
  }
}

for (const javaScriptEnabled of [true, false]) {
  test.describe(`Fitness with JavaScript ${javaScriptEnabled ? 'enabled' : 'disabled'}`, () => {
    test.use({ javaScriptEnabled });

    test('imports privately, renders every theme, and replaces/removes tracks through ordinary forms', async ({ owner: page, browser, site }, testInfo) => {
      test.setTimeout(180_000);
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      // Check map surfaces; Settings has a pre-existing MVC validation-summary inline style.
      page.on('console', message => {
        if (!message.location().url.includes('/admin/settings') && message.type() === 'error' && message.text().includes('Content Security Policy')) errors.push(message.text());
      });
      let privateTileRequests = 0;
      await page.route('https://tile.openstreetmap.org/**', route => {
        privateTileRequests++;
        return route.fulfill({ status: 200, contentType: 'image/png', body: tile });
      });
      await newPost(page, 'fitness activity');
      await page.getByLabel('GPX file', { exact: true }).setInputFiles(fixture);
      // Clicking immediately exercises saving dirty fields before an enhanced import.
      await page.getByLabel('Title', { exact: true }).fill('Morning fitness activity');
      await page.getByLabel('Message', { exact: true }).fill('A run along the river.');
      if (!javaScriptEnabled) await page.getByRole('button', { name: 'Save draft', exact: true }).click();
      await page.getByLabel('GPX file', { exact: true }).setInputFiles(fixture);
      await page.getByRole('button', { name: 'Import GPX', exact: true }).click();
      await expect(page.getByRole('link', { name: 'Download original GPX', exact: true })).toBeVisible();
      await expect(page.getByLabel('Title', { exact: true })).toHaveValue('Morning fitness activity');
      await expect(page.getByLabel('Message', { exact: true })).toHaveValue('A run along the river.');
      await expect(page.locator('[data-fitness-diagram]')).toHaveCount(0);
      expect(privateTileRequests).toBe(0);
      await expectRouteMap(page, javaScriptEnabled, true);
      const editorPath = new URL(page.url()).pathname;
      await page.goto(editorPath.replace('/edit', '/preview'));
      await expectRouteMap(page, javaScriptEnabled, true);
      await expect(page.getByText('PRIVATE GPX METADATA')).toHaveCount(0);
      await page.goto(editorPath);
      if (javaScriptEnabled) await page.getByRole('tab', { name: 'Images', exact: true }).click();
      await page.getByLabel('Photos', { exact: false }).setInputFiles(resolve(repoRoot, 'src/Homestead/wwwroot/icon-192.png'));
      await page.getByLabel('Image description (alt text)', { exact: true }).fill('A finish-line photo');
      await page.getByRole('button', { name: 'Upload photo', exact: true }).click();
      await expect(page.locator('#article-images img')).toHaveCount(1);
      await page.getByLabel('Caption', { exact: true }).fill('At the finish');
      const publicPath = await publish(page);
      const visitor = await browser.newContext({ baseURL: site.baseURL, ignoreHTTPSErrors: true, javaScriptEnabled, viewport: page.viewportSize() });
      await visitor.route('https://tile.openstreetmap.org/**', route => route.fulfill({ status: 200, contentType: 'image/png', body: tile }));
      try {
        const publicPage = await visitor.newPage();
        publicPage.on('pageerror', error => errors.push(error.message));
        publicPage.on('console', message => { if (message.type() === 'error' && message.text().includes('Content Security Policy')) errors.push(message.text()); });
        for (const theme of ['porch', 'journal', 'ledger', 'folio', 'townsquare']) {
          await page.goto('/admin/settings');
          await page.locator('#tab-about').click();
          await page.getByLabel('Author display name', { exact: true }).fill('Ada');
          await page.locator('#tab-posts').click();
          await page.getByLabel('Fitness units', { exact: true }).selectOption('Imperial');
          await page.locator('#tab-theme').click();
          await page.getByLabel('Public theme', { exact: true }).selectOption(theme);
          await page.getByRole('button', { name: 'Save settings', exact: true }).click();
          await expect(page.getByRole('status')).toContainText('Settings saved and public site rebuilt.');
          for (const colorScheme of ['light', 'dark']) {
            await publicPage.emulateMedia({ colorScheme });
            await page.emulateMedia({ colorScheme });
            for (const path of ['/', '/fitness/', publicPath]) {
              await publicPage.goto(path);
              await expect(publicPage.locator('html')).toHaveAttribute('data-public-theme', theme);
              await expect(publicPage.locator('[data-fitness-summary]')).toContainText('Average heart rate');
              await expect(publicPage.locator('[data-fitness-summary]')).toContainText('mi/h');
              await expect(publicPage.locator('[data-fitness-summary]')).toContainText('/mi');
              await expect(publicPage.locator('[data-fitness-summary]')).not.toContainText('km/h');
              expect(await publicPage.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
            }
            await expectRouteMap(publicPage, javaScriptEnabled);
            await expect(publicPage.getByRole('img', { name: 'A finish-line photo', exact: true })).toBeVisible();
            await expect(publicPage.getByText('At the finish', { exact: true })).toBeVisible();
            await publicPage.screenshot({ path: testInfo.outputPath(`${theme}-${colorScheme}-fitness.png`), fullPage: true });
            await page.goto(editorPath.replace('/edit', '/preview'));
            await expectRouteMap(page, javaScriptEnabled, true);
            await expect(page.getByText('PRIVATE GPX METADATA')).toHaveCount(0);
            expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
          }
        }
        await page.goto(editorPath);
        if (javaScriptEnabled) {
          await visitor.route('https://tile.openstreetmap.org/**', route => route.abort());
          await publicPage.goto(publicPath);
          await publicPage.locator('[data-fitness-route]').scrollIntoViewIfNeeded();
          await expect(publicPage.locator('[data-fitness-map-status]')).toBeVisible();
          await expect(publicPage.locator('[data-fitness-diagram]')).toHaveCount(0);
          await expect(publicPage.locator('.fitness-map-line')).toHaveCount(2);
          await expect(publicPage.getByRole('toolbar', { name: 'Route map controls' })).toBeVisible();
        }
        const replacement = readFileSync(fixture, 'utf8').replace('lat="41.883"', 'lat="41.885"').replace(/<gpxtpx:hr>.*?<\/gpxtpx:hr>/g, '');
        const before = await page.locator('[data-fitness-summary]').textContent();
        await page.getByLabel('GPX file', { exact: true }).setInputFiles({ name: 'replacement.gpx', mimeType: 'application/gpx+xml', buffer: Buffer.from(replacement) });
        await page.getByRole('button', { name: 'Import GPX', exact: true }).click();
        await expect(page.locator('[data-fitness-summary]')).not.toHaveText(before);
        await publicPage.goto(publicPath);
        await expect(publicPage.locator('[data-fitness-summary]')).toHaveText(before);
        await publish(page, 'Update published post');
        await publicPage.reload();
        await expect(publicPage.locator('[data-fitness-summary]')).not.toHaveText(before);
        await expect(publicPage.locator('[data-fitness-tab="heart-rate"]')).toHaveCount(0);
        await expect(publicPage.locator('[data-fitness-panel="heart-rate"]')).toHaveCount(0);
        await page.getByRole('button', { name: 'Remove GPX', exact: true }).click();
        await expect(page.locator('[data-fitness-route]')).toHaveCount(0);
        await publicPage.reload();
        await expect(publicPage.locator('[data-fitness-route]')).toBeVisible();
        await publish(page, 'Update published post');
        await publicPage.reload();
        await expect(publicPage.locator('[data-fitness-route]')).toHaveCount(0);
        await expect(publicPage.getByText('A run along the river.', { exact: true })).toBeVisible();
        expect(errors).toEqual([]);
      } finally { await visitor.close(); }
    });
  });
}
