import { expect, test, type APIRequestContext, type APIResponse, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Permit usage (PERMIT-USAGE, план 30.10): дозвіл пов'язаний з даними лише ЗНАЧЕННЯМ — поле шапки документа Lookup на
 * шапку типу дозволу (`PRM_<TYPE>`) і колонка таблиці Lookup на той самий реєстр. Реальний сервер і клієнт без підміни
 * відповідей: `GET /permits/{id}/usages`, `usedInDocumentCount` переліку, вкладка «Used in» картки (посилання на документ,
 * фільтр періоду, порожній стан), колонка «Used in documents» переліку, комірка сітки показує номер дозволу (вибір
 * дозволу в колонці), вкладки прив'язок сховані. axe wcag2a/aa і контраст у light і dark, 1440 і 500 px.
 *
 * Дані заводить сам спек через API адміністратора; `afterAll` прибирає все, що API дає прибрати: значення дозволу в
 * шапці й комірці (стерто — посилань не лишається), чернетку дозволу, чернетку типу (якщо до публікації не дійшло),
 * тему адміністратора — назад на light. Лишаються (API їх адміністраторові стенда не видаляє: документ потребує
 * `Document.Delete`; коди унікальні, стенд — одноразова база `e2e-stand`, як і в `permitLookupHeader.spec.ts`):
 * опублікований тип, чинний дозвіл, версія шаблону, проєкт і порожній документ.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`; CI — workflow `e2e-stand` з `grep`): `-Grep "Permit usage"`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const ShotsDir = process.env['ECR_E2E_SHOTS'] ?? '';

interface Scenario {
  typeCode: string;
  usedId: number;
  usedNumber: string;
  draftId: number;
  documentId: number;
  periodKey: number;
  tableInstanceId: number;
  rowKey: string;
}
const s = {} as Scenario;
let typeCode = '';
let linked = false;
let draftDeleted = false;

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function login(api: APIRequestContext): Promise<void> {
  const response = await api.post('/api/v1/login/local', { data: { userName: Admin.user, password: Admin.password } });
  expect(response.ok(), `вхід ${Admin.user}: ${String(response.status())}`).toBe(true);
}

async function createPermit(api: APIRequestContext, typeCode: string, number: string, activate: boolean): Promise<number> {
  const created = await ok<{ id: number }>(
    await api.post('/api/v1/permits', {
      data: {
        typeCode,
        number,
        location: 'E2E usage facility',
        issueDate: '2026-01-01',
        validFrom: '2026-01-01',
        validTo: '2031-01-01',
        reportingFlags: { form300: false, form301: false, form302: false, tox: false },
      },
    }),
    `дозвіл ${number}`,
  );
  if (activate) await ok(await api.post(`/api/v1/permits/${String(created.id)}/activate`), `активація ${number}`);

  return created.id;
}

function stringColumn(code: string, order: number, over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    code, nameL10n: { en: code }, dataType: 'String', unitId: null, required: false, key: false, group: null, precision: null,
    scale: null, listScope: null, listRegistryCode: null, siblingTableCode: null, choices: null, order, ...over,
  };
}

function sheetColumn(header: string, ordinal: number, dataType: string, lookupRegistryDefId: number | null): Record<string, unknown> {
  return {
    headerL10n: { en: header }, ordinal, dataType, isRequired: false, isReadOnly: false, isHidden: false, precision: null, scale: null,
    defaultValue: null, displayFormat: null, lookupFilter: null, lookupRegistryDefId, styleId: null, unitId: null,
  };
}

/**
 * Тип (опублікований), чинний дозвіл і чернетка; шаблон з полем шапки й колонкою Lookup на `PRM_<TYPE>`; проєкт,
 * документ, відкритий період; дозвіл обрано в шапці й у комірці — тими самими запитами, що й інтерфейс.
 */
async function arrange(api: APIRequestContext): Promise<void> {
  // Код типу запам'ятовується ДО першого запиту, що може його створити: `afterAll` прибирає й тоді, коли впало посередині.
  typeCode = `E2E_PU_${Stamp}`;
  s.typeCode = typeCode;
  const created = await ok<{ rowVersion: string; nameL10n: Record<string, string> }>(
    await api.post('/api/v1/permit-types', { data: { code: s.typeCode, category: 'Water', nameL10n: { en: `E2E usage type ${Stamp}` } } }),
    'тип дозволу',
  );
  const replaced = await ok<{ rowVersion: string }>(
    await api.put(`/api/v1/permit-types/${s.typeCode}`, {
      data: {
        category: 'Water', nameL10n: created.nameL10n, rowVersion: created.rowVersion, rules: [],
        tables: [{ code: 'Limits', nameL10n: { en: 'Limits' }, order: 1, columns: [stringColumn('Name', 1, { key: true, required: true })], rules: [] }],
      },
    }),
    'чернетка типу',
  );
  await ok(await api.post(`/api/v1/permit-types/${s.typeCode}/publish`, { data: { reason: 'E2E permit usage', rowVersion: replaced.rowVersion } }), 'публікація типу');

  s.usedNumber = `E2E-PU-${Stamp}`;
  s.usedId = await createPermit(api, s.typeCode, s.usedNumber, true);
  s.draftId = await createPermit(api, s.typeCode, `E2E-PU-DRAFT-${Stamp}`, false);

  const targets = await ok<{ id: number; code: string }[]>(await api.get('/api/v1/registries?includePermitHeaders=true'), 'цілі Lookup');
  const header = targets.find((registry) => registry.code.toUpperCase() === `PRM_${s.typeCode}`.toUpperCase());
  expect(header, `шапку PRM_${s.typeCode} не віддано в цілях Lookup`).toBeDefined();
  const registryId = header?.id ?? 0;

  const template = await ok<{ templateId: number }>(
    await api.post('/api/v1/templates', { data: { code: `E2EPu${Stamp}`, nameL10n: { en: `E2E permit usage ${Stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await api.post(`/api/v1/templates/${String(template.templateId)}/versions`, { data: { versionNumber: '1.0.0.0', cloneFromVersionId: null } }),
    'версія шаблону',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;
  await ok(await api.put(`${base}/sheets/Main`, { data: { nameL10n: { en: 'Water sheet' }, ordinal: 0, isMandatory: true, isVisible: true, sheetGroup: null } }), 'аркуш');
  const table = await ok<{ id: number }>(
    await api.put(`${base}/sheets/Main/tables/Water`, { data: { nameL10n: { en: 'Water use' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 10 } }),
    'таблиця',
  );
  await ok(await api.put(`${base}/tables/${String(table.id)}/columns/Note`, { data: sheetColumn('Note', 0, 'String', null) }), 'колонка Note');
  await ok(await api.put(`${base}/tables/${String(table.id)}/columns/PermitCol`, { data: sheetColumn('Water permit', 1, 'Lookup', registryId) }), 'колонка Lookup на PRM_*');
  await ok(
    await api.put(`${base}/header-fields/PermitNo`, { data: { dataType: 'Lookup', isRequired: false, labelL10n: { en: 'Permit number' }, lookupRegistryDefId: registryId, ordinal: 0 } }),
    'поле шапки Lookup на PRM_*',
  );
  await ok(await api.post(`${base}/publish`, { data: { reason: 'E2E permit usage' } }), 'публікація версії шаблону');
  const structure = await ok<{ sheets: { id: number; code: string }[] }>(await api.get(`${base}/structure`), 'структура версії');

  const policies = await ok<{ id: number; code: string }[]>(await api.get('/api/v1/projects/period-policies'), 'політики періодів');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  expect(policy, 'у стенда немає жодної політики періодів').toBeDefined();
  const project = await ok<{ projectId: number }>(
    await api.post('/api/v1/projects', {
      data: {
        code: `E2EPU${Stamp}`, nameL10n: { en: `E2E permit usage ${Stamp}` }, periodKind: 'Monthly', timeZoneId: 'UTC',
        templateVersionId: version.versionId, periodPolicyId: policy?.id, year: new Date().getUTCFullYear(),
      },
    }),
    'проєкт',
  );
  await ok(await api.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація проєкту');
  const document = await ok<{ documentId: number }>(
    await api.post('/api/v1/documents', { data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((sheet) => sheet.id), templateVersionId: version.versionId } }),
    'документ',
  );
  s.documentId = document.documentId;
  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(await api.get(`/api/v1/projects/${String(project.projectId)}/periods`), 'календар');
  const period = calendar.periods.find((item) => item.state === 'Open') ?? calendar.periods[0];
  expect(period, 'у проєкту немає періодів').toBeDefined();
  s.periodKey = period?.periodKey ?? 0;

  // Шапка: дозвіл обрано значенням поля PermitNo.
  const headerDto = await ok<{ version: string }>(await api.get(`/api/v1/documents/${String(s.documentId)}/header`), 'шапка документа');
  await ok(
    await api.patch(`/api/v1/documents/${String(s.documentId)}/header`, {
      data: { baseVersion: headerDto.version, fields: [{ code: 'PermitNo', value: s.usedId, isEmpty: false }] },
    }),
    'вибір дозволу в шапці',
  );

  // Комірка: новий рядок динамічної таблиці з дозволом у колонці Lookup.
  const tables = await ok<{ tableInstanceId: number; tableCode: string }[]>(
    await api.get(`/api/v1/documents/${String(s.documentId)}/tables?periodKey=${String(s.periodKey)}`),
    'таблиці документа',
  );
  const water = tables.find((item) => item.tableCode === 'Water');
  expect(water, 'у документа немає таблиці Water').toBeDefined();
  s.tableInstanceId = water?.tableInstanceId ?? 0;
  s.rowKey = `PU${Stamp}`;
  linked = true;
  await ok(
    await api.patch(`/api/v1/documents/${String(s.documentId)}/cells`, {
      data: {
        tableInstanceId: s.tableInstanceId, periodKey: s.periodKey, origin: 'UserEdit',
        rows: [{ rowKey: s.rowKey, baseVersion: null, cells: [{ columnCode: 'PermitCol', value: s.usedId }, { columnCode: 'Note', value: 'e2e' }] }],
      },
    }),
    'вибір дозволу в колонці',
  );
}

/**
 * Прибрати дозвіл зі значень: поле шапки й комірку стерто тими самими запитами, що й інтерфейс. Документ видалити
 * адміністратор стенда не може (`Document.Delete` у його ролі немає), тож посилання знімаються стиранням значень.
 */
async function unlink(api: APIRequestContext): Promise<void> {
  const headerDto = await ok<{ version: string }>(await api.get(`/api/v1/documents/${String(s.documentId)}/header`), 'шапка документа');
  await ok(
    await api.patch(`/api/v1/documents/${String(s.documentId)}/header`, {
      data: { baseVersion: headerDto.version, fields: [{ code: 'PermitNo', value: null, isEmpty: true }] },
    }),
    'стирання дозволу в шапці',
  );
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
        rows: [{ rowKey: s.rowKey, baseVersion: row?.rowVersion ?? null, cells: [{ columnCode: 'PermitCol', value: null, isEmpty: true }] }],
      },
    }),
    'стирання дозволу в колонці',
  );
  linked = false;
}

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Тема на `<html>`: сервер зберігає вподобання і перекриває localStorage, тож перемикаємо через меню користувача. */
async function ensureScheme(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  const html = page.locator('html');
  const menu = page.getByRole('button', { name: new RegExp(Admin.user, 'i') }).first();
  await expect(menu).toBeVisible({ timeout: 30_000 });
  if ((await html.getAttribute('data-mantine-color-scheme')) !== scheme) {
    await menu.click();
    await page.getByText(scheme === 'dark' ? 'Dark' : 'Light', { exact: true }).first().click();
    await page.keyboard.press('Escape');
  }
  await expect(html, `тема ${scheme} не застосувалась`).toHaveAttribute('data-mantine-color-scheme', scheme, { timeout: 15_000 });
}

