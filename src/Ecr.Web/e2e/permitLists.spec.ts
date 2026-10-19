import { expect, test, type Page } from '@playwright/test';
import { CreatedPermitTypes } from './permitTypeCleanup';

/**
 * PM-08d: екран «Lists» (списки вибору пермітів) — на живому стенді.
 *
 * Що доводиться тут, а не у vitest: справжні `GET permit-types`, `GET permit-types/{code}` і `GET registries`,
 * з яких екран зводить списки (окремого ендпоінта списків немає — варіант A), і шторка зі значеннями
 * «лише тип», що читаються з чернетки типу, заведеної через API.
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

test.describe('Списки пермітів: екран «Lists»', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  const created = new CreatedPermitTypes();
  test.afterEach(async ({ page }) => {
    await created.cleanup(page);
  });

  test('список «лише тип» з чернетки видно в переліку, шторка показує значення тільки для читання', async ({ page }) => {
    test.setTimeout(120_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);

    const stamp = String(Date.now());
    const code = created.track(`E2EListsPermit${stamp}`);
    const method = `E2E_Method${stamp}`;

    const made = await page.request.post('/api/v1/permit-types', {
      data: { code, category: 'Other', nameL10n: { en: `E2E_ lists permit ${stamp}` } },
    });
    expect(made.status(), await made.text()).toBe(201);
    const draft = (await made.json()) as { rowVersion: string };
    const saved = await page.request.put(`/api/v1/permit-types/${code}`, {
      data: {
        category: 'Other',
        nameL10n: { en: `E2E_ lists permit ${stamp}` },
        rowVersion: draft.rowVersion,
        rules: [],
        tables: [
          {
            code: 'Items',
            nameL10n: { en: 'Items' },
            order: 1,
            rules: [],
            columns: [
              column('Name', { key: true, required: true, order: 1 }),
              column(method, { dataType: 'Lookup', listScope: 'TypeOnly', choices: ['Alpha', 'Beta'], order: 2 }),
            ],
          },
        ],
      },
    });
    expect(saved.status(), await saved.text()).toBeLessThan(300);

    await page.goto('/admin/permits/lists');
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('navigation', { name: /Permits sections|Разделы|Бөлімдер/i })).toBeVisible();
    await expect(page.locator('[data-lists-readonly]')).toBeVisible();

    const row = page.locator('[data-list-table] table tbody tr', { hasText: method });
    await expect(row).toBeVisible({ timeout: 30_000 });
    await row.click();

    const drawer = page.getByRole('dialog');
    await expect(drawer).toBeVisible({ timeout: 10_000 });
    await expect(drawer).toContainText('Alpha');
    await expect(drawer).toContainText('Beta');
    await expect(drawer.getByRole('textbox')).toHaveCount(0);

    await drawer.locator('[data-list-open="type"]').click();
    await expect(page).toHaveURL(new RegExp(`/admin/permits/types/${code}$`), { timeout: 30_000 });
  });
});
