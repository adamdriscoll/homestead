import { test as base, expect } from '@playwright/test';
import { DockerSite } from './docker.mjs';

export const test = base.extend({
  site: [async ({}, use, testInfo) => {
    const site = new DockerSite();
    let started = false;
    try {
      await site.start();
      started = true;
      await use(site);
    } finally {
      if (!started || testInfo.status !== testInfo.expectedStatus) {
        try {
          await testInfo.attach('docker.log', { body: await site.logs(), contentType: 'text/plain' });
        } catch (error) { console.error('Could not capture Docker logs:', error.message); }
      }
      await site.stop();
    }
  }, { timeout: 120_000 }],
  baseURL: async ({ site }, use) => use(site.baseURL),
  owner: async ({ page, site }, use) => {
    await createAdministrator(page, site);
    await signIn(page, site);
    await use(page);
  }
});

export { expect };

export async function createAdministrator(page, site) {
  await page.goto('/setup');
  await page.getByLabel('Setup secret').fill(site.setupToken);
  await page.getByLabel('Email', { exact: true }).fill(site.email);
  await page.getByLabel('Password', { exact: true }).fill(site.password);
  await page.getByLabel('Confirm password', { exact: true }).fill(site.password);
  await page.getByRole('button', { name: 'Create administrator' }).click();
  await expect(page).toHaveURL(/\/account\/login$/);
}

export async function signIn(page, site) {
  await page.getByLabel('Email', { exact: true }).fill(site.email);
  await page.getByLabel('Password', { exact: true }).fill(site.password);
  await page.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(page).toHaveURL(/\/admin$/);
}

export async function newPost(page, type = 'article') {
  await page.goto('/admin/posts');
  await page.getByRole('button', { name: `New ${type}`, exact: true }).click();
  await expect(page).toHaveURL(/\/admin\/posts\/[\da-f-]+\/edit$/);
  await expect(page.getByRole('heading', { name: `Your ${type}`, exact: true })).toBeVisible();
}

export async function saved(page) {
  await expect(page.locator('#save-status')).toHaveAttribute('data-state', 'saved');
}

export async function publish(page, name = 'Publish') {
  // On updates the public link is already visible; wait for the new document before returning it.
  await Promise.all([
    page.waitForEvent('domcontentloaded'),
    page.getByRole('button', { name, exact: true }).click()
  ]);
  await expect(page.getByRole('link', { name: 'Open public post' })).toBeVisible();
  return page.getByRole('link', { name: 'Open public post' }).getAttribute('href');
}
