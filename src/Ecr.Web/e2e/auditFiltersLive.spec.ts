import { expect, test, type Page } from '@playwright/test';

/**
 * ✎ P-1124-4 (слово людини 24.11: «переглянь всі колонки для аудиту і показуй у місцевому часі, також перевір
 * фільтри щоб вони дійсно працювали») — журнал безпеки проти РЕАЛЬНОГО сервера стенда
 * (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep auditFiltersLive`):
 *
 *  - події з РІЗНИМИ авторами й типами створюються справжніми діями: вхід `e2e-operator` (LoginSucceeded),
 *    невдалий вхід неіснуючого імені (LoginFailed, анонімний автор), вхід `e2e-admin`;
 *  - «Event type» — вибір із типів вікна; обраний тип звужує журнал на СЕРВЕРІ (кожен рядок — цей тип);
 *  - «By user» має варіанти (раніше — порожнє «Anyone» без `Security.ManageUsers`) і звужує журнал до автора;
 *  - обидва разом — кон'юнкція;
 *  - час запису — ОДНИМ рядком, у поясі браузера, 24-годинним записом (не «9:12 PM» майданчика).
 *
 * ⛔ Без стенда файл ПАДАЄ, а не пропускається (honesty-guard, `globalSetup.ts`).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Operator = { user: 'e2e-operator', password: 'E2E-Oper8tor-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

test.describe('Журнал безпеки: фільтри й місцевий час на реальному стенді', () => {
  test.beforeAll(() => {
    if (PeriodKey === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD (tools/e2e-stand.ps1)');
    }
  });

  test.describe.configure({ timeout: 240_000 });

  test('Event type і By user звужують журнал на сервері; час — один рядок, місцевий, 24 год', async ({ page, playwright, baseURL }) => {
    // Події з різними авторами й типами — справжніми входами, без прямого SQL.
    const api = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    const operatorLogin = await api.post('/api/v1/login/local', { data: { userName: Operator.user, password: Operator.password } });
    expect(operatorLogin.ok(), 'вхід e2e-operator не вдався').toBe(true);
    const failed = await api.post('/api/v1/login/local', {
      data: { userName: `e2e-nobody-${String(Date.now())}`, password: 'wrong-password' },
    });
    expect(failed.ok(), 'вхід неіснуючого імені мав бути відхилений').toBe(false);
    await api.dispose();

    await page.setViewportSize({ width: 1335, height: 860 });
    await signIn(page);
    await page.goto('/admin/audit?view=security');

    const rows = page.locator('table.ecr-audit-table tbody tr');
    await expect(rows.first(), 'журнал безпеки порожній').toBeVisible({ timeout: 30_000 });

    // Час: один рядок (одна рядкова коробка), пояс браузера, 24 год — той самий запис, що дає Intl браузера.
    const time = rows.first().locator('td[data-audit-time-cell] time');
    const shown = await time.evaluate((node) => {
      const at = node.getAttribute('datetime') ?? '';
      const expected = new Intl.DateTimeFormat('en', { dateStyle: 'medium', timeStyle: 'short', hourCycle: 'h23' }).format(
        new Date(at),
      );
      return { text: node.textContent ?? '', expected, lines: node.getClientRects().length, title: node.getAttribute('title') ?? '' };
    });
    expect(shown.text, 'час не в поясі браузера / не 24 год').toBe(shown.expected);
    expect(shown.text).not.toMatch(/AM|PM/u);
    expect(shown.lines, `час переноситься: «${shown.text}»`).toBe(1);
    expect(shown.title, 'у підказці немає точного ISO').toMatch(/^\d{4}-\d{2}-\d{2}T/u);
    const header = page.locator('th[data-audit-sort-column="time"]');
    expect(await header.evaluate((node) => node.getBoundingClientRect().height), 'заголовок «Changed at» переноситься').toBeLessThan(
      45,
    );

    // Event type → LoginFailed: кожен рядок — цей тип, і запит пішов на сервер із кодом.
    await pick(page, 'Event type', /^Failed sign-in attempt$/u);
    await expect(page).toHaveURL(/eventType=LoginFailed/u);
    await expectEvery(rows, '[data-audit-event-type]', 'data-audit-event-type', 'LoginFailed');

    // By user → E2E operator (з типом LoginSucceeded): кожен рядок — цей автор.
    await pick(page, 'Event type', /^Successful sign-in$/u);
    await pick(page, 'By user', /^E2E operator$/u);
    await expect(page).toHaveURL(/changedBy=\d+/u);
    await expectEvery(rows, '[data-audit-event-type]', 'data-audit-event-type', 'LoginSucceeded');
    await expectEveryText(rows, '[data-audit-author]', 'E2E operator');

    // Інший автор — інші рядки (фільтр справді на сервері, а не випадковий збіг).
    await pick(page, 'By user', /^E2E administrator$/u);
    await expectEveryText(rows, '[data-audit-author]', 'E2E administrator');
  });
});

/** Вибір у Mantine `Select` за підписом поля. */
async function pick(page: Page, label: string, option: RegExp): Promise<void> {
  await page.getByLabel(label, { exact: true }).click();
  await page.getByRole('option', { name: option }).first().click();
}

/** Кожен рядок (після оновлення видачі) має атрибут із цим значенням; рядків ≥ 1. */
async function expectEvery(
  rows: ReturnType<Page['locator']>,
  selector: string,
  attribute: string,
  value: string,
): Promise<void> {
  await expect
    .poll(
      async () => {
        const values = await rows.locator(selector).evaluateAll((nodes, name) => nodes.map((n) => n.getAttribute(name)), attribute);
        return values.length > 0 && values.every((v) => v === value);
      },
      { timeout: 30_000, message: `не кожен рядок має ${attribute}=${value}` },
    )
    .toBe(true);
}

async function expectEveryText(rows: ReturnType<Page['locator']>, selector: string, text: string): Promise<void> {
  await expect
    .poll(
      async () => {
        const values = await rows.locator(selector).allTextContents();
        return values.length > 0 && values.every((v) => v.trim() === text);
      },
      { timeout: 30_000, message: `не кожен рядок — автор «${text}»` },
    )
    .toBe(true);
}

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
