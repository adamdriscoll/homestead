import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './specs',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  workers: 2,
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 10_000 },
  globalSetup: './support/global-setup.mjs',
  outputDir: '../../output/playwright/results',
  reporter: [
    ['list'],
    ['html', { outputFolder: '../../output/playwright/report', open: 'never' }]
  ],
  use: {
    // Only the disposable test instance uses Caddy's ephemeral internal CA.
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure'
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'mobile-chromium', use: { ...devices['Pixel 7'] } }
  ]
});
