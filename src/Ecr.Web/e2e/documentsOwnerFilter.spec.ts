import { expect, test, type Page } from '@playwright/test';

/**
 * ✎ D-450 (рішення людини 24.10): перелік документів показує власника й фільтрує за конкретною людиною — на
 * справжньому стенді, без підміни відповідей: колонка «Owner», вибір людини зі списку (той самий сервер, та сама межа
 * видимості), `owner=` в адресі, у кожному рядку — та сама людина, «Clear filters» знімає.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

test.describe('Перелік документів: власник і фільтр за людиною (D-450)', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('колонка «Owner» і вибір людини звужують перелік до її документів', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, Operator.user, Operator.password);

    // Перелік людей не читається з відкриттям сторінки — лише з відкриттям списку.
    const ownerRequests: string[] = [];
    page.on('request', (request) => {
      if (request.url().includes('/api/v1/documents/owners')) ownerRequests.push(request.url());
    });

    await page.goto(`/?periodKey=${PeriodKey}`);
    const table = page.locator('[data-documents-table]');
    await expect(table, 'таблиця переліку не з\'явилась').toBeVisible({ timeout: 30_000 });
    await expect(table.locator('thead [data-column="owner"]')).toBeVisible();

    const firstOwner = table.locator('tbody [data-column="owner"]').first();
    await expect(firstOwner).toBeVisible();
    expect(ownerRequests, 'перелік людей прочитано без відкриття списку').toHaveLength(0);

    // Людина з відповіді сервера (перша в списку — найбільше документів).
    const field = page.locator('input[data-owner-filter]');
    await field.click();
    const person = page.getByRole('option').filter({ hasNotText: /Everyone|Mine|Все|Мои|Барлығы|Менікі/ }).first();
    await expect(person, 'у списку немає жодної людини').toBeVisible({ timeout: 30_000 });
    expect(ownerRequests.length).toBeGreaterThan(0);

    const name = ((await person.locator('p').first().textContent()) ?? '').trim();
    expect(name.length).toBeGreaterThan(0);

    const listed = page.waitForResponse((response) => /\/api\/v1\/documents\?.*ownerUserId=\d+/.test(response.url()));
    await person.click();
    await listed;

    await expect.poll(() => new URL(page.url()).searchParams.get('owner'), { timeout: 15_000 }).toMatch(/^\d+$/);
    expect(new URL(page.url()).searchParams.has('mine')).toBe(false);

    const owners = table.locator('tbody [data-column="owner"]');
    await expect(owners.first()).toBeVisible({ timeout: 30_000 });
    for (const cell of await owners.all()) {
      await expect(cell, 'під фільтром рядок чужого документа').toHaveText(name);
    }

    await page.locator('[data-clear-filters]').click();
    await expect.poll(() => new URL(page.url()).searchParams.has('owner'), { timeout: 15_000 }).toBe(false);
  });
});

async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
