import { expect, test, type APIRequestContext, type APIResponse, type BrowserContext, type Page, type Route } from '@playwright/test';

/**
 * Rule (h), human 08.11 (ACCEPTANCE-1108-post15 h1–h4): a permit must cover the months that hold data.
 * REAL stand, no response mocks (stand: `tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep "Permit validity:"`).
 *
 * World, built through the public API once (serial describe):
 *   published Water permit type with two active permits — YEAR (all 2026) and OTHER (2026 except the open month M);
 *   template: sheet «Water» with table «WR» (months in columns: PERMIT Lookup → the type's header, Jan…Dec) and
 *   table «AIR» (no permit column, Q decimal); header field «Permit» → the type's header;
 *   project (Monthly, current year), document, the open period M; rows R1/R2/R3 in WR, one row in AIR.
 *
 * ⚠ The grid/header pickers offer only permits valid at the period end / in the window, so a refusing permit cannot
 * be picked by hand: the UI picks YEAR, and the PATCH body is re-pointed to OTHER on the wire (the same technique as
 * `permitGroupsReal.spec.ts`). What is verified is the server refusal AND that the client shows its explanation.
 *
 * ⚠ Data is one-shot (a published type cannot be deleted); codes carry a timestamp.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const MonthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'] as const;
const FixText = /Pick a permit valid in those months/;

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

interface World {
  documentId: number;
  periodKey: number;
  month: number;
  wrInstanceId: number;
  airInstanceId: number;
  rows: string[];
  yearId: number;
  otherId: number;
  yearPermitId: number;
  yearNumber: string;
  otherNumber: string;
  midId: number;
  midNumber: string;
}

async function patchCells(
  request: APIRequestContext, world: World, instanceId: number, rowKey: string, cells: { columnCode: string; value: unknown }[],
): Promise<APIResponse> {
  const slice = await ok<{ rows: { rowKey: string; rowVersion: string }[] }>(
    await request.get(`/api/v1/documents/${String(world.documentId)}/tables/${String(instanceId)}`),
    'зріз',
  );
  const row = slice.rows.find((candidate) => candidate.rowKey === rowKey);

  return request.patch(`/api/v1/documents/${String(world.documentId)}/cells`, {
    data: {
      tableInstanceId: instanceId, periodKey: world.periodKey, origin: 'UserEdit',
      rows: [{ rowKey, baseVersion: row?.rowVersion, cells: cells.map((cell) => ({ ...cell, isEmpty: false })) }],
    },
  });
}

async function arrange(request: APIRequestContext): Promise<World> {
  const stamp = String(Date.now());
  const year = new Date().getUTCFullYear();

  // Template first: the open period (month M) decides the permits' windows.
  const typeCode = `E2E_PV_${stamp}`;
  const created = await ok<{ rowVersion: string; nameL10n: Record<string, string> }>(
    await request.post('/api/v1/permit-types', { data: { code: typeCode, category: 'Water', nameL10n: { en: `E2E validity ${stamp}` } } }),
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
  await ok(await request.post(`/api/v1/permit-types/${typeCode}/publish`, { data: { reason: 'E2E permit validity', rowVersion: replaced.rowVersion } }), 'публікація типу');
  const targets = await ok<{ id: number; code: string }[]>(await request.get('/api/v1/registries?includePermitHeaders=true'), 'цілі Lookup');
  const headerRegistryId = targets.find((target) => target.code === `PRM_${typeCode}`)?.id ?? 0;
  expect(headerRegistryId, 'реєстр шапки типу').toBeGreaterThan(0);

  const template = await ok<{ templateId: number }>(
    await request.post('/api/v1/templates', { data: { code: `E2EPv${stamp}`, nameL10n: { en: `E2E permit validity ${stamp}` } } }),
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
      await request.put(`${base}/tables/${String(wr.id)}/columns/M${String(index + 1).padStart(2, '0')}`, {
        data: column(index + 1, name, 'Decimal', { precision: 18, scale: 2, isMonthColumn: true, monthNumber: index + 1 }),
      }),
      `місяць ${name}`,
    );
  }
  const air = await ok<{ id: number }>(
    await request.put(`${base}/sheets/Water/tables/AIR`, { data: { nameL10n: { en: 'Air data' }, ordinal: 1, layoutKind: 'PerPeriodInstance', rowMode: 'Dynamic', maxDynamicRows: 10 } }),
    'таблиця AIR',
  );
  await ok(await request.put(`${base}/tables/${String(air.id)}/columns/Q`, { data: column(0, 'Quantity', 'Decimal', { precision: 18, scale: 2 }) }), 'Q');
  await ok(
    await request.put(`${base}/header-fields/Permit`, {
      data: { labelL10n: { en: 'Permit' }, ordinal: 0, dataType: 'Lookup', isRequired: false, lookupRegistryDefId: headerRegistryId, lookupFilter: null, unitId: null, defaultValue: null },
    }),
    'поле шапки Permit',
  );
  await ok(await request.post(`${base}/publish`, { data: { reason: 'E2E permit validity' } }), 'публікація шаблону');

  const policies = await ok<{ id: number; code: string }[]>(await request.get('/api/v1/projects/period-policies'), 'політики');
  const project = await ok<{ projectId: number }>(
    await request.post('/api/v1/projects', {
      data: {
        code: `E2EPV${stamp}`, nameL10n: { en: `E2E permit validity ${stamp}` }, periodKind: 'Monthly', timeZoneId: 'UTC',
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
  const periodKey = calendar.periods.find((p) => p.state === 'Open')?.periodKey ?? 0;
  expect(periodKey, 'у проєкту немає відкритого періоду').toBeGreaterThan(0);
  const month = periodKey % 100;

  // Permits: YEAR covers the year; OTHER covers the year except month M (so it still touches the year).
  const createPermit = async (number: string, from: string, to: string): Promise<number> => {
    const permit = await ok<{ id: number }>(
      await request.post('/api/v1/permits', {
        data: { typeCode, number, location: 'E2E site', issueDate: from, validFrom: from, validTo: to, reportingFlags: { form300: false, form301: false, form302: false, tox: false } },
      }),
      `дозвіл ${number}`,
    );
    await ok(await request.post(`/api/v1/permits/${String(permit.id)}/activate`), `активація ${number}`);
    return permit.id;
  };
  const yearNumber = `E2E-PV-YEAR-${stamp}`;
  const otherNumber = `E2E-PV-OTHER-${stamp}`;
  const yearPermitId = await createPermit(yearNumber, `${String(year)}-01-01`, `${String(year + 1)}-01-01`);
  const otherFrom = month < 12 ? iso(new Date(Date.UTC(year, month, 1))) : `${String(year)}-01-01`;
  const otherTo = month < 12 ? `${String(year + 1)}-01-01` : `${String(year)}-12-01`;
  await createPermit(otherNumber, otherFrom, otherTo);
  // MID starts on the 15th of month M (human 08.11: a mid-month start covers the month, the days are named).
  const midNumber = `E2E-PV-MID-${stamp}`;
  await createPermit(midNumber, iso(new Date(Date.UTC(year, month - 1, 15))), `${String(year + 1)}-01-01`);

  const entries = await ok<{ id: number; display: string }[]>(
    await request.get(`/api/v1/registries/PRM_${typeCode}/entries?documentId=${String(document.documentId)}`),
    'записи дозволів',
  );
  const entryOf = (number: string): number => entries.find((entry) => entry.display === number)?.id ?? 0;

  const tables = await ok<{ tableInstanceId: number; tableCode: string }[]>(
    await request.get(`/api/v1/documents/${String(document.documentId)}/tables?periodKey=${String(periodKey)}`),
    'таблиці документа',
  );
  const wrInstanceId = tables.find((t) => t.tableCode === 'WR')?.tableInstanceId ?? 0;
  const airInstanceId = tables.find((t) => t.tableCode === 'AIR')?.tableInstanceId ?? 0;
  for (let n = 0; n < 4; n += 1) {
    await ok(await request.post(`/api/v1/documents/${String(document.documentId)}/rows`, { data: { tableInstanceId: wrInstanceId, rowKey: null } }), `рядок WR ${String(n)}`);
  }
  await ok(await request.post(`/api/v1/documents/${String(document.documentId)}/rows`, { data: { tableInstanceId: airInstanceId, rowKey: null } }), 'рядок AIR');
  const slice = await ok<{ rows: { rowKey: string }[] }>(
    await request.get(`/api/v1/documents/${String(document.documentId)}/tables/${String(wrInstanceId)}`),
    'зріз WR',
  );

  const world: World = {
    documentId: document.documentId, periodKey, month, wrInstanceId, airInstanceId, rows: slice.rows.map((r) => r.rowKey),
    yearId: entryOf(yearNumber), otherId: entryOf(otherNumber), yearPermitId, yearNumber, otherNumber,
    midId: entryOf(midNumber), midNumber,
  };
  expect(world.midId, 'запис MID').toBeGreaterThan(0);
  expect(world.yearId, 'запис YEAR').toBeGreaterThan(0);
  expect(world.otherId, 'запис OTHER').toBeGreaterThan(0);

  // Data in month M: R1 (h2) and AIR (h1). R2 (h3) gets OTHER while empty (it touches the year — accepted).
  const monthCode = `M${String(month).padStart(2, '0')}`;
  await ok(await patchCells(request, world, wrInstanceId, world.rows[0]!, [{ columnCode: monthCode, value: '5' }]), 'R1: дані місяця');
  const airSlice = await ok<{ rows: { rowKey: string }[] }>(
    await request.get(`/api/v1/documents/${String(document.documentId)}/tables/${String(airInstanceId)}`),
    'зріз AIR',
  );
  await ok(await patchCells(request, world, airInstanceId, airSlice.rows[0]!.rowKey, [{ columnCode: 'Q', value: '9' }]), 'AIR: дані');
  await ok(await patchCells(request, world, wrInstanceId, world.rows[1]!, [{ columnCode: 'PERMIT', value: world.otherId }]), 'R2: дозвіл OTHER');
  await ok(await patchCells(request, world, wrInstanceId, world.rows[3]!, [{ columnCode: 'PERMIT', value: world.midId }]), 'R4: дозвіл MID');

  return world;
}

/** Replaces every JSON value equal to `from` (number or its string) with `to` — the entry id, never a digit inside a hash. */
function repoint(value: unknown, from: number, to: number): unknown {
  if (value === from) return to;
  if (value === String(from)) return String(to);
  if (Array.isArray(value)) return value.map((item) => repoint(item, from, to));
  if (value !== null && typeof value === 'object') {
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, repoint(item, from, to)]));
  }

  return value;
}

