import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { CreatedPermitTypes } from './permitTypeCleanup';

/**
 * Permits (lookup-prm): шапка типу дозволу `PRM_<TYPE>` як ціль Lookup поля шапки шаблону і піцкер шапки документа.
 *
 * Що доводиться тут, а не у vitest: справжній сервер віддає `PRM_*` у цілях Lookup (`?includePermitHeaders=true`),
 * `PUT …/header-fields/{code}` приймає такий довідник, а піцкер шапки документа читає записи у ВІКНІ проєкту
 * (`entries?documentId=`) і показує лише активні й чинні дозволи за НОМЕРОМ - чернетка, дозвіл поза вікном
 * (майбутній і закінчений) не пропонуються. Вибір зберігається і переживає перезавантаження; сценарій вибору
 * повторюється 5 разів (N/5), щоб побачити гонки піцкера.
 *
 * ⚠ Сценарій самодостатній: сам заводить через API тип, дозволи, шаблон, проєкт і документ. Опублікований тип
 * видалити не можна (`ECR-PRM-4094`), тож коди унікальні, а стенд - одноразова база.
 *
 * Запуск (стенд - `tools/e2e-stand.ps1`): `-Grep "Permit lookup header"`.
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

/** Відповідь API без мовчазного провалу: статус і тіло йдуть у повідомлення, щоб лог CI пояснював себе сам. */
async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${response.status()} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
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

interface Scenario {
  readonly documentId: number;
  readonly numbers: { readonly activeA: string; readonly activeB: string; readonly draft: string; readonly future: string; readonly expired: string };
  readonly period: string;
}

async function createPermit(
  page: Page,
  typeCode: string,
  number: string,
  validFrom: string,
  validTo: string,
  activate: boolean,
): Promise<void> {
  const created = await ok<{ id: number }>(
    await page.request.post('/api/v1/permits', {
      data: {
        typeCode,
        number,
        location: 'E2E facility',
        issueDate: validFrom,
        validFrom,
        validTo,
        reportingFlags: { form300: false, form301: false, form302: false, tox: false },
      },
    }),
    `дозвіл ${number}`,
  );
  if (activate) {
    await ok(await page.request.post(`/api/v1/permits/${String(created.id)}/activate`), `активація ${number}`);
  }
}

