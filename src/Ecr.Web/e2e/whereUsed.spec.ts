import { expect, test, type APIRequestContext, type APIResponse, type Locator, type Page } from '@playwright/test';

/**
 * Where used (ФВ-8.14) через реальний інтерфейс і сервер без підміни відповідей:
 * (1) запис довідника, що стоїть у комірці документа, — діалог «Where used» на `/admin/registries` показує розділ
 *     документів, і посилання відкриває документ (`GET /registries/{code}/entries/{id}/usage`);
 * (2) колонка шаблону, яку читає правило валідації, — «Where used» колонки показує правило, а після значення в
 *     комірці — ще й рядок даних (`GET /column-defs/{id}/usage`).
 *
 * Дані заводить сам спек через API адміністратора (як `permitUsage.spec.ts`); `afterAll` стирає значення в комірках і
 * видаляє запис довідника. Лишаються (адміністратор стенда їх не видаляє; коди унікальні, стенд одноразовий): довідник,
 * опублікована версія шаблону, проєкт і порожній документ.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`; CI — workflow `e2e-stand` з `grep`): `-Grep "Where used:"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);

interface Scenario {
  registryCode: string;
  entryId: number;
  entryCode: string;
  templateId: number;
  versionId: number;
  tableId: number;
  documentId: number;
  periodKey: number;
  tableInstanceId: number;
  rowKey: string;
}
const s = {} as Scenario;
let linked = false;

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function login(api: APIRequestContext): Promise<void> {
  const response = await api.post('/api/v1/login/local', { data: { userName: Admin.user, password: Admin.password } });
  expect(response.ok(), `вхід ${Admin.user}: ${String(response.status())}`).toBe(true);
}

function sheetColumn(header: string, ordinal: number, dataType: string, lookupRegistryDefId: number | null): Record<string, unknown> {
  return {
    headerL10n: { en: header }, ordinal, dataType, isRequired: false, isReadOnly: false, isHidden: false, precision: null, scale: null,
    defaultValue: null, displayFormat: null, lookupFilter: null, lookupRegistryDefId, styleId: null, unitId: null,
  };
}

/**
 * Довідник із записом; шаблон: таблиця Water з колонками Note, Amount (читає правило RULE_AMT) і Subst (Lookup на
 * довідник); проєкт, документ і відкритий період. Значень у комірках ще немає — їх пише `fill`.
 */
