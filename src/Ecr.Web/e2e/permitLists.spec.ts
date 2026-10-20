import { expect, test, type Page } from '@playwright/test';
import { CreatedPermitTypes } from './permitTypeCleanup';

/**
 * PM-08d, PM-08e: екран «Lists» (списки вибору пермітів) і «Not in the list? Add a value…» у формі рядка — на живому стенді.
 *
 * Що доводиться тут, а не у vitest: справжні `GET /permit-lists` (один запит замість 1 + N визначень типів), `GET
 * /permit-lists/{key}`, `PUT /permit-lists/{key}/items` — додавання значення «лише тип» зі шторки екрана й значення
 * СПІЛЬНОГО довідника з форми рядка дозволу, перечитування пікера й вибір нового значення, запис рядка з ним.
 *
 * ⚠ Список з'являється в екрані лише після публікації типу (чернеткові стовпці в переліку не беруть участі), тож тип
 * заводиться, наповнюється й публікується через API. Опублікований тип сервер не видаляє (`ECR-PRM-4094`) - на стенді, який
 * розгортається наново, це не заважає.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`; на CI — workflow `e2e-stand`): `npx playwright test -g "Списки пермітів"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

const column = (code: string, over: Record<string, unknown>): Record<string, unknown> => ({
  code,
  nameL10n: { en: code },
  dataType: 'String',
  unitId: null,
  required: false,
  key: false,
  group: null,
  precision: null,
  scale: null,
  listScope: null,
  listRegistryCode: null,
  siblingTableCode: null,
  choices: null,
  order: 0,
  ...over,
});

/** Заводить, наповнює й ПУБЛІКУЄ тип з таблицею `Items`: ключ `Name`, список «лише тип» і (за потреби) спільний довідник. */
async function publishType(page: Page, code: string, method: string, shared?: string): Promise<void> {
  const made = await page.request.post('/api/v1/permit-types', {
    data: { code, category: 'Other', nameL10n: { en: `E2E_ lists permit ${code}` } },
  });
  expect(made.status(), await made.text()).toBe(201);
  const draft = (await made.json()) as { rowVersion: string };
  const columns = [
    column('Name', { key: true, required: true, order: 1 }),
    column(method, { dataType: 'Lookup', listScope: 'TypeOnly', choices: ['Alpha', 'Beta'], order: 2 }),
    ...(shared === undefined
      ? []
      : [column('Substance', { dataType: 'Lookup', listScope: 'SharedRegistry', listRegistryCode: shared, order: 3 })]),
  ];
  const saved = await page.request.put(`/api/v1/permit-types/${code}`, {
    data: {
      category: 'Other',
      nameL10n: { en: `E2E_ lists permit ${code}` },
      rowVersion: draft.rowVersion,
      rules: [],
      tables: [{ code: 'Items', nameL10n: { en: 'Items' }, order: 1, rules: [], columns }],
    },
  });
  expect(saved.status(), await saved.text()).toBe(200);
  const replaced = (await saved.json()) as { rowVersion: string };
  const published = await page.request.post(`/api/v1/permit-types/${code}/publish`, {
    data: { reason: 'E2E permit lists type', rowVersion: replaced.rowVersion },
  });
  expect(published.status(), await published.text()).toBeLessThan(300);
}

interface ListDetail {
  list: { itemCount: number | null; editable: boolean };
  items: { id: number; displayL10n: Record<string, string> }[];
}

async function readList(page: Page, key: string): Promise<ListDetail> {
  const response = await page.request.get(`/api/v1/permit-lists/${encodeURIComponent(key)}`);
  expect(response.status(), await response.text()).toBe(200);

  return (await response.json()) as ListDetail;
}

