import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const BACKEND_URL = process.env.E2E_API_BASE_URL ?? 'http://localhost:8080';
const BEARER = process.env.E2E_BEARER_TOKEN ?? 'dev-key-mrc-acme';

test.beforeAll(async ({ request }) => {
  // Probe readiness — fail fast if the backend isn't reachable.
  let ready = false;
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    try {
      const res = await request.get(`${BACKEND_URL}/health/ready`);
      if (res.ok()) {
        ready = true;
        break;
      }
    } catch {
      // retry
    }
    await new Promise((r) => setTimeout(r, 1000));
  }
  if (!ready) {
    test.skip(true, `Backend not ready at ${BACKEND_URL} — skipping E2E`);
  }

  // Optimistically create a payment so the list isn't empty.
  await request
    .post(`${BACKEND_URL}/v1/payments`, {
      headers: {
        Authorization: `Bearer ${BEARER}`,
        'Idempotency-Key': `E2E${Date.now().toString(36).toUpperCase()}`,
      },
      data: {
        amountMinor: 89_900,
        currency: 'USD',
        cardToken: 'tok_VISA_4242',
        customerReference: 'order_e2e',
      },
    })
    .catch(() => null);
});

test('payments list → detail happy path', async ({ page }) => {
  await page.goto('/payments');

  // 1. Page renders with the title and main table.
  await expect(page.getByRole('heading', { level: 1, name: /Payments/i })).toBeVisible();
  await expect(page.getByRole('table')).toBeVisible({ timeout: 10_000 });

  // 2. Click into the first row.
  const firstRow = page.getByRole('link', { name: /Open payment pay_/ }).first();
  await firstRow.waitFor({ state: 'visible' });
  const aria = (await firstRow.getAttribute('aria-label')) ?? '';
  const paymentId = aria.replace('Open payment ', '');
  await firstRow.click();

  // 3. Detail page shows the payment ID and a status badge.
  await expect(page).toHaveURL(new RegExp(`/payments/${paymentId.replace(/_/g, '_')}`));
  await expect(page.getByText(paymentId)).toBeVisible();
  await expect(page.getByRole('status', { name: /Status:/ })).toBeVisible();
});

test('axe-core has no serious or critical violations on the list page', async ({ page }) => {
  await page.goto('/payments');
  await page.getByRole('table').waitFor({ state: 'visible', timeout: 10_000 });

  const results = await new AxeBuilder({ page })
    .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'])
    .analyze();

  const blocking = results.violations.filter(
    (v) => v.impact === 'serious' || v.impact === 'critical',
  );
  if (blocking.length > 0) {
    // eslint-disable-next-line no-console
    console.error('axe violations:', JSON.stringify(blocking, null, 2));
  }
  expect(blocking, 'No serious/critical accessibility violations').toHaveLength(0);
});
