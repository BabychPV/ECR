import { expect, test, type Page } from '@playwright/test';
import { axeViolations } from './axeRule';

/**
 * Permits subnav 500: смуга розділів Permits (`PermitsSubnav`, `.pm-nav`) на 500 px - кожне посилання, зокрема
 * «NAE structure», видно ПОВНІСТЮ в межах вікна (раніше шосте посилання було відрізане прихованою горизонтальною
 * прокруткою), на нього можна клікнути й дійти до сторінки; без горизонтальної прокрутки сторінки; axe (контраст, мітки)
 * у світлій і темній темах. Реальний стенд, без підміни. Запуск: `-Grep "Permits subnav 500"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Width = 500;

async function setTheme(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  const put = await page.request.put('/api/v1/me/preferences/theme', {
    headers: { 'Content-Type': 'application/json' },
    data: JSON.stringify(scheme),
  });
  expect(put.ok(), `тема: ${String(put.status())}`).toBe(true);
}

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

test.describe('Permits subnav 500', () => {
  test.describe.configure({ timeout: 120_000 });

  test.afterAll(async ({ browser }) => {
    // Тему повертаємо в light (сервер перемагає localStorage).
    const context = await browser.newContext();
    try {
      await context.request.post('/api/v1/login/local', { data: { userName: Admin.user, password: Admin.password } });
      await context.request.put('/api/v1/me/preferences/theme', {
        headers: { 'Content-Type': 'application/json' },
        data: JSON.stringify('light'),
      });
    } finally {
      await context.close();
    }
  });

  for (const scheme of ['light', 'dark'] as const) {
    test(`Permits subnav 500: усі вкладки розділу видно й клікабельні, ${scheme}`, async ({ page }) => {
      await page.setViewportSize({ width: Width, height: 900 });
      await signIn(page);
      await setTheme(page, scheme);
      await page.goto('/admin/permits');
      await expect
        .poll(() => page.evaluate(() => document.documentElement.getAttribute('data-mantine-color-scheme')), { timeout: 15_000 })
        .toBe(scheme);

      const nav = page.locator('nav.pm-nav');
      await expect(nav).toBeVisible({ timeout: 30_000 });
      const links = nav.getByRole('link');
      const count = await links.count();
      expect(count, 'у смузі розділів має бути кілька посилань, зокрема NAE structure').toBeGreaterThanOrEqual(5);

      for (let i = 0; i < count; i += 1) {
        const link = links.nth(i);
        // Без scrollIntoView: вкладка має бути видимою одразу, а не після прихованої прокрутки смуги.
        await expect(link).toBeVisible();
        const box = await link.boundingBox();
        const text = (await link.textContent()) ?? `#${String(i)}`;
        expect(box, `${text}: без розмірів`).not.toBeNull();
        expect(box?.x ?? -1, `«${text}» відрізане ліворуч`).toBeGreaterThanOrEqual(0);
        expect((box?.x ?? 0) + (box?.width ?? 0), `«${text}» відрізане праворуч`).toBeLessThanOrEqual(Width + 1);
      }

      const hidden = await nav.evaluate((el) => el.scrollWidth - el.clientWidth);
      expect(hidden, 'смуга розділів має приховану горизонтальну прокрутку (вкладка відрізана)').toBeLessThanOrEqual(1);

      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow, 'горизонтальна прокрутка сторінки').toBeLessThanOrEqual(1);
      expect(await axeViolations(page, 'color-contrast'), `контраст (${scheme})`).toEqual([]);
      expect(await axeViolations(page, 'link-name'), `імена посилань (${scheme})`).toEqual([]);

      await nav.locator('a[href="/admin/permits/nae"]').click();
      await expect(page).toHaveURL(/\/admin\/permits\/nae$/);
      await expect(page.locator('nav.pm-nav a[aria-current="page"]')).toHaveAttribute('href', '/admin/permits/nae');
    });
  }
});
