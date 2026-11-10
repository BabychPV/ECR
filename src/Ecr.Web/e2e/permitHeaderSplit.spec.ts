import { expect, test, type APIRequestContext, type APIResponse, type BrowserContext, type Page } from '@playwright/test';

/**
 * Feature (j) Ship 2 (ACCEPTANCE-1108-post15 jh1–jh3, mockup light-1335-jh1…jh3): the document HEADER permit changes
 * mid-month — every row relying on the header permit with data in that month needs a split («Split needed» like a row),
 * Submit is blocked (PERMIT-SPLIT) until the rows are split. REAL stand, no response mocks (`tools/e2e-stand.ps1`,
 * workflow `e2e-stand`, `-Grep "Permit header:"`).
 *
 * World (public API, once, serial): published Water permit type with permits A (whole year) and B (from the 15th of the
 * open month M); template «Header split» with a header field PermitNo (Lookup on the type) and table «AE» (Jan…Dec, no
 * permit column — relies on the header); project, document, header PermitNo = A; rows R1 = 12.6+14.1 → typed 26.7, R2 = 7.3
 * in M.
 *
 * Screenshots: one per script line, light 1335, into the test output folder (CI artifact `e2e-stand-report-shard-N`).
 * ⚠ Data is one-shot (a published type cannot be deleted); codes carry a timestamp.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const MonthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'] as const;

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);
  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

function column(ordinal: number, header: string, dataType: string, over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    headerL10n: { en: header }, ordinal, dataType, isRequired: false, isReadOnly: false, isHidden: false, precision: null,
    scale: null, defaultValue: null, displayFormat: null, lookupFilter: null, lookupRegistryDefId: null, styleId: null,
    unitId: null, ...over,
  };
}

const iso = (date: Date): string => date.toISOString().slice(0, 10);
const pad = (n: number): string => String(n).padStart(2, '0');

interface World {
  documentId: number;
  periodKey: number;
  year: number;
  month: number;
  instanceId: number;
  rows: string[];
  bNumber: string;
}

async function arrange(request: APIRequestContext): Promise<World> {
  const stamp = String(Date.now());
  const year = new Date().getUTCFullYear();
  const typeCode = `E2E_PH_${stamp}`;
  const created = await ok<{ rowVersion: string; nameL10n: Record<string, string> }>(
    await request.post('/api/v1/permit-types', { data: { code: typeCode, category: 'Water', nameL10n: { en: `E2E header split ${stamp}` } } }),
    'тип дозволу',
  );
  const replaced = await ok<{ rowVersion: string }>(
    await request.put(`/api/v1/permit-types/${typeCode}`, {
      data: {
        category: 'Water', nameL10n: created.nameL10n, rowVersion: created.rowVersion, rules: [],
        tables: [{
          code: 'Limits', nameL10n: { en: 'Limits' }, order: 1, rules: [],
          columns: [{
            code: 'Name', nameL10n: { en: 'Name' }, dataType: 'String', unitId: null, required: true, key: true, group: null, precision: null,
            scale: null, listScope: null, listRegistryCode: null, siblingTableCode: null, choices: null, order: 1,
          }],
        }],
      },
    }),
    'чернетка типу',
  );
  await ok(await request.post(`/api/v1/permit-types/${typeCode}/publish`, { data: { reason: 'E2E header split', rowVersion: replaced.rowVersion } }), 'публікація типу');
  const targets = await ok<{ id: number; code: string }[]>(await request.get('/api/v1/registries?includePermitHeaders=true'), 'цілі Lookup');
  const headerRegistryId = targets.find((target) => target.code === `PRM_${typeCode}`)?.id ?? 0;
  expect(headerRegistryId, 'реєстр шапки типу').toBeGreaterThan(0);

  const template = await ok<{ templateId: number }>(
    await request.post('/api/v1/templates', { data: { code: `E2EPh${stamp}`, nameL10n: { en: `E2E header split ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await request.post(`/api/v1/templates/${String(template.templateId)}/versions`, { data: { versionNumber: '1.0.0.0', cloneFromVersionId: null } }),
    'версія',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;
  await ok(await request.put(`${base}/sheets/Air`, { data: { nameL10n: { en: 'Air' }, ordinal: 0, isMandatory: true, isVisible: true, sheetGroup: null } }), 'аркуш');
  const ae = await ok<{ id: number }>(
    await request.put(`${base}/sheets/Air/tables/AE`, { data: { nameL10n: { en: 'Air emissions' }, ordinal: 0, layoutKind: 'MonthsInColumns', rowMode: 'Dynamic', maxDynamicRows: 10 } }),
    'таблиця AE',
  );
  for (const [index, name] of MonthNames.entries()) {
    await ok(
      await request.put(`${base}/tables/${String(ae.id)}/columns/M${pad(index + 1)}`, {
        data: column(index, name, 'Decimal', { precision: 18, scale: 2, isMonthColumn: true, monthNumber: index + 1 }),
      }),
      `місяць ${name}`,
    );
  }
  await ok(
    await request.put(`${base}/header-fields/PermitNo`, {
      data: { labelL10n: { en: 'Permit Number' }, ordinal: 0, dataType: 'Lookup', isRequired: false, lookupRegistryDefId: headerRegistryId, lookupFilter: null, unitId: null, defaultValue: null },
    }),
    'поле шапки PermitNo',
  );
  await ok(await request.post(`${base}/publish`, { data: { reason: 'E2E header split' } }), 'публікація шаблону');

  const policies = await ok<{ id: number; code: string }[]>(await request.get('/api/v1/projects/period-policies'), 'політики');
  const project = await ok<{ projectId: number }>(
    await request.post('/api/v1/projects', {
      data: {
        code: `E2EPH${stamp}`, nameL10n: { en: `E2E header split ${stamp}` }, periodKind: 'Monthly', timeZoneId: 'UTC',
        templateVersionId: version.versionId, periodPolicyId: (policies.find((p) => p.code === 'ECR-Standard') ?? policies[0])?.id, year,
      },
    }),
    'проєкт',
  );
  await ok(await request.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація проєкту');
  const structure = await ok<{ sheets: { id: number }[] }>(await request.get(`${base}/structure`), 'структура');
  const document = await ok<{ documentId: number }>(
    await request.post('/api/v1/documents', { data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((s) => s.id), templateVersionId: version.versionId } }),
    'документ',
  );
  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
    await request.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар',
  );
  const periodKey = Number(PeriodKey) || (calendar.periods.find((p) => p.state === 'Open')?.periodKey ?? 0);
  expect(periodKey, 'у проєкту немає відкритого періоду').toBeGreaterThan(0);
  const month = periodKey % 100;

  const createPermit = async (number: string, from: string, to: string): Promise<void> => {
    const permit = await ok<{ id: number }>(
      await request.post('/api/v1/permits', {
        data: { typeCode, number, location: 'E2E site', issueDate: from, validFrom: from, validTo: to, reportingFlags: { form300: false, form301: false, form302: false, tox: false } },
      }),
      `дозвіл ${number}`,
    );
    await ok(await request.post(`/api/v1/permits/${String(permit.id)}/activate`), `активація ${number}`);
  };
  const aNumber = `E2E-PH-A-${stamp}`;
  const bNumber = `E2E-PH-B-${stamp}`;
  await createPermit(aNumber, `${String(year)}-01-01`, `${String(year + 1)}-01-01`);
  await createPermit(bNumber, iso(new Date(Date.UTC(year, month - 1, 15))), `${String(year + 1)}-01-01`);
  const entries = await ok<{ id: number; display: string }[]>(
    await request.get(`/api/v1/registries/PRM_${typeCode}/entries?documentId=${String(document.documentId)}`),
    'записи дозволів',
  );
  const aId = entries.find((entry) => entry.display === aNumber)?.id ?? 0;

  // Header permit = A.
  const header = await ok<{ version: string }>(await request.get(`/api/v1/documents/${String(document.documentId)}/header`), 'шапка');
  await ok(
    await request.patch(`/api/v1/documents/${String(document.documentId)}/header`, {
      data: { fields: [{ code: 'PermitNo', isEmpty: false, value: aId }], baseVersion: header.version },
    }),
    'шапка = A',
  );

  const tables = await ok<{ tableInstanceId: number; tableCode: string }[]>(
    await request.get(`/api/v1/documents/${String(document.documentId)}/tables?periodKey=${String(periodKey)}`),
    'таблиці документа',
  );
  const instanceId = tables.find((t) => t.tableCode === 'AE')?.tableInstanceId ?? 0;
  for (let n = 0; n < 2; n += 1) {
    await ok(await request.post(`/api/v1/documents/${String(document.documentId)}/rows`, { data: { tableInstanceId: instanceId, rowKey: null } }), `рядок ${String(n)}`);
  }
  const slice = await ok<{ rows: { rowKey: string; rowVersion: string }[] }>(
    await request.get(`/api/v1/documents/${String(document.documentId)}/tables/${String(instanceId)}`),
    'зріз AE',
  );
  const monthCode = `M${pad(month)}`;
  await ok(
    await request.patch(`/api/v1/documents/${String(document.documentId)}/cells`, {
      data: {
        tableInstanceId: instanceId, periodKey, origin: 'UserEdit',
        rows: slice.rows.map((row, index) => ({
          rowKey: row.rowKey, baseVersion: row.rowVersion, cells: [{ columnCode: monthCode, value: index === 0 ? '26.7' : '7.3', isEmpty: false }],
        })),
      },
    }),
    'дані місяця',
  );

  return { documentId: document.documentId, periodKey, year, month, instanceId, rows: slice.rows.map((r) => r.rowKey), bNumber };
}

async function openDocument(page: Page, world: World): Promise<void> {
  await page.goto(`/documents/${String(world.documentId)}?periodKey=${String(world.periodKey)}`);
  await expect(page.locator('revo-grid').first()).toBeVisible({ timeout: 60_000 });
}

/** One screenshot per script line, light 1335 (the context viewport), into the test's output folder (CI artifact). */
async function shot(page: Page, name: string): Promise<void> {
  await page.screenshot({ path: test.info().outputPath(`light-1335-${name}.png`) });
}

