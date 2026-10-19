import { expect, test, type Page } from '@playwright/test';

/**
 * Екран дозволу (Permits, PM-07): на живому стенді.
 *
 * Що доводиться тут, а не у vitest: справжній `PUT /api/v1/permits/{id}` з токеном `rowVersion`, справжній запис
 * рядків у прихований реєстр таблиці через `entries/batch` (поле композиції `PERMIT`), `dryRun`-перевірка вставки,
 * активація чернетки й зміна стану на екрані.
 *
 * ⚠ Тип і дозвіл заводяться через API входом `e2e-admin` (`page.request` ділить куки з вкладкою). Опублікований тип
 * сервер не видаляє (`ECR-PRM-4094`), тож код кожного прогону унікальний; `e2e-stand` розгортає базу наново.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`; на CI — workflow `e2e-stand`): `npx playwright test -g "Permit page"`.
 * Локально агентом НЕ запускався (правило «Режим тестів»).
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

function column(code: string, order: number, over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
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
    order,
    ...over,
  };
}

interface PermitJson {
  id: number;
  status: string;
  location: string | null;
  rowVersion: string;
  tables: { code: string; rowCount: number }[];
}

/** Заводить і публікує тип з таблицею `Limits` (ключ `Name`, обов'язкове число `Annual`) і дозвіл-чернетку цього типу. */
async function seedPermit(page: Page, stamp: string): Promise<{ id: number; number: string }> {
  const code = `E2E_PP_${stamp}`;
  const created = await page.request.post('/api/v1/permit-types', {
    data: { code, category: 'Water', nameL10n: { en: `E2E permit page ${stamp}` } },
  });
  expect(created.status(), `створення типу: ${await created.text()}`).toBeLessThan(300);
  const fresh = (await created.json()) as { rowVersion: string; nameL10n: Record<string, string> };

  const replaced = await page.request.put(`/api/v1/permit-types/${code}`, {
    data: {
      category: 'Water',
      nameL10n: fresh.nameL10n,
      rowVersion: fresh.rowVersion,
      rules: [],
      tables: [
        {
          code: 'Limits',
          nameL10n: { en: 'Limits' },
          order: 1,
          columns: [
            column('Name', 1, { key: true, required: true }),
            column('Annual', 2, { dataType: 'Decimal', required: true, precision: 18, scale: 3 }),
          ],
          rules: [],
        },
      ],
    },
  });
  expect(replaced.status(), `заміна чернетки: ${await replaced.text()}`).toBe(200);
  const draft = (await replaced.json()) as { rowVersion: string };

  const published = await page.request.post(`/api/v1/permit-types/${code}/publish`, {
    data: { reason: 'E2E permit page type', rowVersion: draft.rowVersion },
  });
  expect(published.status(), `публікація типу: ${await published.text()}`).toBeLessThan(300);

  const number = `E2E-PP-${stamp}`;
  const permit = await page.request.post('/api/v1/permits', {
    data: {
      typeCode: code,
      number,
      location: 'E2E facility',
      issueDate: '2026-01-05',
      validFrom: '2026-01-10',
      validTo: '2027-01-10',
      reportingFlags: { form300: false, form301: false, form302: false, tox: false },
    },
  });
  expect(permit.status(), `створення дозволу: ${await permit.text()}`).toBe(201);
  return { id: ((await permit.json()) as PermitJson).id, number };
}

async function getPermit(page: Page, id: number): Promise<PermitJson> {
  return (await (await page.request.get(`/api/v1/permits/${String(id)}`)).json()) as PermitJson;
}

test.describe('Permit page', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  test('шапка, рядки вручну і вставкою, активація', async ({ page }) => {
    test.setTimeout(240_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);
    const { id, number } = await seedPermit(page, String(Date.now()));

    // 1. Чернетка: заголовок із номером, бейдж стану, смуга «Draft», вкладки.
    await page.goto(`/admin/permits/${String(id)}`);
    await expect(page.getByRole('heading', { level: 1, name: number })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText('Draft - sheets do not read this permit yet')).toBeVisible();
    await expect(page.getByRole('tab', { name: /^Limits/ })).toBeVisible();

    // 2. Шапка: нове розташування зберігається на сервері з токеном rowVersion.
    const location = page.getByRole('textbox', { name: /^Location/ });
    await location.fill('E2E facility 2');
    await page.getByRole('button', { name: 'Save changes' }).click();
    await expect.poll(async () => (await getPermit(page, id)).location, { timeout: 15_000 }).toBe('E2E facility 2');

    // 3. Таблиця порожня; рядок додається формою.
    await page.getByRole('tab', { name: /^Limits/ }).click();
    await expect(page.getByText('No rows in Limits yet')).toBeVisible();
    await page.getByRole('button', { name: 'Add row' }).first().click();
    const drawer = page.getByRole('dialog', { name: 'New row' });
    await expect(drawer).toBeVisible();

    await drawer.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(drawer.getByText('Fill in the required fields.')).toBeVisible();

    await drawer.getByRole('textbox', { name: /^Name/ }).fill('Outfall 1');
    await drawer.getByRole('textbox', { name: /^Annual/ }).fill('12,5');
    await drawer.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page.getByRole('row', { name: /Outfall 1/ })).toBeVisible({ timeout: 15_000 });
    await expect.poll(async () => (await getPermit(page, id)).tables.find((t) => t.code === 'Limits')?.rowCount).toBe(1);

    // 4. Вставка з Excel: перевірка dryRun, потім запис двох рядків.
    await page.getByRole('button', { name: 'Paste from Excel…' }).click();
    const paste = page.getByRole('dialog', { name: 'Paste rows into Limits' });
    await expect(paste).toBeVisible();
    await paste.getByRole('textbox', { name: 'Rows' }).fill('Outfall 2\t7,25\nOutfall 3\t3');
    await expect(paste.getByText('2 rows can be added to Limits; 0 need fixing first.')).toBeVisible();
    await paste.getByRole('button', { name: 'Check rows' }).click();
    await expect(paste.getByText('2 rows pass the check, 0 are rejected by the server')).toBeVisible({ timeout: 15_000 });
    await paste.getByRole('button', { name: 'Add 2 rows' }).click();
    await expect(page.getByRole('row', { name: /Outfall 3/ })).toBeVisible({ timeout: 15_000 });
    await expect.poll(async () => (await getPermit(page, id)).tables.find((t) => t.code === 'Limits')?.rowCount).toBe(3);

    // 5. Активація: діалог, підтвердження, стан на екрані й на сервері.
    await page.getByRole('tab', { name: 'Overview' }).click();
    await page.getByRole('button', { name: 'Activate…' }).first().click();
    const activate = page.getByRole('dialog', { name: `Activate permit ${number}?` });
    await expect(activate).toBeVisible();
    await activate.getByRole('button', { name: 'Activate', exact: true }).click();
    await expect.poll(async () => (await getPermit(page, id)).status, { timeout: 15_000 }).toBe('Active');
    await expect(page.getByText('Draft - sheets do not read this permit yet')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Renew…' })).toBeVisible();
  });
});
