import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * PERMITES §3 на живому стенді: вкладка Columns конструктора → вибрати 3 колонки → «Edit selected…» → група
 * «January» → у перегляді таблиці з'являється смуга January над трьома колонками; потім «— none —» прибирає її.
 * Один PATCH «все або нічого» з `expectedPresentationRevision` (макет `?dialog=edit-selected`, KIT §6.5.1).
 *
 * Фікстура — через API, як `colGroups.ts`: опублікована версія з колонками C1..C4 і однією групою January без
 * колонок (групи й призначення — презентаційні, працюють після публікації).
 */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);
  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

interface Fixture {
  readonly templateId: number;
  readonly versionId: number;
  readonly tableId: number;
}

async function arrange(page: Page): Promise<Fixture> {
  const stamp = String(Date.now());
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2EBulk${stamp}`, nameL10n: { en: `E2E bulk ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await page.request.post(`/api/v1/templates/${String(template.templateId)}/versions`, {
      data: { versionNumber: '1.0.0.0', cloneFromVersionId: null },
    }),
    'версія',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;

  await ok(
    await page.request.put(`${base}/sheets/S1`, {
      data: { nameL10n: { en: 'Sheet 1' }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null },
    }),
    'аркуш',
  );
  const table = await ok<{ id: number }>(
    await page.request.put(`${base}/sheets/S1/tables/T1`, {
      data: { nameL10n: { en: 'Table 1' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
    }),
    'таблиця',
  );
  for (let c = 1; c <= 4; c += 1) {
    await ok(
      await page.request.put(`${base}/tables/${String(table.id)}/columns/C${String(c)}`, {
        data: {
          headerL10n: { en: `C${String(c)}` },
          ordinal: c - 1,
          dataType: 'Decimal',
          isRequired: false,
          isReadOnly: false,
          isHidden: false,
          precision: 18,
          scale: 4,
          defaultValue: null,
          displayFormat: null,
          lookupFilter: null,
          lookupRegistryDefId: null,
          styleId: null,
          unitId: null,
        },
      }),
      `колонка C${String(c)}`,
    );
  }
  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: Columns bulk' } }), 'публікація');
  await ok(
    await page.request.put(`${base}/tables/${String(table.id)}/header-groups`, {
      data: { groups: [{ code: 'JAN', parentCode: null, ordinal: 0, labelL10n: { en: 'January' } }] },
    }),
    'група January',
  );

  return { templateId: template.templateId, versionId: version.versionId, tableId: table.id };
}

async function applyGroup(page: Page, columns: readonly string[], option: string): Promise<void> {
  for (const code of columns) {
    await page.getByRole('checkbox', { name: `Select column ${code}`, exact: true }).check();
  }
  const bar = page.locator('[data-columns-bulk="bar"]');
  await expect(bar).toContainText(`${String(columns.length)} selected`);
  await bar.getByRole('button', { name: 'Edit selected…' }).click();

  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible({ timeout: 30_000 });
  await dialog.getByLabel('Header group').click();
  await page.getByRole('option', { name: option, exact: true }).click();
  await dialog.getByRole('button', { name: 'Apply', exact: true }).click();

  await expect(dialog).toBeHidden({ timeout: 45_000 });
  // Успіх знімає вибір — панель зникає.
  await expect(bar).toBeHidden();
}

test.describe('Columns bulk: група заголовка для вибраних колонок', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('Columns bulk: 3 колонки → January → смуга в перегляді; «— none —» прибирає її', async ({ page }) => {
    test.setTimeout(300_000);
    await page.setViewportSize({ width: 1500, height: 1000 });
    await signIn(page);
    const fx = await arrange(page);
    const base = `/admin/templates/${String(fx.templateId)}/versions/${String(fx.versionId)}?node=t:${String(fx.tableId)}`;

    await page.goto(`${base}&tab=columns`);
    await expect(page.getByRole('checkbox', { name: 'Select column C1', exact: true })).toBeVisible({ timeout: 60_000 });

    await applyGroup(page, ['C1', 'C2', 'C3'], 'January');
    for (const code of ['C1', 'C2', 'C3']) {
      await expect(page.locator(`[data-column-group="${code}"]`), `група ${code}`).toHaveText('January');
    }
    await expect(page.locator('[data-column-group="C4"]')).toHaveText('—');

    await page.goto(`${base}&tab=preview`);
    await expect(page.locator('th[data-preview-group="January"]')).toHaveAttribute('colspan', '3', { timeout: 45_000 });
    await expect(page.locator('th[data-preview-column="C4"]')).toBeVisible();

    await page.goto(`${base}&tab=columns`);
    await expect(page.getByRole('checkbox', { name: 'Select column C1', exact: true })).toBeVisible({ timeout: 60_000 });
    await applyGroup(page, ['C1', 'C2', 'C3'], '— none — (remove from group)');
    for (const code of ['C1', 'C2', 'C3']) {
      await expect(page.locator(`[data-column-group="${code}"]`), `без групи ${code}`).toHaveText('—');
    }

    await page.goto(`${base}&tab=preview`);
    await expect(page.locator('th[data-preview-column="C1"]')).toBeVisible({ timeout: 45_000 });
    await expect(page.locator('th[data-preview-group="January"]')).toHaveCount(0);
    await expect(page.locator('[data-preview-group-row]')).toHaveCount(0);
  });
});
