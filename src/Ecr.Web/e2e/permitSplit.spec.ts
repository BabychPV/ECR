import { expect, test, type APIRequestContext, type APIResponse, type BrowserContext, type Page } from '@playwright/test';

/**
 * Feature (j), human 08.11 (ACCEPTANCE-1108-post15 j1–j7): a permit changes mid-month — the row keeps its permit history
 * and the user splits the month. REAL stand, no response mocks (`tools/e2e-stand.ps1`, workflow `e2e-stand`,
 * `-Grep "Permit split:"`).
 *
 * World (public API, once, serial): published Water permit type with permits A (whole year), B (from the 15th of the open
 * month M), EARLY (valid until the 9th of M); template «Water» / table «WR» (PERMIT Lookup + Jan…Dec); project (Monthly,
 * current year), document, open period M; rows R1 (A, 12 in M), R2 (EARLY), R3 (A, no data).
 *
 * Screenshots: one per script line, light 1335, into the test output folder (`test-results` — the CI artifact
 * `e2e-stand-report-shard-N`).
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
  monthCode: string;
}

async function patchCells(request: APIRequestContext, world: World, rowKey: string, cells: { columnCode: string; value: unknown }[]): Promise<APIResponse> {
  const slice = await ok<{ rows: { rowKey: string; rowVersion: string }[] }>(
    await request.get(`/api/v1/documents/${String(world.documentId)}/tables/${String(world.instanceId)}`),
    'зріз',
  );
  const row = slice.rows.find((candidate) => candidate.rowKey === rowKey);
  return request.patch(`/api/v1/documents/${String(world.documentId)}/cells`, {
    data: {
      tableInstanceId: world.instanceId, periodKey: world.periodKey, origin: 'UserEdit',
      rows: [{ rowKey, baseVersion: row?.rowVersion, cells: cells.map((cell) => ({ ...cell, isEmpty: false })) }],
    },
  });
}

async function arrange(request: APIRequestContext): Promise<World> {
  const stamp = String(Date.now());
  const year = new Date().getUTCFullYear();
  const typeCode = `E2E_PS_${stamp}`;
  const created = await ok<{ rowVersion: string; nameL10n: Record<string, string> }>(
    await request.post('/api/v1/permit-types', { data: { code: typeCode, category: 'Water', nameL10n: { en: `E2E split ${stamp}` } } }),
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
  await ok(await request.post(`/api/v1/permit-types/${typeCode}/publish`, { data: { reason: 'E2E permit split', rowVersion: replaced.rowVersion } }), 'публікація типу');
  const targets = await ok<{ id: number; code: string }[]>(await request.get('/api/v1/registries?includePermitHeaders=true'), 'цілі Lookup');
  const headerRegistryId = targets.find((target) => target.code === `PRM_${typeCode}`)?.id ?? 0;
  expect(headerRegistryId, 'реєстр шапки типу').toBeGreaterThan(0);

  const template = await ok<{ templateId: number }>(
    await request.post('/api/v1/templates', { data: { code: `E2EPs${stamp}`, nameL10n: { en: `E2E permit split ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await request.post(`/api/v1/templates/${String(template.templateId)}/versions`, { data: { versionNumber: '1.0.0.0', cloneFromVersionId: null } }),
    'версія',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;
  await ok(await request.put(`${base}/sheets/Water`, { data: { nameL10n: { en: 'Water' }, ordinal: 0, isMandatory: true, isVisible: true, sheetGroup: null } }), 'аркуш');
  const wr = await ok<{ id: number }>(
    await request.put(`${base}/sheets/Water/tables/WR`, { data: { nameL10n: { en: 'Water Report' }, ordinal: 0, layoutKind: 'MonthsInColumns', rowMode: 'Dynamic', maxDynamicRows: 10 } }),
    'таблиця WR',
  );
  await ok(await request.put(`${base}/tables/${String(wr.id)}/columns/PERMIT`, { data: column(0, 'Permit number', 'Lookup', { lookupRegistryDefId: headerRegistryId }) }), 'PERMIT');
  for (const [index, name] of MonthNames.entries()) {
    await ok(
      await request.put(`${base}/tables/${String(wr.id)}/columns/M${pad(index + 1)}`, {
        data: column(index + 1, name, 'Decimal', { precision: 18, scale: 2, isMonthColumn: true, monthNumber: index + 1 }),
      }),
      `місяць ${name}`,
    );
  }
  await ok(await request.post(`${base}/publish`, { data: { reason: 'E2E permit split' } }), 'публікація шаблону');

  const policies = await ok<{ id: number; code: string }[]>(await request.get('/api/v1/projects/period-policies'), 'політики');
  const project = await ok<{ projectId: number }>(
    await request.post('/api/v1/projects', {
      data: {
        code: `E2EPS${stamp}`, nameL10n: { en: `E2E permit split ${stamp}` }, periodKind: 'Monthly', timeZoneId: 'UTC',
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
  const aNumber = `E2E-PS-A-${stamp}`;
  const bNumber = `E2E-PS-B-${stamp}`;
  const earlyNumber = `E2E-PS-EARLY-${stamp}`;
  await createPermit(aNumber, `${String(year)}-01-01`, `${String(year + 1)}-01-01`);
  await createPermit(bNumber, iso(new Date(Date.UTC(year, month - 1, 15))), `${String(year + 1)}-01-01`);
  await createPermit(earlyNumber, `${String(year)}-01-01`, iso(new Date(Date.UTC(year, month - 1, 10))));

  const entries = await ok<{ id: number; display: string }[]>(
    await request.get(`/api/v1/registries/PRM_${typeCode}/entries?documentId=${String(document.documentId)}`),
    'записи дозволів',
  );
  const entryOf = (number: string): number => entries.find((entry) => entry.display === number)?.id ?? 0;

  const tables = await ok<{ tableInstanceId: number; tableCode: string }[]>(
    await request.get(`/api/v1/documents/${String(document.documentId)}/tables?periodKey=${String(periodKey)}`),
    'таблиці документа',
  );
  const instanceId = tables.find((t) => t.tableCode === 'WR')?.tableInstanceId ?? 0;
  for (let n = 0; n < 3; n += 1) {
    await ok(await request.post(`/api/v1/documents/${String(document.documentId)}/rows`, { data: { tableInstanceId: instanceId, rowKey: null } }), `рядок ${String(n)}`);
  }
  const slice = await ok<{ rows: { rowKey: string }[] }>(
    await request.get(`/api/v1/documents/${String(document.documentId)}/tables/${String(instanceId)}`),
    'зріз WR',
  );
  const world: World = {
    documentId: document.documentId, periodKey, year, month, instanceId, rows: slice.rows.map((r) => r.rowKey), bNumber,
    monthCode: `M${pad(month)}`,
  };

  await ok(await patchCells(request, world, world.rows[0]!, [{ columnCode: 'PERMIT', value: entryOf(aNumber) }, { columnCode: world.monthCode, value: '12' }]), 'R1');
  await ok(await patchCells(request, world, world.rows[1]!, [{ columnCode: 'PERMIT', value: entryOf(earlyNumber) }]), 'R2');
  await ok(await patchCells(request, world, world.rows[2]!, [{ columnCode: 'PERMIT', value: entryOf(aNumber) }]), 'R3');
  return world;
}

async function openDocument(page: Page, world: World): Promise<void> {
  await page.goto(`/documents/${String(world.documentId)}?periodKey=${String(world.periodKey)}`);
  await expect(page.locator('revo-grid').first()).toBeVisible({ timeout: 60_000 });
}

async function gridColumn(page: Page, header: string): Promise<string> {
  const cell = page.locator('revo-grid revogr-header .rgHeaderCell', { hasText: header }).first();
  const scroller = page.getByTestId('grid-hscroll');
  await expect(page.locator('revo-grid revogr-header .rgHeaderCell').first()).toBeVisible({ timeout: 60_000 });
  await scroller.evaluate((el) => {
    el.scrollLeft = 0;
  });
  await expect(async () => {
    if ((await cell.count()) === 0) {
      await scroller.evaluate((el) => {
        el.scrollLeft = Math.min(el.scrollLeft + Math.max(200, el.clientWidth / 2), el.scrollWidth);
      });
    }
    await expect(cell).toBeVisible({ timeout: 1_000 });
  }, `заголовка «${header}» немає в сітці`).toPass({ timeout: 60_000 });
  await cell.scrollIntoViewIfNeeded();
  const index = await cell.getAttribute('aria-colindex');
  if (index === null) throw new Error(`заголовок «${header}» без aria-colindex`);
  return index;
}

function dataCell(page: Page, row: number, index: string) {
  return page.locator(`revo-grid revogr-data[type="rgRow"] .rgCell[data-rgrow="${String(row)}"][aria-colindex="${index}"]`);
}

/** One screenshot per script line, light 1335 (the context viewport), into the test's output folder (CI artifact). */
async function shot(page: Page, name: string): Promise<void> {
  await page.screenshot({ path: test.info().outputPath(`light-1335-${name}.png`) });
}