/**
 * Re-points the entry id in the NEXT PATCH to `url` from `from` to `to` (the picker cannot offer the refusing permit).
 * ⚠ The handler answers each request exactly once (continue or fallback) and is removed by the caller after the
 * response: unrouting inside the handler made Playwright report «Route is already handled» and the PATCH went out
 * unchanged (run 42500895032: 200 instead of 422).
 */
async function rewriteNextPatch(page: Page, url: string, from: number, to: number): Promise<() => Promise<void>> {
  let used = false;
  const handler = async (route: Route): Promise<void> => {
    if (used || route.request().method() !== 'PATCH') {
      await route.fallback();
      return;
    }

    used = true;
    const body = JSON.parse(route.request().postData() ?? '{}') as unknown;
    const rewritten = JSON.stringify(repoint(body, from, to));
    expect(rewritten, `тіло PATCH не містить id ${String(from)}`).not.toBe(JSON.stringify(body));
    await route.continue({ postData: rewritten });
  };
  await page.route(url, handler);

  return () => page.unroute(url, handler);
}

async function openDocument(page: Page, world: World): Promise<void> {
  await page.goto(`/documents/${String(world.documentId)}?periodKey=${String(world.periodKey)}`);
  await expect(page.locator('revo-grid').first()).toBeVisible({ timeout: 60_000 });
}

