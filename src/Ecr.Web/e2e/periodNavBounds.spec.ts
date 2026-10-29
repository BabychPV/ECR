import { expect, test, type Page } from '@playwright/test';

/**
 * PERIODNAV (P1): стрілка періоду не виходить за період проєкту; адреса з періодом поза проєктом не
 * лишає глухого кута; перемикання періоду не крутить сторінку.
 *
 * ⚠ Межі проєкту беруться з API стенда (`GET /projects/{id}/periods`), а не зашиті: набір не залежить від дати.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('Період документа: межі проєкту (PERIODNAV)', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  async function projectPeriods(page: Page): Promise<number[]> {
    const summary = await page.request.get(`/api/v1/documents/${DocumentId}`);
    expect(summary.ok(), 'картка документа без періоду').toBe(true);
    const { projectId } = (await summary.json()) as { projectId: number };
    const calendar = await page.request.get(`/api/v1/projects/${String(projectId)}/periods`);
    expect(calendar.ok(), 'календар проєкту').toBe(true);
    const body = (await calendar.json()) as { periods: { periodKey: number }[] };

    return body.periods.map((period) => period.periodKey).sort((a, b) => a - b);
  }

  test('на останньому періоді › вимкнена; адреса за межею веде на останній допустимий', async ({ page }) => {
    test.slow();
    await signIn(page, Operator.user, Operator.password);
    const periods = await projectPeriods(page);
    const last = periods[periods.length - 1] as number;

    await page.goto(`/documents/${DocumentId}?periodKey=${String(last)}`);
    const next = page.getByRole('button', { name: 'Next period' });
    await expect(next, 'стрілка › на останньому періоді проєкту').toBeDisabled({ timeout: 30_000 });

    // Рік уперед від останнього періоду - гарантовано поза проєктом.
    await page.goto(`/documents/${DocumentId}?periodKey=${String(last + 100)}`);
    await page.waitForURL((url) => url.searchParams.get('periodKey') === String(last), { timeout: 30_000 });
    await expect(page.getByRole('button', { name: 'Next period' }), 'після переходу екран робочий').toBeDisabled({
      timeout: 30_000,
    });
    await expect(page.getByRole('button', { name: 'Previous period' })).toBeVisible();
  });

  test('перемикання періоду не зсуває прокрутку сторінки', async ({ page }) => {
    test.slow();
    await signIn(page, Operator.user, Operator.password);
    const periods = await projectPeriods(page);
    test.skip(periods.length < 2, 'у проєкті один період - перемикати нікуди');
    const last = periods[periods.length - 1] as number;
    const before = periods[periods.length - 2] as number;

    await page.goto(`/documents/${DocumentId}?periodKey=${String(before)}`);
    const next = page.getByRole('button', { name: 'Next period' });
    await expect(next).toBeEnabled({ timeout: 30_000 });
    await expect(page.locator('[data-doc-sheet-strip]').first()).toBeVisible({ timeout: 30_000 });

    const room = await page.evaluate(() => document.documentElement.scrollHeight - window.innerHeight);
    test.skip(room < 60, 'сторінка не прокручується - міряти нічого');
    const target = Math.min(400, room);
    await page.evaluate((y) => window.scrollTo(0, y), target);
    const startY = await page.evaluate(() => window.scrollY);
    expect(startY).toBeGreaterThan(0);

    // Перемикач періоду має лишатися досяжним: клік по ньому з прокрученої сторінки (Playwright докручує сам),
    // але міряємо позицію ПІСЛЯ того, як клік спрацював і сторінка перемалювалась.
    await page.evaluate(() => window.scrollTo(0, 0));
    await next.click();
    await page.waitForURL((url) => url.searchParams.get('periodKey') === String(last), { timeout: 30_000 });
    await expect(page.locator('[data-doc-sheet-strip]').first()).toBeVisible({ timeout: 30_000 });
    // Розкладка стабілізується (скелет -> документ).
    await page.waitForTimeout(1_500);
    const flat = await page.evaluate(() => window.scrollY);
    expect(flat, 'з верху сторінки перемикання не має зсувати вниз').toBeLessThanOrEqual(5);

    // Те саме з прокруткою: позиція зберігається (допуск - перемальований вміст).
    await page.getByRole('button', { name: 'Previous period' }).scrollIntoViewIfNeeded();
    await page.evaluate((y) => window.scrollTo(0, y), Math.min(startY, 120));
    const y0 = await page.evaluate(() => window.scrollY);
    await page.getByRole('button', { name: 'Previous period' }).click();
    await page.waitForURL((url) => url.searchParams.get('periodKey') === String(before), { timeout: 30_000 });
    await expect(page.locator('[data-doc-sheet-strip]').first()).toBeVisible({ timeout: 30_000 });
    await page.waitForTimeout(1_500);
    const y1 = await page.evaluate(() => window.scrollY);
    expect(Math.abs(y1 - y0), `scrollY ${String(y0)} -> ${String(y1)}`).toBeLessThanOrEqual(60);
  });
});

async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });

  await page.getByLabel(/User name|Ім'я/i).fill(user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