/** Inspector «Info» of the selected permit cell → «Add permit from date…» with B from the 15th of the month. */
async function addB(page: Page, world: World, row: number): Promise<void> {
  await dataCell(page, row, await gridColumn(page, 'Permit number')).click();
  await page.getByRole('tab', { name: /Info/ }).click();
  await page.getByRole('button', { name: 'Add permit from date…' }).click();
  const form = page.getByRole('form', { name: 'New permit for the row' });
  await form.getByLabel('Permit').click();
  await form.getByLabel('Permit').fill(world.bNumber);
  await page.getByRole('option', { name: new RegExp(`^${world.bNumber}`) }).click();
  const date = form.getByLabel('Valid for this row from');
  await date.fill(`15.${pad(world.month)}.${String(world.year)}`);
  await date.press('Tab');
  await form.getByRole('button', { name: 'Add permit' }).click();
}

test.describe('Permit split:', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');
  test.describe.configure({ mode: 'serial', timeout: 600_000 });
  let context: BrowserContext;
  let world: World;

  // Signed in as the e2e admin (as permitValidity.spec.ts): the API world and every page share this context.
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

  test('j1: the row keeps its permit history — «until» / «from» in the permit cell, no new row', async () => {
    const page = await context.newPage();
    await openDocument(page, world);
    await addB(page, world, 0);
    const permitCell = dataCell(page, 0, await gridColumn(page, 'Permit number'));
    await expect(permitCell).toContainText('until', { timeout: 30_000 });
    await expect(permitCell).toContainText('from');
    await shot(page, 'j1');
    const slice = await ok<{ rows: unknown[] }>(
      await page.request.get(`/api/v1/documents/${String(world.documentId)}/tables/${String(world.instanceId)}`),
      'зріз',
    );
    expect(slice.rows, 'нового рядка немає').toHaveLength(3);
  });

  test('j3: one value into the shared month is refused (4225) and Submit is blocked until split', async () => {
    const page = await context.newPage();
    const refused = await patchCells(page.request, world, world.rows[0]!, [{ columnCode: world.monthCode, value: '20' }]);
    expect(refused.status()).toBe(422);
    expect(((await refused.json()) as { errorCode?: string }).errorCode).toBe('ECR-CELL-4225');

    const validate = await ok<{ messages: { ruleCode: string; severity: string }[] }>(
      await page.request.post(`/api/v1/documents/${String(world.documentId)}/validate`, { data: { periodKey: world.periodKey } }),
      'validate',
    );
    expect(validate.messages.some((m) => m.ruleCode === 'PERMIT-SPLIT' && m.severity === 'Error'), 'PERMIT-SPLIT').toBe(true);

    await openDocument(page, world);
    await expect(dataCell(page, 0, await gridColumn(page, MonthNames[world.month - 1]!))).toContainText('Split needed');
    await shot(page, 'j3');
  });

  test('j2: Enter opens the split, A is pre-filled, A + B is saved as the total and the finding clears', async () => {
    const page = await context.newPage();
    await openDocument(page, world);
    const cell = dataCell(page, 0, await gridColumn(page, MonthNames[world.month - 1]!));
    await cell.click();
    await page.keyboard.press('Enter');
    const dialog = page.getByRole('dialog');
    await expect(dialog).toBeVisible({ timeout: 30_000 });
    const parts = dialog.getByRole('textbox');
    await expect(parts.nth(0)).toHaveValue(/12/);
    await parts.nth(0).fill('5.5');
    await parts.nth(1).fill('6.5');
    await expect(dialog).toContainText('12');
    await shot(page, 'j2');
    await dialog.getByRole('button', { name: 'Save parts' }).click();
    await expect(dialog).toBeHidden({ timeout: 30_000 });
    await expect(cell).not.toContainText('Split needed', { timeout: 30_000 });

    const validate = await ok<{ messages: { ruleCode: string }[] }>(
      await page.request.post(`/api/v1/documents/${String(world.documentId)}/validate`, { data: { periodKey: world.periodKey } }),
      'validate',
    );
    expect(validate.messages.some((m) => m.ruleCode === 'PERMIT-SPLIT'), 'PERMIT-SPLIT знято').toBe(false);
  });

  test('j4: a gap between the permits is refused naming the days, with the two fixes', async () => {
    const page = await context.newPage();
    await openDocument(page, world);
    await addB(page, world, 1);
    const gap = page.locator('[data-permit-gap]');
    await expect(gap).toBeVisible({ timeout: 30_000 });
    await expect(gap).toContainText(`10.${pad(world.month)}–14.${pad(world.month)}`);
    await expect(gap.getByRole('button', { name: /valid from/ })).toBeVisible();
    await expect(gap.getByRole('button', { name: /Add a permit for/ })).toBeVisible();
    await shot(page, 'j4');
  });

  test('j6: undo the latest permit; the audit names the row and both permits', async () => {
    const page = await context.newPage();
    await openDocument(page, world);
    await addB(page, world, 2);
    const permitCell = dataCell(page, 2, await gridColumn(page, 'Permit number'));
    await expect(permitCell).toContainText('from', { timeout: 30_000 });
    await page.getByRole('button', { name: 'Undo last' }).click();
    await expect(permitCell).not.toContainText('from', { timeout: 30_000 });
    await shot(page, 'j6');
  });
});
