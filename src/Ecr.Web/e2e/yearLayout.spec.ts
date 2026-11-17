import { randomBytes } from 'node:crypto';
import { expect, test, type APIRequestContext, type APIResponse, type Browser, type BrowserContext } from '@playwright/test';

/**
 * ✎ LAND-YEAR L7a/L7b на РЕАЛЬНОМУ стенді (`tools/e2e-stand.ps1 -Grep yearLayout`): документ версії
 * `YearWithMonthColumns` — одна копія на рік, без перемикача місяця в будь-якому вигляді.
 *
 *  1. `?periodKey` жовтня веде на якір року (січень); у шапці «Year YYYY», вибору періоду й меню «Calculated: <місяць>»
 *     немає.
 *  2. Issues: перевірку запущено лише за ЖОВТЕНЬ (не за якір) — зауваження видно в документі «рік» з позначкою місяця
 *     (`GET validation?year=true`; доти Issues читав лише якір і казав «ще не перевіряли»).
 *  3. Журнал аркуша (History без комірки): правки вересня й жовтня, кожна з назвою свого місяця (`activity?year=true`).
 *  4. Allow/Deny: користувач із Deny на таблицю T2 (аркуш S2) не бачить ні зауважень T2 у річному Issues, ні журналу S2;
 *     адміністратор (Allow) — бачить. Та сама база, ті самі дані.
 *
 * ⚠ Місяці — 09/10 ПОПЕРЕДНЬОГО року, а не 2099: зсуви політики періодів обмежені ±3660 днями
 * (`PeriodPolicy.MaxOffsetDays`), тож місяці 2099 року на живому стенді ніколи не відкриті, а правка в невідкритий
 * місяць відхиляється. База стенда — одноразова (`-Database`), спільних рядків немає.
 * ⚠ Тестовий користувач і роль заводяться тут (`test-l7-deny-<stamp>`, пароль випадковий, ніде не друкується).
 * ⛔ Без стенда файл ПАДАЄ (вхід/API), а не пропускається (honesty-guard).
 */
test.describe.configure({ mode: 'serial', timeout: 600_000 });

const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const MonthHeader = (m: number): string => `Month ${String(m).padStart(2, '0')}`;

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

function column(ordinal: number, header: string, dataType: 'String' | 'Decimal', month: number | null, required = false): Record<string, unknown> {
  return {
    headerL10n: { en: header },
    ordinal,
    dataType,
    isRequired: required,
    isReadOnly: false,
    isHidden: false,
    precision: dataType === 'Decimal' ? 18 : null,
    scale: dataType === 'Decimal' ? 2 : null,
    defaultValue: null,
    displayFormat: null,
    lookupFilter: null,
    lookupRegistryDefId: null,
    styleId: null,
    unitId: null,
    ...(month === null ? {} : { isMonthColumn: true, monthNumber: month }),
  };
}

/** Випадковий пароль, що проходить політику (великі, малі, цифри, символ); ніде не друкується. */
function randomPassword(): string {
  return `Tq${randomBytes(12).toString('base64url')}7!x`;
}

interface Arranged {
  readonly documentId: number;
  readonly projectId: number;
  readonly year: number;
  readonly anchor: number;
  readonly september: number;
  readonly october: number;
  readonly sheets: { readonly S1: number; readonly S2: number };
  readonly tables: { readonly T1: number; readonly T2: number };
}

interface TableRow {
  tableInstanceId: number;
  tableDefId: number;
  sheetCode: string;
  tableCode: string;
  sheetDefId: number;
}