/** Тип + п'ять дозволів, шаблон з полем шапки Lookup на `PRM_<TYPE>`, проєкт 2026 і документ. */
async function arrange(page: Page, tracked: CreatedPermitTypes): Promise<Scenario> {
  const stamp = String(Date.now());
  const typeCode = tracked.track(`E2E_LKP_${stamp}`);

  const created = await ok<{ rowVersion: string; nameL10n: Record<string, string> }>(
    await page.request.post('/api/v1/permit-types', { data: { code: typeCode, category: 'Water', nameL10n: { en: `E2E lookup type ${stamp}` } } }),
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
            columns: [column('Name', 1, { key: true, required: true })],
            rules: [],
          },
        ],
      },
    }),
    'заміна чернетки типу',
  );
  await ok(
    await page.request.post(`/api/v1/permit-types/${typeCode}/publish`, { data: { reason: 'E2E lookup header', rowVersion: replaced.rowVersion } }),
    'публікація типу',
  );

  const numbers = {
    activeA: `E2E-LKP-A-${stamp}`,
    activeB: `E2E-LKP-B-${stamp}`,
    draft: `E2E-LKP-DRAFT-${stamp}`,
    future: `E2E-LKP-FUT-${stamp}`,
    expired: `E2E-LKP-OLD-${stamp}`,
  };
  await createPermit(page, typeCode, numbers.activeA, '2026-01-10', '2027-01-10', true);
  await createPermit(page, typeCode, numbers.activeB, '2026-02-01', '2026-12-01', true);
  await createPermit(page, typeCode, numbers.draft, '2026-01-10', '2027-01-10', false);
  await createPermit(page, typeCode, numbers.future, '2028-01-01', '2029-01-01', true);
  await createPermit(page, typeCode, numbers.expired, '2024-01-01', '2025-01-01', true);

  // Реєстр шапки типу видно лише в цілях Lookup, не в звичайному переліку «Довідники».
  const plain = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/registries'), 'перелік довідників');
  expect(plain.some((registry) => registry.code.startsWith('PRM_')), 'звичайний перелік не повинен віддавати PRM_*').toBe(false);
  const targets = await ok<{ id: number; code: string }[]>(
    await page.request.get('/api/v1/registries?includePermitHeaders=true'),
    'цілі Lookup',
  );
  const header = targets.find((registry) => registry.code.toUpperCase() === `PRM_${typeCode}`.toUpperCase());
  expect(header, `шапку PRM_${typeCode} не віддано в цілях Lookup`).toBeDefined();

  // Шаблон: один аркуш із таблицею і поле шапки Lookup на шапку типу.
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2ELkp${stamp}`, nameL10n: { en: `E2E lookup ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await page.request.post(`/api/v1/templates/${template.templateId}/versions`, { data: { versionNumber: '1.0.0.0', cloneFromVersionId: null } }),
    'версія шаблону',
  );
  const base = `/api/v1/template-versions/${version.versionId}`;
  await ok(
    await page.request.put(`${base}/sheets/Main`, { data: { nameL10n: { en: 'Main' }, ordinal: 0, isMandatory: true, isVisible: true, sheetGroup: null } }),
    'аркуш',
  );
  const table = await ok<{ id: number }>(
    await page.request.put(`${base}/sheets/Main/tables/Data`, {
      data: { nameL10n: { en: 'Data' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 10 },
    }),
    'таблиця',
  );
  await ok(
    await page.request.put(`${base}/tables/${table.id}/columns/Note`, {
      data: {
        headerL10n: { en: 'Note' },
        ordinal: 0,
        dataType: 'String',
        isRequired: false,
        isReadOnly: false,
        isHidden: false,
        precision: null,
        scale: null,
        defaultValue: null,
        displayFormat: null,
        lookupFilter: null,
        lookupRegistryDefId: null,
        styleId: null,
        unitId: null,
      },
    }),
    'стовпець',
  );
  await ok(
    await page.request.put(`${base}/header-fields/PermitNo`, {
      data: {
        dataType: 'Lookup',
        isRequired: false,
        labelL10n: { en: 'Permit number' },
        lookupRegistryDefId: header?.id ?? null,
        ordinal: 0,
      },
    }),
    'поле шапки Lookup на PRM_*',
  );
  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'E2E lookup header' } }), 'публікація версії шаблону');

  const structure = await ok<{ sheets: { id: number; code: string }[] }>(await page.request.get(`${base}/structure`), 'структура версії');
  const sheetId = structure.sheets.find((sheet) => sheet.code === 'Main')?.id ?? 0;

  const policies = await ok<{ id: number }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики періодів');
  expect(policies.length, 'на стенді немає жодної політики періодів').toBeGreaterThan(0);

  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2ELKP${stamp}`,
        nameL10n: { en: `E2E lookup project ${stamp}` },
        periodKind: 'Monthly',
        periodPolicyId: policies[0]?.id ?? null,
        templateVersionId: version.versionId,
        timeZoneId: 'Asia/Atyrau',
        year: 2026,
      },
    }),
    'проєкт',
  );
  const document = await ok<{ documentId: number }>(
    await page.request.post('/api/v1/documents', { data: { projectId: project.projectId, sheetDefIds: [sheetId], templateVersionId: version.versionId } }),
    'документ',
  );

  // Період - з календаря проєкту (його вікно задає сервер), а не вгадується з року.
  const calendar = await ok<{ periods: { periodKey: number }[] }>(
    await page.request.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар проєкту',
  );
  const periodKey = calendar.periods[0]?.periodKey;
  expect(periodKey, 'у проєкту немає періодів').toBeDefined();

  return { documentId: document.documentId, numbers, period: String(periodKey) };
}

async function openHeader(page: Page, s: Scenario): Promise<void> {
  await page.goto(`/documents/${String(s.documentId)}?periodKey=${s.period}`);
  const toggle = page.getByTestId('document-header-toggle');
  await expect(toggle).toBeVisible({ timeout: 60_000 });
  if ((await toggle.getAttribute('aria-expanded')) !== 'true') await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
}

test.describe('Permit lookup header', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  const tracked = new CreatedPermitTypes();
  test.afterEach(async ({ page }) => {
    await tracked.cleanup(page);
  });

  test('піцкер шапки пропонує лише активні дозволи у вікні проєкту за номером; вибір зберігається (5 повторів)', async ({ page }) => {
    test.setTimeout(420_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);
    const s = await arrange(page, tracked);

    // Піцкер шапки: список = рівно активні дозволи вікна проєкту, підпис - номер дозволу.
    await openHeader(page, s);
    const field = page.getByRole('textbox', { name: 'Permit number' });
    await expect(field).toBeVisible({ timeout: 30_000 });
    await expect(field).toBeEnabled({ timeout: 30_000 });

    await field.click();
    // ⚠ Лише список цього поля: на сторінці є й інші `option` (панель інструментів сітки).
    const options = page.getByRole('listbox', { name: 'Permit number' }).getByRole('option');
    await expect(options.first()).toBeVisible({ timeout: 15_000 });
    const shown = (await options.allTextContents()).map((text) => text.trim()).sort();
    expect(shown, 'піцкер має пропонувати лише активні дозволи, чинні у вікні проєкту').toEqual([s.numbers.activeA, s.numbers.activeB].sort());
    for (const hidden of [s.numbers.draft, s.numbers.future, s.numbers.expired]) {
      expect(shown, `дозвіл ${hidden} не повинен пропонуватись`).not.toContain(hidden);
    }
    await page.keyboard.press('Escape');

    // N/5: вибір → Save → перезавантаження → значення на місці; номери чергуються, тож кожен вибір - справжня зміна.
    const picks = [s.numbers.activeA, s.numbers.activeB, s.numbers.activeA, s.numbers.activeB, s.numbers.activeA];
    for (const [index, number] of picks.entries()) {
      await openHeader(page, s);
      const input = page.getByRole('textbox', { name: 'Permit number' });
      await expect(input).toBeEnabled({ timeout: 30_000 });
      await input.click();
      await page.getByRole('option', { name: number, exact: true }).click();
      await expect(input).toHaveValue(number);

      const saved = page.waitForResponse(
        (response) => response.request().method() === 'PATCH' && response.url().includes(`/documents/${String(s.documentId)}/header`),
      );
      await page.getByTestId('document-header-panel').getByRole('button', { name: /^(Save|Зберегти|Сохранить|Сақтау)$/ }).click();
      const response = await saved;
      expect(response.ok(), `повтор ${String(index + 1)}/5: збереження шапки: ${response.status()} ${await response.text()}`).toBe(true);

      await page.reload();
      await openHeader(page, s);
      await expect(page.getByRole('textbox', { name: 'Permit number' }), `повтор ${String(index + 1)}/5: значення не пережило перезавантаження`).toHaveValue(number, {
        timeout: 20_000,
      });
    }
  });
});
