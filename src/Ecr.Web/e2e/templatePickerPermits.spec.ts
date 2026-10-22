import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { CreatedPermitTypes } from './permitTypeCleanup';

/**
 * Permits (template-picker): у конструкторі шаблону вибір «Registry» поля шапки (і колонки) Lookup показує групу
 * «Permits» (шапки опублікованих типів дозволів, `PRM_*`) ПЕРШОЮ, поряд із групою «Dictionaries».
 *
 * Що доводиться тут, а не у vitest: справжній сервер віддає `PRM_<TYPE>` у `?includePermitHeaders=true`, чернетка
 * версії шаблону приймає поле шапки Lookup на цю шапку, а повторне відкриття поля показує вибране «Permits: …».
 * Вибір повторюється 5 разів (N/5) - гонки випадного списку Mantine.
 *
 * Знімки (необов'язково): `ECR_TPL_SHOTS_DIR` + `ECR_TPL_SHOTS_TAG` (before/after) - випадний список у Light/Dark на
 * 1440 і 500 пікселів. Запуск: `-Grep "Template picker permits"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${response.status()} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

interface Scenario {
  readonly typeName: string;
  readonly headerCode: string;
  readonly templateId: number;
  readonly versionId: number;
}

/** Опублікований тип дозволу (шапка `PRM_<TYPE>` з'являється в цілях Lookup) і шаблон з порожньою чернеткою версії. */
async function arrange(page: Page, tracked: CreatedPermitTypes): Promise<Scenario> {
  const stamp = String(Date.now());
  const typeCode = tracked.track(`E2E_TPP_${stamp}`);
  const typeName = `E2E picker type ${stamp}`;

  const created = await ok<{ rowVersion: string; nameL10n: Record<string, string> }>(
    await page.request.post('/api/v1/permit-types', { data: { code: typeCode, category: 'Water', nameL10n: { en: typeName } } }),
    'заведення типу дозволу',
  );
  const replaced = await ok<{ rowVersion: string }>(
    await page.request.put(`/api/v1/permit-types/${typeCode}`, {
      data: {
        category: 'Water',
        nameL10n: created.nameL10n,
        rowVersion: created.rowVersion,
        rules: [],
        tables: [
          {
            code: 'Limits',
            nameL10n: { en: 'Limits' },
            order: 1,
            columns: [
              {
                code: 'Name', nameL10n: { en: 'Name' }, dataType: 'String', unitId: null, required: true, key: true, group: null,
                precision: null, scale: null, listScope: null, listRegistryCode: null, siblingTableCode: null, choices: null, order: 1,
              },
            ],
            rules: [],
          },
        ],
      },
    }),
    'заміна чернетки типу',
  );
  await ok(
    await page.request.post(`/api/v1/permit-types/${typeCode}/publish`, { data: { reason: 'E2E template picker', rowVersion: replaced.rowVersion } }),
    'публікація типу',
  );

  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2ETpp${stamp}`, nameL10n: { en: `E2E picker ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await page.request.post(`/api/v1/templates/${template.templateId}/versions`, { data: { versionNumber: '1.0.0.0', cloneFromVersionId: null } }),
    'версія шаблону',
  );

  return { typeName, headerCode: `PRM_${typeCode}`, templateId: template.templateId, versionId: version.versionId };
}

async function shoot(page: Page, name: string): Promise<void> {
  const dir = process.env['ECR_TPL_SHOTS_DIR'];
  const tag = process.env['ECR_TPL_SHOTS_TAG'];
  if (dir === undefined || tag === undefined) return;
  mkdirSync(dir, { recursive: true });
  await page.screenshot({ path: join(dir, `${tag}-${name}.png`) });
}

async function openAddFieldForm(page: Page, s: Scenario): Promise<void> {
  await page.goto(`/admin/templates/${String(s.templateId)}/versions/${String(s.versionId)}`);
  await page.getByRole('button', { name: 'Add header field' }).first().click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible({ timeout: 30_000 });
  await dialog.getByLabel('Code', { exact: true }).fill('PermitNo');
  await dialog.getByLabel(/^Label/).first().fill('Permit number');
  await dialog.getByRole('textbox', { name: 'Data type' }).click();
  await page.getByRole('option', { name: 'Lookup' }).click();
}