async function arrange(request: APIRequestContext): Promise<Arranged> {
  const stamp = String(Date.now());
  const year = new Date().getUTCFullYear() - 1;

  const template = await ok<{ templateId: number }>(
    await request.post('/api/v1/templates', { data: { code: `E2EYear${stamp}`, nameL10n: { en: `E2E year ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await request.post(`/api/v1/templates/${String(template.templateId)}/versions`, { data: { versionNumber: '1.0.0.0', cloneFromVersionId: null } }),
    'версія',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;
  await ok(await request.put(`${base}/layout`, { data: { layout: 'YearWithMonthColumns' } }), 'розкладка «рік»');

  for (const [sheet, tableCode, name] of [['S1', 'T1', 'Year sheet'], ['S2', 'T2', 'Secret sheet']] as const) {
    await ok(
      await request.put(`${base}/sheets/${sheet}`, { data: { nameL10n: { en: name }, ordinal: sheet === 'S1' ? 1 : 2, isMandatory: true, isVisible: true, sheetGroup: null } }),
      `аркуш ${sheet}`,
    );
    const table = await ok<{ id: number }>(
      await request.put(`${base}/sheets/${sheet}/tables/${tableCode}`, {
        data: { nameL10n: { en: `${name} table` }, ordinal: 0, layoutKind: 'MonthsInColumns', rowMode: 'Dynamic', maxDynamicRows: 20 },
      }),
      `таблиця ${tableCode}`,
    );
    const columns: [string, Record<string, unknown>][] = [
      ['NAME', column(0, `${tableCode} name`, 'String', null)],
      ...Array.from({ length: 12 }, (_, i): [string, Record<string, unknown>] => [
        `M${String(i + 1).padStart(2, '0')}`,
        column(i + 1, MonthHeader(i + 1), 'Decimal', i + 1),
      ]),
    ];
    for (const [code, body] of columns) {
      await ok(await request.put(`${base}/tables/${String(table.id)}/columns/${code}`, { data: body }), `стовпець ${tableCode}.${code}`);
    }
    // Правило рядка «жовтень ≤ 5»: правка 10 у жовтні дає зауваження — його знаходить прогін жовтня.
    await ok(
      await request.put(`${base}/tables/${String(table.id)}/validation-rules/OCT_CAP`, {
        data: { columnDefId: null, expression: '[M10] <= 5', isActive: true, messageL10n: { en: `October of ${tableCode} is above 5` }, scope: 1, severity: 'Warning' },
      }),
      `правило ${tableCode}`,
    );
  }
  await ok(await request.post(`${base}/publish`, { data: { reason: 'e2e: документ «рік» (LAND-YEAR L7)' } }), 'публікація');

  // Політика з довгою пільгою: усі місяці минулого року відкриті — правка вересня й жовтня проходить.
  const policy = await ok<{ id: number }>(
    await request.post('/api/v1/projects/period-policies', {
      data: { code: `E2EYr${stamp}`, openOffsetDays: 0, graceOffsetDays: 3000, hardCloseOffsetDays: 3600, yearGraceOffsetDays: 3600 },
    }),
    'політика періодів',
  );
  const project = await ok<{ projectId: number }>(
    await request.post('/api/v1/projects', {
      data: {
        code: `E2EYrPrj${stamp}`, nameL10n: { en: `E2E year project ${stamp}` }, periodKind: 'Monthly', timeZoneId: 'UTC',
        templateVersionId: version.versionId, periodPolicyId: policy.id, year,
      },
    }),
    'проєкт',
  );
  await ok(await request.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація');

  const structure = await ok<{ layout: string; sheets: { id: number; code: string }[] }>(await request.get(`${base}/structure`), 'структура');
  expect(structure.layout).toBe('YearWithMonthColumns');
  const sheetId = (code: string): number => structure.sheets.find((s) => s.code === code)?.id ?? 0;
  const document = await ok<{ documentId: number }>(
    await request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((s) => s.id), templateVersionId: version.versionId },
    }),
    'документ',
  );
  const documentId = document.documentId;

  const anchor = year * 100 + 1;
  const september = year * 100 + 9;
  const october = year * 100 + 10;
  // Екземпляри всіх місяців (власники місячних колонок мають існувати).
  let anchorTables: TableRow[] = [];
  for (let m = 1; m <= 12; m += 1) {
    const rows = await ok<TableRow[]>(await request.get(`/api/v1/documents/${String(documentId)}/tables?periodKey=${String(year * 100 + m)}`), `таблиці ${String(m)}`);
    if (m === 1) anchorTables = rows;
  }
  const t1 = anchorTables.find((t) => t.tableCode === 'T1');
  const t2 = anchorTables.find((t) => t.tableCode === 'T2');
  if (t1 === undefined || t2 === undefined) throw new Error('у якоря немає таблиць T1/T2');

  // Рядок і правки вересня/жовтня через ЯКІР року — сервер веде місячні колонки до власників.
  for (const table of [t1, t2]) {
    await ok(await request.post(`/api/v1/documents/${String(documentId)}/rows`, { data: { tableInstanceId: table.tableInstanceId, rowKey: 'R1' } }), `рядок ${table.tableCode}`);
    for (const [code, value] of [['M09', 9], ['M10', 10]] as const) {
      const slice = await ok<{ rows: { rowKey: string; rowVersion: string }[] }>(
        await request.get(`/api/v1/documents/${String(documentId)}/tables/${String(table.tableInstanceId)}`),
        `зріз ${table.tableCode}`,
      );
      const row = slice.rows.find((r) => r.rowKey === 'R1');
      if (row === undefined) throw new Error(`немає R1 у ${table.tableCode}`);
      await ok(
        await request.patch(`/api/v1/documents/${String(documentId)}/cells`, {
          data: {
            tableInstanceId: table.tableInstanceId, periodKey: anchor, origin: 'UserEdit',
            rows: [{ rowKey: 'R1', baseVersion: row.rowVersion, cells: [{ columnCode: code, value }] }],
          },
        }),
        `правка ${table.tableCode}.${code}`,
      );
    }
  }

  // Перевірка — лише за ЖОВТЕНЬ: якір року не перевіряли (правило OCT_CAP → зауваження обох таблиць).
  const validated = await ok<{ messages: { tableDefId: number }[] }>(
    await request.post(`/api/v1/documents/${String(documentId)}/validate`, { data: { periodKey: october } }),
    'перевірка жовтня',
  );
  expect(validated.messages.some((m) => m.tableDefId === t1.tableDefId), 'прогін жовтня без зауваження T1').toBe(true);
  expect(validated.messages.some((m) => m.tableDefId === t2.tableDefId), 'прогін жовтня без зауваження T2').toBe(true);

  return {
    documentId, projectId: project.projectId, year, anchor, september, october,
    sheets: { S1: sheetId('S1'), S2: sheetId('S2') }, tables: { T1: t1.tableDefId, T2: t2.tableDefId },
  };
}

async function signedIn(browser: Browser, userName: string, password: string): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', { data: { userName, password } });
  expect(login.ok(), `вхід ${userName}: ${String(login.status())}`).toBe(true);

  return context;
}

let arranged: Arranged;
let adminContext: BrowserContext;

test.beforeAll(async ({ browser }) => {
  adminContext = await signedIn(browser, Admin.userName, Admin.password);
  arranged = await arrange(adminContext.request);
});

test.afterAll(async () => {
  await adminContext.close();
});

test('yearLayout: ?periodKey of October opens the year anchor, no month switcher of any kind', async () => {
  const page = await adminContext.newPage();
  await page.goto(`/documents/${String(arranged.documentId)}?periodKey=${String(arranged.october)}`);

  await expect(page).toHaveURL(new RegExp(`periodKey=${String(arranged.anchor)}`), { timeout: 90_000 });
  await expect(page.getByTestId('doc-cr-year')).toBeVisible({ timeout: 90_000 });
  await expect(page.getByTestId('doc-cr-year')).toContainText(String(arranged.year));
  await expect(page.getByTestId('doc-cr-month')).toHaveCount(0);
  await expect(page.locator('.ecr-period-anchor')).toHaveCount(0);
  await page.close();
});

test('yearLayout: Issues of the year show October findings (validated in October only) with the month', async () => {
  const page = await adminContext.newPage();
  const yearRead = page.waitForRequest((r) => r.url().includes('/validation?') && r.url().includes('year=true'), { timeout: 90_000 });
  await page.goto(`/documents/${String(arranged.documentId)}?periodKey=${String(arranged.anchor)}&panel=issues`);
  await yearRead;

  const aside = page.locator('aside.ecr-insp');
  await expect(aside).toBeVisible({ timeout: 90_000 });
  const months = aside.locator(`[data-inspector-finding-period="${String(arranged.october)}"]`);
  await expect(months.first()).toBeVisible({ timeout: 60_000 });
  await expect(months.first()).toContainText('October');
  await expect(aside.locator('[data-inspector-period]')).toContainText(String(arranged.year));
  await page.close();
});

test('yearLayout: the sheet log names the month of every edit (September and October)', async () => {
  const page = await adminContext.newPage();
  await page.goto(`/documents/${String(arranged.documentId)}?periodKey=${String(arranged.anchor)}&sheet=S1&panel=history`);

  const aside = page.locator('aside.ecr-insp');
  await expect(aside).toBeVisible({ timeout: 90_000 });
  await expect(aside.locator(`[data-inspector-log-period="${String(arranged.october)}"]`).first()).toContainText('October', { timeout: 60_000 });
  await expect(aside.locator(`[data-inspector-log-period="${String(arranged.september)}"]`).first()).toContainText('September');
  await page.close();
});

test('yearLayout: Allow/Deny — a Deny on table T2 hides its year Issues and the sheet log of S2', async ({ browser }) => {
  const request = adminContext.request;
  const stamp = String(Date.now());
  const roleCode = `test-L7Deny-${stamp}`;
  const userName = `test-l7-deny-${stamp}`;

  // Allow (адміністратор): у річному результаті є зауваження T2, у журналі S2 — правка жовтня.
  const allowIssues = await ok<{ messages: { tableDefId: number; periodKey?: number | null }[] }>(
    await request.get(`/api/v1/documents/${String(arranged.documentId)}/validation?periodKey=${String(arranged.anchor)}&year=true`),
    'Allow: Issues року',
  );
  expect(allowIssues.messages.some((m) => m.tableDefId === arranged.tables.T2 && m.periodKey === arranged.october)).toBe(true);
  const allowLog = await ok<{ items: { tableCode: string | null; periodKey: number }[] }>(
    await request.get(`/api/v1/documents/${String(arranged.documentId)}/sheets/${String(arranged.sheets.S2)}/activity?periodKey=${String(arranged.anchor)}&year=true&includeSystem=false&limit=50`),
    'Allow: журнал S2',
  );
  expect(allowLog.items.some((i) => i.tableCode === 'T2' && i.periodKey === arranged.october)).toBe(true);

  // Deny: тестова роль (перегляд документів) + грант проєкту й Deny на T2; тестовий користувач з випадковим паролем.
  await ok(await request.post('/api/v1/roles', { data: { code: roleCode, nameL10n: { en: 'Test L7 deny' }, permissionCodes: ['Document.View'] } }), 'роль');
  const roles = await ok<{ id: number; code: string }[]>(await request.get('/api/v1/roles'), 'ролі');
  const roleId = roles.find((r) => r.code === roleCode)?.id;
  expect(roleId, 'тестова роль не створилась').toBeDefined();
  const issued = randomPassword();
  await ok(
    await request.post('/api/v1/users', { data: { userName, provider: 'Local', sid: null, displayName: 'Test L7 deny', initialPassword: issued, roleCodes: [roleCode] } }),
    'тестовий користувач',
  );
  const grants = await request.get(`/api/v1/roles/${String(roleId)}/grants`);
  await ok(
    await request.put(`/api/v1/roles/${String(roleId)}/grants`, {
      headers: { 'If-Match': grants.headers()['etag'] ?? '' },
      data: {
        grants: [
          { resourceKind: 'Project', resourceId: arranged.projectId, level: 'Read', isDeny: false },
          { resourceKind: 'Table', resourceId: arranged.tables.T2, level: 'Read', isDeny: true },
        ],
      },
    }),
    'гранти тестової ролі',
  );

  const work = randomPassword();
  const first = await signedIn(browser, userName, issued);
  await ok(await first.request.post('/api/v1/auth/change-password', { data: { currentPassword: issued, newPassword: work } }), 'зміна разового пароля');
  await first.close();

  const deny = await signedIn(browser, userName, work);
  try {
    const denyIssues = await ok<{ messages: { tableDefId: number }[] }>(
      await deny.request.get(`/api/v1/documents/${String(arranged.documentId)}/validation?periodKey=${String(arranged.anchor)}&year=true`),
      'Deny: Issues року',
    );
    expect(denyIssues.messages.some((m) => m.tableDefId === arranged.tables.T2), 'Deny бачить зауваження прихованої T2').toBe(false);
    expect(denyIssues.messages.some((m) => m.tableDefId === arranged.tables.T1), 'Deny не бачить зауважень видимої T1').toBe(true);

    const denyLog = await deny.request.get(
      `/api/v1/documents/${String(arranged.documentId)}/sheets/${String(arranged.sheets.S2)}/activity?periodKey=${String(arranged.anchor)}&year=true&includeSystem=false&limit=50`,
    );
    if (denyLog.status() !== 404) {
      expect(denyLog.status()).toBe(200);
      const body = (await denyLog.json()) as { items: { tableCode: string | null }[] };
      expect(body.items.some((i) => i.tableCode === 'T2'), 'Deny бачить правки прихованої T2 у журналі року').toBe(false);
    }

    // Інтерфейс Deny: Issues року — лише T1 (жодного зауваження T2 з назвою таблиці).
    const page = await deny.newPage();
    await page.goto(`/documents/${String(arranged.documentId)}?periodKey=${String(arranged.anchor)}&panel=issues`);
    const aside = page.locator('aside.ecr-insp');
    await expect(aside.locator(`[data-inspector-finding-period="${String(arranged.october)}"]`).first()).toBeVisible({ timeout: 90_000 });
    await expect(aside.locator(`[data-inspector-group="${String(arranged.tables.T2)}"]`)).toHaveCount(0);
    await expect(aside.locator(`[data-inspector-group="${String(arranged.tables.T1)}"]`)).toHaveCount(1);
  } finally {
    await deny.close();
  }
});