async function openAs(browser: Browser, scheme: 'light' | 'dark', width = 1440): Promise<{ page: Page; context: BrowserContext }> {
  const context = await browser.newContext({ viewport: { width, height: 900 } });
  const page = await context.newPage();
  await page.emulateMedia({ colorScheme: scheme });
  await signIn(page);
  await ensureScheme(page, scheme);
  await page.reload();
  await ensureScheme(page, scheme);

  return { page, context };
}

/** Ключі каталогу й нерозгорнуті плейсхолдери не мають потрапляти на екран. */
async function expectNoRawKeys(page: Page): Promise<void> {
  const text = await page.locator('main').innerText();
  expect(text, 'на екрані сирий ключ каталогу').not.toMatch(/⟦|permits\.usage\.|permits\.page\.tabs\./);
  expect(text, 'на екрані нерозгорнутий плейсхолдер').not.toMatch(/\{[a-zA-Z]+\}/);
}

async function axeViolations(page: Page): Promise<{ all: string[]; contrast: string[] }> {
  await page.addScriptTag({ path: 'node_modules/axe-core/axe.min.js' });

  return page.evaluate(async () => {
    const axe = (window as unknown as {
      axe: {
        run: (c: Document, o: object) => Promise<{
          violations: { id: string; nodes: { target: unknown[]; any: { data?: { fgColor?: string; bgColor?: string; contrastRatio?: number } }[] }[] }[];
        }>;
      };
    }).axe;
    const result = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa'] } });
    const all = result.violations.flatMap((v) => v.nodes.map((n) => `${v.id}: ${String(n.target.join(' '))}`));
    const contrast = result.violations
      .filter((v) => v.id === 'color-contrast')
      .flatMap((v) =>
        v.nodes.map((n) => {
          const d = n.any[0]?.data;
          return `${String(n.target.join(' '))} fg=${d?.fgColor ?? '?'} bg=${d?.bgColor ?? '?'} ratio=${String(d?.contrastRatio ?? '?')}`;
        }),
      );

    return { all, contrast };
  });
}