async function gridColumn(page: Page, header: string): Promise<string> {
  const cell = page.locator('revo-grid revogr-header .rgHeaderCell', { hasText: header }).first();
  await expect(cell, `заголовка «${header}» немає в сітці`).toBeVisible({ timeout: 60_000 });
  const index = await cell.getAttribute('aria-colindex');
  if (index === null) throw new Error(`заголовок «${header}» без aria-colindex`);

  return index;
}

function dataCell(page: Page, row: number, index: string) {
  return page.locator(`revo-grid revogr-data[type="rgRow"] .rgCell[data-rgrow="${String(row)}"][aria-colindex="${index}"]`);
}

test.describe('Permit validity:', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');
  test.describe.configure({ mode: 'serial', timeout: 600_000 });

  let context: BrowserContext;
  let world: World;

  test.beforeAll(async ({ browser }) => {
    test.setTimeout(600_000);
    context = await browser.newContext({ permissions: ['clipboard-read', 'clipboard-write'], viewport: { width: 1440, height: 900 } });
    const login = await context.request.post('/api/v1/login/local', { data: { userName: Admin.user, password: Admin.password } });
    expect(login.ok(), `вхід: ${String(login.status())} ${await login.text()}`).toBe(true);
    world = await arrange(context.request);
  });

  test.afterAll(async () => {
    await context.close();
  });

  test('Permit validity: h1 Contract tab — a header permit that misses a month with data is refused with the explanation', async () => {
    const page = await context.newPage();
    await openDocument(page, world);
    const toggle = page.getByTestId('document-header-toggle');
    if ((await toggle.getAttribute('aria-expanded')) !== 'true') await toggle.click();
    const field = page.getByRole('textbox', { name: 'Permit' });
    await expect(field).toBeEnabled({ timeout: 30_000 });
    await field.click();
    await page.getByRole('option', { name: new RegExp(`^${world.yearNumber}`) }).click();

    const unroute = await rewriteNextPatch(page, `**/api/v1/documents/${String(world.documentId)}/header`, world.yearId, world.otherId);
    const saved = page.waitForResponse((r) => r.request().method() === 'PATCH' && r.url().includes(`/documents/${String(world.documentId)}/header`));
    await page.getByTestId('document-header-panel').getByRole('button', { name: /^(Save|Зберегти|Сохранить|Сақтау)$/ }).click();
    const response = await saved;
    await unroute();
    expect(response.status(), await response.text()).toBe(422);
    expect(((await response.json()) as { messageKey?: string }).messageKey).toBe('err.ECR-HDR-4223.permitNotCoveringMonths');

    // Shown under the Permit field: permit, its validity, the month, the way out.
    const panel = page.getByTestId('document-header-panel');
    await expect(panel.getByText(FixText).first()).toBeVisible({ timeout: 15_000 });
    await expect(panel.getByText(new RegExp(world.otherNumber)).first()).toBeVisible();
    await expect(panel.getByText(new RegExp(`${MonthNames[world.month - 1]!} ${String(Math.floor(world.periodKey / 100))}`)).first()).toBeVisible();
    await page.close();
  });

  test('Permit validity: h2 Water tab — changing a row permit to one that misses its data month is refused', async () => {
    const page = await context.newPage();
    await openDocument(page, world);
    const permitCell = dataCell(page, 0, await gridColumn(page, 'Permit number'));
    await expect(async () => {
      await permitCell.dblclick();
      await expect(page.getByRole('option', { name: new RegExp(`^${world.yearNumber}`) }).first()).toBeVisible({ timeout: 3_000 });
    }).toPass({ timeout: 30_000 });

    const unroute = await rewriteNextPatch(page, `**/api/v1/documents/${String(world.documentId)}/cells`, world.yearId, world.otherId);
    const saved = page.waitForResponse((r) => r.request().method() === 'PATCH' && new URL(r.url()).pathname.endsWith('/cells'), { timeout: 90_000 });
    await page.getByRole('option', { name: new RegExp(`^${world.yearNumber}`) }).first().click();
    await page.keyboard.press('Control+s');
    const response = await saved;
    await unroute();
    expect(response.status(), await response.text()).toBe(422);
    expect(((await response.json()) as { messageKey?: string }).messageKey).toBe('err.ECR-CELL-4223.permitNotCoveringMonths');

    await expect(page.getByText(FixText).first(), 'сітка не показала пояснення відмови').toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(new RegExp(world.otherNumber)).first()).toBeVisible();
    await page.close();
  });

  test('Permit validity: h3 a value in a month outside the row permit is refused and explained', async () => {
    const page = await context.newPage();
    await openDocument(page, world);
    const monthCell = dataCell(page, 1, await gridColumn(page, MonthNames[world.month - 1]!));
    await monthCell.click();
    await page.evaluate(async () => {
      await navigator.clipboard.writeText('7');
    });
    const saved = page.waitForResponse((r) => r.request().method() === 'PATCH' && new URL(r.url()).pathname.endsWith('/cells'), { timeout: 90_000 });
    await page.keyboard.press('Control+v');
    await page.keyboard.press('Control+s');
    const response = await saved;
    expect(response.status(), await response.text()).toBe(422);
    const problem = (await response.json()) as { messageKey?: string; cells?: { rowKey: string; columnCode: string }[] };
    expect(problem.messageKey).toBe('err.ECR-CELL-4223.permitNotCoveringMonths');
    expect(problem.cells?.[0]?.columnCode).toBe(`M${String(world.month).padStart(2, '0')}`);

    await expect(page.getByText(FixText).first(), 'сітка не показала пояснення відмови').toBeVisible({ timeout: 15_000 });
    await page.close();
  });

  test('Permit validity: h4 an existing violation is listed in Issues with the reason and the fix', async () => {
    const request = context.request;
    // R3: permit YEAR and data in month M (both valid), then the permit stops covering month M.
    const monthCode = `M${String(world.month).padStart(2, '0')}`;
    await ok(await patchCells(request, world, world.wrInstanceId, world.rows[2]!, [{ columnCode: 'PERMIT', value: world.yearId }, { columnCode: monthCode, value: '3' }]), 'R3');
    const year = Math.floor(world.periodKey / 100);
    const current = await ok<{ rowVersion: string }>(await request.get(`/api/v1/permits/${String(world.yearPermitId)}`), 'дозвіл YEAR');
    const shrunk = world.month < 12
      ? { validFrom: iso(new Date(Date.UTC(year, world.month, 1))), validTo: `${String(year + 1)}-01-01` }
      : { validFrom: `${String(year)}-01-01`, validTo: `${String(year)}-12-01` };
    await ok(
      await request.put(`/api/v1/permits/${String(world.yearPermitId)}`, {
        data: {
          rowVersion: current.rowVersion, number: world.yearNumber, location: 'E2E site', issueDate: `${String(year)}-01-01`, ...shrunk,
          actualEnd: null, reportingFlags: { form300: false, form301: false, form302: false, tox: false },
        },
      }),
      'звуження дозволу YEAR',
    );

    const validation = await ok<{ messages: { ruleCode: string; rowKey: string | null; columnCode: string | null; message: string; severity: string }[] }>(
      await request.post(`/api/v1/documents/${String(world.documentId)}/validate`, { data: { periodKey: world.periodKey } }),
      'Перевірити',
    );
    const finding = validation.messages.find((m) => m.ruleCode === 'PERMIT-VALIDITY' && m.rowKey === world.rows[2]);
    expect(finding, JSON.stringify(validation.messages)).toBeDefined();
    expect(finding?.severity).toBe('Error');
    expect(finding?.columnCode).toBe('PERMIT');
    expect(finding?.message).toMatch(/extend the permit's validity/);

    // The Issues tab of the document shows it with the reason and the fix.
    const page = await context.newPage();
    await openDocument(page, world);
    await page.getByRole('button', { name: /issue/i }).first().click();
    const inspector = page.getByRole('complementary');
    await expect(inspector.getByText(new RegExp(world.yearNumber)).first()).toBeVisible({ timeout: 30_000 });
    await expect(inspector.getByText(/extend the permit's validity/).first()).toBeVisible();
    await page.close();
  });

  test('Permit validity: partial month — a permit from the 15th accepts that month and Issues names the days', async () => {
    const page = await context.newPage();
    await openDocument(page, world);
    const monthCell = dataCell(page, 3, await gridColumn(page, MonthNames[world.month - 1]!));
    await monthCell.click();
    await page.evaluate(async () => {
      await navigator.clipboard.writeText('4');
    });
    const saved = page.waitForResponse((r) => r.request().method() === 'PATCH' && new URL(r.url()).pathname.endsWith('/cells'), { timeout: 90_000 });
    await page.keyboard.press('Control+v');
    await page.keyboard.press('Control+s');
    const response = await saved;
    expect(response.status(), `місяць із дозволом від 15-го: ${await response.text()}`).toBe(200);

    const year = Math.floor(world.periodKey / 100);
    const mm = String(world.month).padStart(2, '0');
    const lastDay = String(new Date(Date.UTC(year, world.month, 0)).getUTCDate()).padStart(2, '0');
    const days = `15.${mm}–${lastDay}.${mm}.${String(year)}`;
    const validation = await ok<{ messages: { ruleCode: string; rowKey: string | null; message: string; severity: string }[] }>(
      await context.request.post(`/api/v1/documents/${String(world.documentId)}/validate`, { data: { periodKey: world.periodKey } }),
      'Перевірити',
    );
    const warning = validation.messages.find((m) => m.ruleCode === 'PERMIT-VALIDITY' && m.rowKey === world.rows[3]);
    expect(warning, JSON.stringify(validation.messages)).toBeDefined();
    expect(warning?.severity, 'часткове покриття — попередження, не помилка').toBe('Warning');
    expect(warning?.message).toContain(days);

    await page.reload();
    await expect(page.locator('revo-grid').first()).toBeVisible({ timeout: 60_000 });
    await page.getByRole('button', { name: /issue/i }).first().click();
    await expect(page.getByRole('complementary').getByText(new RegExp(days)).first()).toBeVisible({ timeout: 30_000 });
    await page.close();
  });
});