test.describe('Списки пермітів: екран «Lists»', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  const created = new CreatedPermitTypes();
  test.afterEach(async ({ page }) => {
    await created.cleanup(page);
  });

  test('список «лише тип» опублікованого типу: шторка читає значення і додає нове', async ({ page }) => {
    test.setTimeout(120_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const stamp = String(Date.now());
    const code = created.track(`E2EListsPermit${stamp}`);
    const method = `E2E_Method${stamp}`;
    await publishType(page, code, method);

    // Перелік - ОДИН запит `GET /permit-lists`, а не визначення кожного типу.
    const requests: string[] = [];
    page.on('request', (request) => requests.push(new URL(request.url()).pathname));

    await page.goto('/admin/permits/lists');
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('navigation', { name: /Permits sections|Разделы|Бөлімдер/i })).toBeVisible();
    await expect(page.locator('[data-lists-editable]')).toBeVisible();

    const row = page.locator('[data-list-table] table tbody tr', { hasText: method });
    await expect(row).toBeVisible({ timeout: 30_000 });
    expect(requests.filter((path) => path === '/api/v1/permit-lists')).toHaveLength(1);
    expect(requests.filter((path) => /^\/api\/v1\/permit-types\/[^/]+$/.test(path))).toHaveLength(0);
    await row.click();

    const drawer = page.getByRole('dialog');
    await expect(drawer).toBeVisible({ timeout: 10_000 });
    await expect(drawer).toContainText('Alpha');
    await expect(drawer).toContainText('Beta');

    await drawer.getByRole('textbox', { name: 'New value' }).fill('Gamma');
    await drawer.getByRole('button', { name: 'Add value' }).click();
    await expect(drawer).toContainText('Gamma', { timeout: 15_000 });

    const key = `type:${code}:Items:${method}`;
    const list = await readList(page, key);
    expect(list.items.map((item) => item.displayL10n['en'])).toEqual(['Alpha', 'Beta', 'Gamma']);
    expect(list.list.itemCount).toBe(3);

    await drawer.locator('[data-list-open="type"]').click();
    await expect(page).toHaveURL(new RegExp(`/admin/permits/types/${code}$`), { timeout: 30_000 });
  });

  test('PUI-1: перелік відкривається з клавіатури (Tab -> рядок -> Enter), значення додається, Esc повертає фокус на рядок', async ({ page }) => {
    test.setTimeout(150_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const stamp = String(Date.now());
    const code = created.track(`E2EListsKbd${stamp}`);
    const method = `E2E_Kbd${stamp}`;
    await publishType(page, code, method);

    await page.goto('/admin/permits/lists');
    const row = page.locator('[data-list-table] table tbody tr', { hasText: method });
    await expect(row).toBeVisible({ timeout: 30_000 });
    await expect(row).toHaveAttribute('tabindex', '0');

    const focused = (selector: string) => page.evaluate((s) => document.activeElement?.matches(s) === true, selector);
    const tabUntil = async (selector: string): Promise<void> => {
      for (let i = 0; i < 80; i += 1) {
        await page.keyboard.press('Tab');
        if (await focused(selector)) return;
      }
      throw new Error(`Tab не дійшов до ${selector}`);
    };

    // Tab з останнього заголовка стовпця приводить до рядків (раніше - одразу до «Skip to main content»).
    await page.locator('[data-list-table] thead th button').last().focus();
    await tabUntil(`[data-row-key="type:${code}:Items:${method}"]`);
    await page.keyboard.press('Enter');

    const drawer = page.getByRole('dialog');
    await expect(drawer).toBeVisible({ timeout: 10_000 });
    await expect(drawer).toContainText('Alpha');

    const field = drawer.getByRole('textbox', { name: 'New value' });
    for (let i = 0; i < 30 && !(await field.evaluate((el) => el === document.activeElement)); i += 1) {
      await page.keyboard.press('Tab');
    }
    await expect(field).toBeFocused();
    await page.keyboard.type('Gamma');
    await page.keyboard.press('Tab');
    await expect(drawer.getByRole('button', { name: 'Add value' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(drawer).toContainText('Gamma', { timeout: 15_000 });

    await page.keyboard.press('Escape');
    await expect(drawer).toBeHidden({ timeout: 10_000 });
    await expect(row, 'фокус вернувся на рядок').toBeFocused();

    // Space відкриває той самий рядок і не гортає сторінку.
    await page.keyboard.press('Space');
    await expect(page.getByRole('dialog')).toBeVisible({ timeout: 10_000 });
  });

  test('форма рядка дозволу: «Add a value…» кладе значення в спільний довідник, пікер його показує', async ({ page }) => {
    test.setTimeout(180_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const stamp = String(Date.now());
    const code = created.track(`E2EAddValue${stamp}`);
    const registry = `E2E_SH_${stamp}`;
    const made = await page.request.post('/api/v1/registries', {
      data: { code: registry, nameL10n: { en: `E2E shared ${stamp}` }, isTemporal: false },
    });
    expect(made.status(), `довідник: ${await made.text()}`).toBe(201);
    await publishType(page, code, 'Method', registry);

    const permit = await page.request.post('/api/v1/permits', {
      data: {
        typeCode: code,
        number: `E2E-AV-${stamp}`,
        location: 'E2E facility',
        issueDate: '2026-01-05',
        validFrom: '2026-01-10',
        validTo: '2027-01-10',
        reportingFlags: { form300: false, form301: false, form302: false, tox: false },
      },
    });
    expect(permit.status(), await permit.text()).toBe(201);
    const id = ((await permit.json()) as { id: number }).id;

    await page.goto(`/admin/permits/${String(id)}?tab=Items`);
    await page.getByRole('button', { name: 'Add row' }).first().click();
    const drawer = page.getByRole('dialog', { name: 'New row' });
    await expect(drawer).toBeVisible({ timeout: 30_000 });

    // Кнопка є під обома списками (спільний і «лише тип»): e2e-admin має права на обидва.
    await expect(drawer.getByRole('button', { name: 'Not in the list? Add a value…' })).toHaveCount(2, { timeout: 15_000 });
    await drawer.locator('[data-add-value-field="Substance"] [data-add-value]').click();

    const dialog = page.getByRole('dialog', { name: 'Add a value' });
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText(`shared list E2E shared ${stamp}`);
    await dialog.getByRole('textbox', { name: 'Value' }).fill('Toluene');
    await dialog.getByRole('button', { name: 'Add value' }).click();
    await expect(dialog).toBeHidden({ timeout: 15_000 });

    // Нове значення вже вибране в полі (пікер перечитано), а в самому довіднику воно є.
    await expect(drawer.getByLabel(/^Substance/)).toHaveValue('Toluene', { timeout: 15_000 });
    const list = await readList(page, `shared:${registry}`);
    expect(list.items.map((item) => item.displayL10n['en'])).toContain('Toluene');

    // Рядок із новим значенням зберігається.
    await drawer.getByRole('textbox', { name: /^Name/ }).fill('Row with Toluene');
    await drawer.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page.getByRole('row', { name: /Row with Toluene/ })).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('row', { name: /Row with Toluene/ })).toContainText('Toluene');
  });
});