async function shot(page: Page, scheme: string, width: number, label: string): Promise<void> {
  if (ShotsDir === '') return;
  mkdirSync(ShotsDir, { recursive: true });
  await page.screenshot({ path: join(ShotsDir, `permit-usage-${label}-${scheme}-${String(width)}.png`), fullPage: true });
}

const usagesTable = (page: Page) => page.getByRole('table', { name: 'Places where the permit is used' });

test.describe('Permit usage', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });
  let admin: APIRequestContext;

  test.beforeAll(async ({ playwright }, info) => {
    test.setTimeout(240_000);
    const baseURL = info.project.use.baseURL;
    admin = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    await login(admin);
    await arrange(admin);
  });

  test.afterAll(async ({ browser }) => {
    test.setTimeout(180_000);
    const problems: string[] = [];
    if (linked) {
      try {
        await unlink(admin);
      } catch (error) {
        problems.push(`unlink: ${String(error).slice(0, 300)}`);
      }
    }
    if (!draftDeleted && s.draftId !== undefined) {
      const response = await admin.delete(`/api/v1/permits/${String(s.draftId)}`);
      if (response.status() >= 300 && response.status() !== 404) problems.push(`draft permit: ${String(response.status())} ${(await response.text()).slice(0, 300)}`);
    }
    // Тип: видаляється лише чернетка, що не публікувалась (як `permitTypeCleanup.ts`); опублікований - 409, і це не збій.
    if (typeCode !== '') {
      const response = await admin.delete(`/api/v1/permit-types/${encodeURIComponent(typeCode)}`);
      if (response.status() >= 300 && response.status() !== 404 && response.status() !== 409) {
        problems.push(`type: ${String(response.status())} ${(await response.text()).slice(0, 300)}`);
      }
    }

    // Тема адміністратора назад на light: вподобання зберігається на сервері і перекриває localStorage.
    const reset = await openAs(browser, 'light');
    await reset.context.close();
    await admin.dispose();
    expect(problems, 'прибирання стенда').toEqual([]);
  });

  test('Permit usage: the server counts the header field and the table cell', async () => {
    const usage = await ok<{ documentCount: number; linkCount: number; truncated: boolean; items: { kind: string; documentId: number; sheetCode: string | null }[] }>(
      await admin.get(`/api/v1/permits/${String(s.usedId)}/usages`),
      'usages',
    );
    expect(usage.documentCount).toBe(1);
    expect(usage.linkCount).toBe(2);
    expect(usage.truncated).toBe(false);
    expect(usage.items.map((item) => item.kind).sort()).toEqual(['cell', 'header']);
    expect(usage.items.every((item) => item.documentId === s.documentId)).toBe(true);

    const list = await ok<{ items: { id: number; usedInDocumentCount: number }[] }>(
      await admin.get(`/api/v1/permits?q=${encodeURIComponent(s.usedNumber)}&limit=500`),
      'перелік дозволів',
    );
    expect(list.items.find((item) => item.id === s.usedId)?.usedInDocumentCount).toBe(1);
    expect(list.items.find((item) => item.id === s.draftId)?.usedInDocumentCount ?? 0).toBe(0);
  });

  test('Permit usage: the card tab lists both places, links to the document and filters by period', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);
    await page.goto(`/admin/permits/${String(s.usedId)}`);
    await page.getByRole('tab', { name: 'Used in' }).click();
    await expect(page).toHaveURL(new RegExp(`/admin/permits/${String(s.usedId)}\\?tab=usage`));

    const table = usagesTable(page);
    await expect(table).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText('Documents: 1 · places: 2')).toBeVisible();
    const rows = table.locator('tbody tr');
    await expect(rows).toHaveCount(2);

    const headerRow = rows.filter({ hasText: 'Document header' });
    await expect(headerRow).toContainText('Permit number');
    await expect(headerRow.getByRole('link')).toHaveAttribute('href', `/documents/${String(s.documentId)}`);

    const cellRow = rows.filter({ hasText: 'Water sheet · Water use' });
    await expect(cellRow).toContainText('Water permit');
    await expect(cellRow.getByRole('link')).toHaveAttribute(
      'href',
      `/documents/${String(s.documentId)}?periodKey=${String(s.periodKey)}&sheet=Main&table=Water`,
    );
    await expectNoRawKeys(page);

    // Фільтр періоду: сервер фільтрує за ?period=; комірка цього періоду лишається.
    await page.getByRole('textbox', { name: 'Period' }).click();
    await page.getByRole('option').nth(1).click();
    await expect(page).toHaveURL(new RegExp(`period=${String(s.periodKey)}`));
    await expect(usagesTable(page).locator('tbody tr').filter({ hasText: 'Water sheet · Water use' })).toHaveCount(1, { timeout: 30_000 });

    // Посилання комірки веде на документ із періодом, аркушем і таблицею; сітка показує НОМЕР дозволу в колонці.
    await usagesTable(page).locator('tbody tr').filter({ hasText: 'Water sheet · Water use' }).getByRole('link').click();
    await expect(page).toHaveURL(new RegExp(`/documents/${String(s.documentId)}\\?periodKey=${String(s.periodKey)}&sheet=Main&table=Water`));
    const grid = page.locator('revo-grid').first();
    await expect(grid.locator('revogr-data .rgCell', { hasText: s.usedNumber }).first(), 'колонка Lookup не показує номер дозволу').toBeVisible({
      timeout: 60_000,
    });
  });

  test('Permit usage: an unused permit shows the empty state and the list shows the count', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);
    await page.goto(`/admin/permits/${String(s.draftId)}?tab=usage`);
    await expect(page.getByText('Not used in any document yet')).toBeVisible({ timeout: 30_000 });
    await expect(page.getByText('Documents: 0 · places: 0')).toBeVisible();

    await page.goto(`/admin/permits?q=${encodeURIComponent(`E2E-PU-`)}`);
    const list = page.getByRole('table').first();
    await expect(list.getByRole('columnheader', { name: /Used in documents/ })).toBeVisible({ timeout: 30_000 });
    await expect(list.getByRole('columnheader', { name: /Read by/ })).toHaveCount(0);
    const used = list.locator('tbody tr').filter({ hasText: s.usedNumber });
    await expect(used.getByRole('cell', { name: '1', exact: true }), 'колонка «Used in documents» не показала 1').toHaveCount(1);

    // Прив'язки сховані прапорцем: у смузі розділів Permits посилання «Sheet bindings» немає.
    await expect(page.getByRole('link', { name: 'Sheet bindings', exact: true })).toHaveCount(0);
    await expectNoRawKeys(page);
  });

  for (const scheme of ['light', 'dark'] as const) {
    for (const width of [1440, 500]) {
      test(`Permit usage: axe and contrast - ${scheme} ${String(width)}`, async ({ browser }) => {
        const { page, context } = await openAs(browser, scheme, width);
        try {
          await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);
          const clean = async (label: string): Promise<void> => {
            await expectNoRawKeys(page);
            const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
            expect.soft(overflow, `${label}: горизонтальне переповнення сторінки`).toBeLessThanOrEqual(1);
            const found = await axeViolations(page);
            expect.soft(found.contrast, `${label}: контраст ${scheme} ${String(width)}`).toEqual([]);
            expect.soft(found.all, `${label}: axe wcag2a/aa ${scheme} ${String(width)}`).toEqual([]);
            await shot(page, scheme, width, label);
          };

          await page.goto(`/admin/permits/${String(s.usedId)}?tab=usage`);
          await expect(usagesTable(page)).toBeVisible({ timeout: 30_000 });
          await clean('card-used-in');

          await page.goto(`/admin/permits/${String(s.draftId)}?tab=usage`);
          await expect(page.getByText('Not used in any document yet')).toBeVisible({ timeout: 30_000 });
          await clean('card-used-in-empty');

          await page.goto(`/admin/permits?q=${encodeURIComponent(`E2E-PU-${Stamp}`)}`);
          await expect(page.getByRole('columnheader', { name: /Used in documents/ })).toBeVisible({ timeout: 30_000 });
          await clean('list-used-in');
        } finally {
          await context.close();
        }
      });
    }
  }

  test('Permit usage: clearing the values removes the places from the permit', async () => {
    await unlink(admin);
    const usage = await ok<{ documentCount: number; linkCount: number }>(await admin.get(`/api/v1/permits/${String(s.usedId)}/usages`), 'usages після видалення');
    expect(usage).toMatchObject({ documentCount: 0, linkCount: 0 });

    await ok(await admin.delete(`/api/v1/permits/${String(s.draftId)}`), 'видалення чернетки дозволу');
    draftDeleted = true;
  });
});