async function permitSplitFinding(request: APIRequestContext, world: World): Promise<boolean> {
  const validate = await ok<{ messages: { ruleCode: string; severity: string }[] }>(
    await request.post(`/api/v1/documents/${String(world.documentId)}/validate`, { data: { periodKey: world.periodKey } }),
    'validate',
  );
  return validate.messages.some((m) => m.ruleCode === 'PERMIT-SPLIT' && m.severity === 'Error');
}

test.describe('Permit header:', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');
  test.describe.configure({ mode: 'serial', timeout: 600_000 });
  let context: BrowserContext;
  let world: World;

  test.beforeAll(async ({ browser }) => {
    test.setTimeout(600_000);
    context = await browser.newContext({ viewport: { width: 1335, height: 900 } });
    const login = await context.request.post('/api/v1/login/local', { data: { userName: Admin.user, password: Admin.password } });
    expect(login.ok(), `вхід: ${String(login.status())} ${await login.text()}`).toBe(true);
    world = await arrange(context.request);
  });

  test.afterAll(async () => {
    await context.close();
  });

  test('jh1: the header permit gets a second permit from the 15th — one field with its history, no new document', async () => {
    const page = await context.newPage();
    await openDocument(page, world);
    const toggle = page.getByTestId('document-header-toggle');
    if ((await toggle.getAttribute('aria-expanded')) !== 'true') await toggle.click();
    const section = page.locator('[data-header-permit-history="PermitNo"]');
    await section.getByRole('button', { name: /Add permit from date/ }).click();
    const form = page.getByRole('form', { name: 'New permit for the document header' });
    await form.getByLabel('Permit').click();
    await form.getByLabel('Permit').fill(world.bNumber);
    await page.getByRole('option', { name: new RegExp(`^${world.bNumber}`) }).click();
    // App-wide date input (StrictDateInput, A1-02): in English only `yyyy-MM-dd`, committed on blur.
    const date = form.getByLabel('Valid for this document from');
    await date.click();
    await date.press('ControlOrMeta+A');
    await date.pressSequentially(`${String(world.year)}-${pad(world.month)}-15`);
    await date.press('Tab');
    await expect(form.locator('[data-add-keeps]')).toContainText(`from 15.${pad(world.month)}.${String(world.year)}`);
    await shot(page, 'jh1');
    await form.getByRole('button', { name: 'Add permit' }).click();

    await expect(section.locator('[data-header-permit-item]')).toHaveCount(2, { timeout: 30_000 });
    await expect(section).toContainText(world.bNumber);
  });

  test('jh2: the relying rows need a split — banner with the count and the table; Submit blocked (PERMIT-SPLIT, 4225)', async () => {
    const page = await context.newPage();
    const slice = await ok<{ rows: { rowKey: string; rowVersion: string }[] }>(
      await page.request.get(`/api/v1/documents/${String(world.documentId)}/tables/${String(world.instanceId)}`),
      'зріз',
    );
    const refused = await page.request.patch(`/api/v1/documents/${String(world.documentId)}/cells`, {
      data: {
        tableInstanceId: world.instanceId, periodKey: world.periodKey, origin: 'UserEdit',
        rows: [{ rowKey: world.rows[1], baseVersion: slice.rows.find((row) => row.rowKey === world.rows[1])?.rowVersion, cells: [{ columnCode: `M${pad(world.month)}`, value: '8', isEmpty: false }] }],
      },
    });
    expect(refused.status()).toBe(422);
    expect(((await refused.json()) as { errorCode?: string }).errorCode).toBe('ECR-CELL-4225');
    expect(await permitSplitFinding(page.request, world), 'PERMIT-SPLIT').toBe(true);

    await openDocument(page, world);
    const banner = page.locator('[data-header-split-banner]');
    await expect(banner).toBeVisible({ timeout: 30_000 });
    await expect(banner).toContainText('2 rows');
    // Compact banner (F13 p223): ≤ 3 rows are listed without «Show all».
    await expect(banner.locator('[data-header-split-rows] li')).toHaveCount(2);
    await shot(page, 'jh2');
  });

  test('jh3: «Split all…» — rows × A/B, «was X»; partial save keeps Submit blocked; all split clears it', async () => {
    const page = await context.newPage();
    await openDocument(page, world);
    await page.locator('[data-header-split-banner]').getByRole('button', { name: 'Split all…' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible({ timeout: 30_000 });
    await expect(dialog.locator('[data-header-split-line]')).toHaveCount(2);
    await expect(dialog).toContainText('was 26.7');
    const inputs = dialog.getByRole('textbox');
    await inputs.nth(0).fill('12.6');
    await inputs.nth(1).fill('14.1');
    await expect(dialog.locator('[data-header-split-progress]')).toContainText('1 of 2 split');
    await shot(page, 'jh3');
    await dialog.getByRole('button', { name: 'Save parts' }).click();
    await expect(dialog).toBeHidden({ timeout: 30_000 });
    expect(await permitSplitFinding(page.request, world), 'PERMIT-SPLIT лишається (1 з 2)').toBe(true);

    await page.locator('[data-header-split-banner]').getByRole('button', { name: 'Split all…' }).click();
    const again = page.getByRole('dialog');
    const rest = again.getByRole('textbox');
    // «Split all» lists the rows still to split (F13 p223): only the second row now.
    await expect(again.locator('[data-header-split-line]')).toHaveCount(1);
    await rest.nth(0).fill('3.4');
    await rest.nth(1).fill('3.9');
    await again.getByRole('button', { name: 'Save parts' }).click();
    await expect(again).toBeHidden({ timeout: 30_000 });
    await expect(page.locator('[data-header-split-banner]')).toBeHidden({ timeout: 30_000 });
    expect(await permitSplitFinding(page.request, world), 'PERMIT-SPLIT знято').toBe(false);
  });
});