test.describe('Template picker permits', () => {
  const tracked = new CreatedPermitTypes();
  // Опублікований тип видалити не можна (`ECR-PRM-4094`) - стенд одноразова база, коди унікальні за часом.

  test('поле шапки Lookup: група Permits першою, вибір PRM_* зберігається N/5', async ({ page }) => {
    test.setTimeout(240_000);
    await signIn(page);
    const s = await arrange(page, tracked);

    for (let run = 1; run <= 5; run += 1) {
      await openAddFieldForm(page, s);
      const dialog = page.getByRole('dialog');
      await dialog.getByRole('textbox', { name: 'Registry' }).click();

      const permitsGroup = page.locator('[class*="groupLabel"]').filter({ hasText: /^Permits$/ });
      await expect(permitsGroup, `прогін ${run}: групи «Permits» немає`).toBeVisible({ timeout: 30_000 });
      const option = page.getByRole('option', { name: new RegExp(`^Permits: ${s.typeName} \\(${s.headerCode}\\)$`) });
      await expect(option, `прогін ${run}: PRM_* немає в списку`).toBeVisible();
      // Група «Permits» стоїть ПЕРЕД першою групою довідників.
      const order = await page.evaluate(() => {
        const labels = Array.from(document.querySelectorAll('[class*="groupLabel"], [class*="group-label"]')).map((node) => node.textContent);
        return labels;
      });
      expect(order[0], `прогін ${run}: порядок груп ${JSON.stringify(order)}`).toBe('Permits');
      if (run === 1) await shoot(page, 'header-field-open');

      await option.click();
      await dialog.getByRole('button', { name: 'Save field' }).click();
      await expect(dialog).toBeHidden({ timeout: 30_000 });

      // Збережене: повторне відкриття поля показує «Permits: …».
      await page.getByRole('button', { name: 'Edit' }).first().click();
      await expect(page.getByRole('dialog').getByRole('textbox', { name: 'Registry' })).toHaveValue(
        new RegExp(`^Permits: ${s.typeName} \\(${s.headerCode}\\)$`),
      );
      await page.keyboard.press('Escape');

      // Наступний прогін - нова версія шаблону (поле створюється заново).
      if (run < 5) {
        const next = await ok<{ versionId: number }>(
          await page.request.post(`/api/v1/templates/${s.templateId}/versions`, { data: { versionNumber: `1.0.${String(run)}.0`, cloneFromVersionId: null } }),
          'наступна версія',
        );
        (s as { versionId: number }).versionId = next.versionId;
      }
    }
  });

  test('колонка таблиці Lookup: випадний список має групу Permits', async ({ page }) => {
    test.setTimeout(180_000);
    await signIn(page);
    const s = await arrange(page, tracked);
    const base = `/api/v1/template-versions/${String(s.versionId)}`;
    const targets = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/registries?includePermitHeaders=true'), 'цілі Lookup');
    const header = targets.find((registry) => registry.code.toUpperCase() === s.headerCode.toUpperCase());
    await ok(await page.request.put(`${base}/sheets/Main`, { data: { nameL10n: { en: 'Main' }, ordinal: 0, isMandatory: true, isVisible: true, sheetGroup: null } }), 'аркуш');
    const table = await ok<{ id: number }>(
      await page.request.put(`${base}/sheets/Main/tables/Data`, {
        data: { nameL10n: { en: 'Data' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 10 },
      }),
      'таблиця',
    );
    await ok(
      await page.request.put(`${base}/tables/${String(table.id)}/columns/Ref`, {
        data: {
          headerL10n: { en: 'Ref' }, ordinal: 0, dataType: 'Lookup', isRequired: false, isReadOnly: false, isHidden: false, precision: null,
          scale: null, defaultValue: null, displayFormat: null, lookupFilter: null, lookupRegistryDefId: header?.id ?? null, styleId: null, unitId: null,
        },
      }),
      'стовпець',
    );

    await page.goto(`/admin/templates/${String(s.templateId)}/versions/${String(s.versionId)}?node=t:${String(table.id)}`);
    await page.getByRole('button', { name: 'Edit' }).first().click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('textbox', { name: 'Registry' }).click();
    await expect(page.locator('[class*="groupLabel"]').filter({ hasText: /^Permits$/ })).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('option', { name: new RegExp(`\\(${s.headerCode}\\)$`) })).toBeVisible();
  });

  for (const scheme of ['light', 'dark'] as const) {
    for (const width of [1440, 500]) {
      test(`знімок випадного списку ${scheme} ${String(width)}`, async ({ page }) => {
        test.skip(process.env['ECR_TPL_SHOTS_DIR'] === undefined, 'знімки лише за ECR_TPL_SHOTS_DIR');
        test.setTimeout(180_000);
        await page.emulateMedia({ colorScheme: scheme });
        await page.setViewportSize({ width, height: width === 500 ? 900 : 800 });
        await signIn(page);
        // Тема - тим самим шляхом, що й у людини (меню користувача): локальне значення перекриває вподобання з сервера.
        const html = page.locator('html');
        if ((await html.getAttribute('data-mantine-color-scheme')) !== scheme) {
          await page.getByRole('button', { name: new RegExp(Admin.user, 'i') }).first().click();
          await page.getByText(scheme === 'dark' ? 'Dark' : 'Light', { exact: true }).first().click();
          await page.keyboard.press('Escape');
        }
        await expect(html, `тема ${scheme} не застосувалась`).toHaveAttribute('data-mantine-color-scheme', scheme, { timeout: 15_000 });
        const s = await arrange(page, tracked);
        // Довгий список як на Land (60+ довідників): до відповіді API додаються клони першого реєстру. Лише UI-підміна.
        await page.route('**/api/v1/registries?includePermitHeaders=true', async (route) => {
          const real = (await (await route.fetch()).json()) as { id: number; code: string; nameL10n: unknown }[];
          const template = real.find((r) => !r.code.startsWith('PRM_')) ?? real[0];
          const fakes = Array.from({ length: 60 }, (_, i) => ({
            ...template,
            id: 90_000 + i,
            code: `LAND_FAKE_${String(i).padStart(2, '0')}`,
            nameL10n: { values: { en: `Land dictionary ${String(i).padStart(2, '0')}` } },
          }));
          await route.fulfill({ json: [...real.filter((r) => !r.code.startsWith('PRM_')), ...fakes, ...real.filter((r) => r.code.startsWith('PRM_'))] });
        });
        await openAddFieldForm(page, s);
        await page.getByRole('dialog').getByRole('textbox', { name: 'Registry' }).click();
        await expect(page.getByRole('option').first()).toBeVisible();
        await shoot(page, `${scheme}-${String(width)}`);
      });
    }
  }
});
