import { test, expect, newPost, publish } from '../support/fixtures.mjs';

async function expectPopupInsideMap(page) {
  const popup = page.locator('.leaflet-popup');
  await expect(popup).toHaveCSS('opacity', '1');
  await expect.poll(() => popup.evaluate(element => {
    const bounds = element.getBoundingClientRect();
    const map = element.closest('.leaflet-container').getBoundingClientRect();
    const controls = element.closest('.leaflet-container').querySelector('.leaflet-control-zoom').getBoundingClientRect();
    return bounds.left >= map.left && bounds.right <= map.right && bounds.top >= map.top && bounds.bottom <= map.bottom &&
      (bounds.left >= controls.right || bounds.top >= controls.bottom);
  })).toBe(true);
}

for (const javaScriptEnabled of [true, false]) {
  test.describe(`location map with JavaScript ${javaScriptEnabled ? 'enabled' : 'disabled'}`, () => {
    test.use({ javaScriptEnabled });

    test('markers and post links use published locations', async ({ owner: page }, testInfo) => {
      // Automated tests use a local image, never request production OSM tiles.
      await page.route('https://tile.openstreetmap.org/**', route => route.fulfill({
        status: 200, contentType: 'image/png',
        body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64')
      }));
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      page.on('console', message => { if (message.type() === 'error' && message.text().includes('Content Security Policy')) errors.push(message.text()); });
      expect((await page.request.get('/map/')).status()).toBe(404);
      await page.goto('/admin/settings');
      await page.locator('#tab-about').click();
      await page.getByLabel('Author display name', { exact: true }).fill('Ada');
      await page.locator('#tab-posts').click();
      await page.getByRole('checkbox', { name: 'Enable location map', exact: true }).check();
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('Settings saved and public site rebuilt.');
      await page.goto('/map/');
      await expect(page.getByText('No published posts with coordinates yet', { exact: false })).toBeVisible();

      await newPost(page, 'check-in');
      if (javaScriptEnabled) await page.getByRole('tab', { name: 'Details', exact: true }).click();
      await page.getByLabel('Place name (required to publish)', { exact: true }).fill('Equator café');
      await page.getByLabel('Latitude (optional)', { exact: true }).fill('0');
      await page.getByLabel('Longitude (optional)', { exact: true }).fill('0');
      const publicPath = await publish(page);
      const editorPath = new URL(page.url()).pathname;
      if (javaScriptEnabled) await page.getByRole('tab', { name: 'Details', exact: true }).click();
      await page.getByLabel('Place name (required to publish)', { exact: true }).fill('Private draft place');
      await page.getByLabel('Latitude (optional)', { exact: true }).fill('42');
      await page.getByRole('button', { name: 'Save draft', exact: true }).click();
      await expect(page.getByRole('status').filter({ hasText: 'Draft saved.' })).toBeVisible();
      await page.goto('/');
      await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Map', exact: true }).click();
      await expect(page).toHaveURL(/\/map\/$/);
      await expect(page.getByRole('link', { name: 'Check-in at Equator café', exact: true })).toBeVisible();
      await expect(page.getByText('Private draft place', { exact: false })).toHaveCount(0);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      if (javaScriptEnabled) {
        await expect(page.locator('.leaflet-marker-icon')).toHaveCount(1);
        await expect(page.getByRole('link', { name: 'OpenStreetMap', exact: true })).toBeVisible();
        await page.locator('.leaflet-marker-icon').focus();
        await page.keyboard.press('Enter');
        await expect(page.locator('.leaflet-popup-content').getByRole('link', { name: 'Check-in at Equator café', exact: true })).toBeVisible();
        await expectPopupInsideMap(page);
      } else {
        await expect(page.locator('#location-map')).toBeHidden();
      }
      await page.screenshot({ path: testInfo.outputPath('location-map.png'), fullPage: true });
      await (javaScriptEnabled ? page.locator('.leaflet-popup-content') : page.locator('#main')).getByRole('link', { name: 'Check-in at Equator café', exact: true }).click();
      await expect(page).toHaveURL(new RegExp(publicPath + '$'));
      await expect(page.getByRole('heading', { name: 'Check-in at Equator café', exact: true })).toBeVisible();

      await page.goto('/admin/settings');
      await page.locator('#tab-posts').click();
      await page.getByRole('checkbox', { name: 'Enable location map', exact: true }).uncheck();
      await page.getByRole('button', { name: 'Save settings', exact: true }).click();
      expect((await page.request.get('/map/')).status()).toBe(404);
      await page.goto(editorPath);
      if (javaScriptEnabled) await page.getByRole('tab', { name: 'Details', exact: true }).click();
      await expect(page.getByLabel('Place name (required to publish)', { exact: true })).toHaveValue('Private draft place');
      expect(errors).toEqual([]);
    });
  });
}

test('every post has a marker and overlapping markers expose safe links to all posts at that place', async ({ owner: page }, testInfo) => {
  await page.route('https://tile.openstreetmap.org/**', route => route.abort());
  await page.goto('/admin/settings');
  await page.locator('#tab-about').click();
  await page.getByLabel('Author display name', { exact: true }).fill('Ada');
  await page.locator('#tab-posts').click();
  await page.getByRole('checkbox', { name: 'Enable location map', exact: true }).check();
  await page.getByRole('button', { name: 'Save settings', exact: true }).click();
  const places = [
    ['Chicago café', '41.88', '-87.63'],
    ['Chicago <img src=x onerror=alert(1)>', '41.88', '-87.63'],
    ['London café', '51.5', '-0.09']
  ];
  for (const [place, latitude, longitude] of places) {
    await newPost(page, 'check-in');
    await page.getByRole('tab', { name: 'Details', exact: true }).click();
    await page.getByLabel('Place name (required to publish)', { exact: true }).fill(place);
    await page.getByLabel('Latitude (optional)', { exact: true }).fill(latitude);
    await page.getByLabel('Longitude (optional)', { exact: true }).fill(longitude);
    await publish(page);
  }
  await page.goto('/map/');
  await expect(page.locator('.leaflet-marker-icon')).toHaveCount(3);
  await expect(page.getByRole('status')).toContainText('Map tiles could not be loaded.');
  // Keyboard activation also reaches a marker hidden underneath another marker.
  await page.locator('.leaflet-marker-icon[alt="Check-in at Chicago café"]').focus();
  await page.keyboard.press('Enter');
  const popup = page.locator('.leaflet-popup-content');
  await expect(popup.getByRole('link')).toHaveCount(2);
  await expect(popup.getByText('Chicago <img src=x onerror=alert(1)>', { exact: false }).first()).toBeVisible();
  await expect(popup.locator('img')).toHaveCount(0);
  await expectPopupInsideMap(page);
  await page.screenshot({ path: testInfo.outputPath('location-map-markers.png'), fullPage: true });
  await popup.getByRole('link', { name: 'Check-in at Chicago café', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Check-in at Chicago café', exact: true })).toBeVisible();
});