async function arrange(api: APIRequestContext): Promise<void> {
  s.registryCode = `E2E_WU_${Stamp}`;
  await ok(await api.post('/api/v1/registries', { data: { code: s.registryCode, nameL10n: { en: s.registryCode }, isTemporal: false } }), 'довідник');
  const registry = await ok<{ id: number }>(await api.get(`/api/v1/registries/${s.registryCode}/definition`), 'опис довідника');
  s.entryCode = `WU${Stamp}`;
  const entry = await ok<{ id: number }>(
    await api.post(`/api/v1/registries/${s.registryCode}/entries`, {
      data: { registryDefId: registry.id, code: s.entryCode, display: { values: { en: `Where used ${Stamp}` } }, parentEntryId: null, levelCode: null, values: {} },
    }),
    'запис довідника',
  );
  s.entryId = entry.id;

  const template = await ok<{ templateId: number }>(
    await api.post('/api/v1/templates', { data: { code: `E2EWu${Stamp}`, nameL10n: { en: `E2E where used ${Stamp}` } } }),
    'шаблон',
  );
  s.templateId = template.templateId;
  const version = await ok<{ versionId: number }>(
    await api.post(`/api/v1/templates/${String(s.templateId)}/versions`, { data: { versionNumber: '1.0.0.0', cloneFromVersionId: null } }),
    'версія шаблону',
  );
  s.versionId = version.versionId;
  const base = `/api/v1/template-versions/${String(s.versionId)}`;
  await ok(await api.put(`${base}/sheets/Main`, { data: { nameL10n: { en: 'Main sheet' }, ordinal: 0, isMandatory: true, isVisible: true, sheetGroup: null } }), 'аркуш');
  const table = await ok<{ id: number }>(
    await api.put(`${base}/sheets/Main/tables/Water`, { data: { nameL10n: { en: 'Water use' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 10 } }),
    'таблиця',
  );
  s.tableId = table.id;
  const columns = `${base}/tables/${String(s.tableId)}/columns`;
  await ok(await api.put(`${columns}/Note`, { data: sheetColumn('Note', 0, 'String', null) }), 'колонка Note');
  await ok(await api.put(`${columns}/Amount`, { data: sheetColumn('Amount', 1, 'Decimal', null) }), 'колонка Amount');
  await ok(await api.put(`${columns}/Subst`, { data: sheetColumn('Substance', 2, 'Lookup', registry.id) }), 'колонка Lookup');
  await ok(
    await api.put(`${base}/tables/${String(s.tableId)}/validation-rules/RULE_AMT`, {
      data: { columnDefId: null, expression: '[Amount] >= 0', isActive: true, messageL10n: { en: 'Amount must not be negative' }, scope: 1, severity: 'Warning' },
    }),
    'правило валідації',
  );
  await ok(await api.post(`${base}/publish`, { data: { reason: 'E2E where used' } }), 'публікація версії шаблону');
  const structure = await ok<{ sheets: { id: number }[] }>(await api.get(`${base}/structure`), 'структура версії');

  const policies = await ok<{ id: number; code: string }[]>(await api.get('/api/v1/projects/period-policies'), 'політики періодів');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  expect(policy, 'у стенда немає жодної політики періодів').toBeDefined();
  const project = await ok<{ projectId: number }>(
    await api.post('/api/v1/projects', {
      data: {
        code: `E2EWU${Stamp}`, nameL10n: { en: `E2E where used ${Stamp}` }, periodKind: 'Monthly', timeZoneId: 'UTC',
        templateVersionId: s.versionId, periodPolicyId: policy?.id, year: new Date().getUTCFullYear(),
      },
    }),
    'проєкт',
  );
  await ok(await api.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація проєкту');
  const document = await ok<{ documentId: number }>(
    await api.post('/api/v1/documents', { data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((sheet) => sheet.id), templateVersionId: s.versionId } }),
    'документ',
  );
  s.documentId = document.documentId;
  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(await api.get(`/api/v1/projects/${String(project.projectId)}/periods`), 'календар');
  const period = calendar.periods.find((item) => item.state === 'Open') ?? calendar.periods[0];
  expect(period, 'у проєкту немає періодів').toBeDefined();
  s.periodKey = period?.periodKey ?? 0;

  const tables = await ok<{ tableInstanceId: number; tableCode: string }[]>(
    await api.get(`/api/v1/documents/${String(s.documentId)}/tables?periodKey=${String(s.periodKey)}`),
    'таблиці документа',
  );
  s.tableInstanceId = tables.find((item) => item.tableCode === 'Water')?.tableInstanceId ?? 0;
  expect(s.tableInstanceId, 'у документа немає таблиці Water').toBeGreaterThan(0);
  s.rowKey = `WU${Stamp}`;
}

/** Рядок із записом у колонці Lookup і числом в Amount — тим самим запитом, що й сітка. */
async function fill(api: APIRequestContext): Promise<void> {
  linked = true;
  await ok(
    await api.patch(`/api/v1/documents/${String(s.documentId)}/cells`, {
      data: {
        tableInstanceId: s.tableInstanceId, periodKey: s.periodKey, origin: 'UserEdit',
        rows: [{ rowKey: s.rowKey, baseVersion: null, cells: [{ columnCode: 'Subst', value: s.entryId }, { columnCode: 'Amount', value: 5 }] }],
      },
    }),
    'значення в комірках',
  );
}

/** Стерти значення (документ адміністратор стенда видалити не може) — запис тоді можна видалити. */
async function unlink(api: APIRequestContext): Promise<void> {
  const slice = await ok<{ rows: { rowKey: string; rowVersion: string }[] }>(
    await api.get(`/api/v1/documents/${String(s.documentId)}/tables/${String(s.tableInstanceId)}?periodKey=${String(s.periodKey)}`),
    'рядки таблиці',
  );
  const row = slice.rows.find((item) => item.rowKey === s.rowKey);
  expect(row, `рядка ${s.rowKey} немає`).toBeDefined();
  await ok(
    await api.patch(`/api/v1/documents/${String(s.documentId)}/cells`, {
      data: {
        tableInstanceId: s.tableInstanceId, periodKey: s.periodKey, origin: 'UserEdit',
        rows: [{
          rowKey: s.rowKey, baseVersion: row?.rowVersion ?? null,
          cells: [{ columnCode: 'Subst', value: null, isEmpty: true }, { columnCode: 'Amount', value: null, isEmpty: true }],
        }],
      },
    }),
    'стирання значень',
  );
  linked = false;
}

async function signIn(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Діалог «Where used» колонки Amount на сторінці версії шаблону. */
async function openAmountUsage(page: Page): Promise<Locator> {
  await page.goto(`/admin/templates/${String(s.templateId)}/versions/${String(s.versionId)}?node=t:${String(s.tableId)}`);
  const row = page.locator('tr', { hasText: '(Amount)' });
  await row.locator('[data-column-usage="trigger"]').click({ timeout: 30_000 });
  const dialog = page.getByRole('dialog');
  await expect(dialog.locator('[data-registry-usage="list"]')).toBeVisible({ timeout: 30_000 });

  return dialog;
}

test.describe('Where used', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });
  let admin: APIRequestContext;

  test.beforeAll(async ({ playwright }, info) => {
    test.setTimeout(240_000);
    const baseURL = info.project.use.baseURL;
    admin = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    await login(admin);
    await arrange(admin);
  });

  test.afterAll(async () => {
    test.setTimeout(120_000);
    const problems: string[] = [];
    try {
      if (linked) await unlink(admin);
      if (s.entryId !== undefined) {
        const response = await admin.delete(`/api/v1/registries/${s.registryCode}/entries/${String(s.entryId)}`);
        if (response.status() >= 300 && response.status() !== 404) problems.push(`entry: ${String(response.status())} ${(await response.text()).slice(0, 300)}`);
      }
    } catch (error) {
      problems.push(`cleanup: ${String(error).slice(0, 300)}`);
    }
    await admin.dispose();
    expect(problems, 'прибирання стенда').toEqual([]);
  });

  test('Where used: a column shows the validation rule that reads it, and the data line once a cell has a value', async ({ page }) => {
    await signIn(page);

    let dialog = await openAmountUsage(page);
    await expect(dialog.getByText('Template validation rule')).toBeVisible();
    await expect(dialog.getByRole('link', { name: 'Water.RULE_AMT' })).toHaveAttribute(
      'href',
      `/admin/templates/${String(s.templateId)}/versions/${String(s.versionId)}`,
    );
    await expect(dialog.locator('[data-registry-usage="data"]'), 'даних ще немає — рядка даних теж').toHaveCount(0);
    await expect(dialog).not.toContainText('⟦');

    await fill(admin);
    await page.keyboard.press('Escape');
    await page.reload();
    dialog = await openAmountUsage(page);
    await expect(dialog.locator('[data-registry-usage="data"]')).toHaveText('Values of this column are already stored in documents');
    await expect(dialog.getByRole('link', { name: 'Water.RULE_AMT' })).toBeVisible();
  });

  test('Where used: an entry lists the document whose cell holds it, and the link opens the document', async ({ page }) => {
    if (!linked) await fill(admin);
    await signIn(page);

    await page.goto(`/admin/registries?code=${encodeURIComponent(s.registryCode)}&q=${encodeURIComponent(s.entryCode)}`);
    await page.getByRole('button', { name: `Where entry ${s.entryCode} is used` }).click({ timeout: 30_000 });
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('heading', { name: 'Documents that hold this entry' })).toBeVisible({ timeout: 30_000 });

    const documents = dialog.locator('[data-entry-usage="documents"]');
    const link = documents.getByRole('link');
    await expect(link).toHaveCount(1);
    await expect(link).toHaveAttribute('href', `/documents/${String(s.documentId)}`);
    await expect(dialog.locator('[data-entry-usage="none-named"]')).toHaveCount(0);
    await expect(dialog).not.toContainText('⟦');

    await link.click();
    await expect(page).toHaveURL(new RegExp(`/documents/${String(s.documentId)}(\\?|$)`));
  });
});
