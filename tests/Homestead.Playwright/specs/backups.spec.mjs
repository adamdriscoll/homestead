import { test, expect } from '../support/fixtures.mjs';

for (const javaScriptEnabled of [true, false]) {
  test.describe(`backup settings with JavaScript ${javaScriptEnabled ? 'enabled' : 'disabled'}`, () => {
    test.use({ javaScriptEnabled });

    test('saves private configuration, retains credentials, reports upload failure, and disables backups', async ({ owner: page }) => {
      await page.goto('/admin/settings');
      await page.locator('#tab-backup').click();
      const backup = page.locator('#backup');
      await expect(backup.getByRole('heading', { name: 'Backup', exact: true })).toBeVisible();
      await backup.getByLabel('Enable automated backups').check();
      await backup.getByLabel('S3 endpoint URL').fill('https://127.0.0.1:1');
      await backup.getByLabel('Bucket', { exact: true }).fill('private-backups');
      await backup.getByLabel('Access key ID').fill('browser-private-access');
      await backup.getByLabel('Secret access key', { exact: true }).fill('browser-private-secret');
      await backup.getByRole('button', { name: 'Save backup settings', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('Backup settings saved.');
      await expect(backup.getByLabel('Access key ID')).toHaveValue('');
      await expect(backup.getByLabel('Secret access key', { exact: true })).toHaveValue('');
      await expect(backup.getByText('Credentials saved: Yes.', { exact: false })).toBeVisible();
      await page.reload();
      await expect(backup.getByLabel('Enable automated backups')).toBeChecked();
      await backup.getByRole('button', { name: 'Save backup settings', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('Backup settings saved.');
      await backup.getByRole('button', { name: 'Back up now', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('Backup failed.');
      await expect(backup.getByText('Backup failed.', { exact: false }).first()).toBeVisible();
      expect(await page.content()).not.toContain('browser-private-secret');
      expect(await page.content()).not.toContain('browser-private-access');
      await backup.getByLabel('Clear saved credentials').check();
      await backup.getByLabel('Enable automated backups').uncheck();
      await backup.getByRole('button', { name: 'Save backup settings', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('Backup settings saved.');
      await expect(backup.getByText('Credentials saved: No.', { exact: false })).toBeVisible();
      for (const appearance of ['light', 'dark']) {
        await page.evaluate(value => document.documentElement.setAttribute('data-theme', value), appearance);
        await expect(backup.getByRole('button', { name: 'Save backup settings', exact: true })).toBeVisible();
        expect(await backup.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
      }
    });
  });
}
