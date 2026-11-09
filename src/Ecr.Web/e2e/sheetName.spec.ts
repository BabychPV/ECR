import { expect, test, type APIResponse, type Page } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * `SHEETNAME()` на живому стенді (`tools/e2e-stand.ps1 -Grep "SHEETNAME:"`, workflow `e2e-stand`).
 *
 * Сценарій: шаблон з копійованим аркушем і колонкою-формулою `SHEETNAME()` -> документ -> копія аркуша з рядком ->
 * перейменування групи «E2E vessel» -> на копії формула дає «E2E vessel 2» (оригінал «E2E vessel 1») ->
 * перейменування «E2E contract» -> «E2E contract 2». Значення пише фонова задача перерахунку, яку ставить
 * перейменування (`DocumentSheetCopyHandler`), тож тест чекає зрізу (обох екземплярів), а не відповіді PUT: задачі
 * документа йдуть по черзі під його блокуванням (по одній на період), і оригінал може дійти пізніше за копію.
 * Третій кадр — автодоповнення `SHEETNAME` у редакторі виразів шаблону.
 *
 * ⛔ Без стенда файл ПАДАЄ (вхід/запити до API), а не пропускається (honesty-guard).
 */
test.describe.configure({ timeout: 600_000 });

const SheetCode = 'VESSELS';
const Shots = process.env['ECR_E2E_SHEETNAME_SHOTS'];

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

function column(ordinal: number, header: string, dataType: string): Record<string, unknown> {
  return {
    headerL10n: { en: header },
    ordinal,
    dataType,
    isRequired: false,
    isReadOnly: dataType === 'Formula',
    isHidden: false,
    precision: null,
    scale: null,
    defaultValue: null,
    displayFormat: null,
    lookupFilter: null,
    lookupRegistryDefId: null,
    styleId: null,
    unitId: null,
  };
}

interface Arranged {
  documentId: number;
  periodKey: number;
  sheetDefId: number;
}

interface TableRef {
  tableInstanceId: number;
  sheetDefId: number;
  sheetCopyId?: number | null;
}

interface Slice {
  rows: { cells: Record<string, unknown> }[];
}

/** Шаблон: копійований аркуш, динамічна таблиця, колонка вводу і колонка-формула `SHEETNAME()`. */
async function arrange(page: Page): Promise<Arranged> {
  const stamp = String(Date.now());
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2ESn${stamp}`, nameL10n: { en: `E2E sheet name ${stamp}` } } }),
    'шаблон',
  );
  const version = await ok<{ versionId: number }>(
    await page.request.post(`/api/v1/templates/${String(template.templateId)}/versions`, {
      data: { versionNumber: '1.0.0.0', cloneFromVersionId: null },
    }),
    'версія шаблону',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;
  await ok(
    await page.request.put(`${base}/sheets/${SheetCode}`, {
      data: { nameL10n: { en: 'Vessel' }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null, isCopyable: true },
    }),
    'копійований аркуш',
  );
  const table = await ok<{ id: number }>(
    await page.request.put(`${base}/sheets/${SheetCode}/tables/T1`, {
      data: { nameL10n: { en: 'Readings' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
    }),
    'таблиця',
  );
  await ok(await page.request.put(`${base}/tables/${String(table.id)}/columns/NOTE`, { data: column(0, 'Note', 'String') }), 'колонка NOTE');
  await ok(await page.request.put(`${base}/tables/${String(table.id)}/columns/SHEET`, { data: column(1, 'Sheet', 'Formula') }), 'колонка SHEET');

  const draft = await ok<{ sheets: { id: number; tables: { columns: { id: number; code: string }[] }[] }[] }>(
    await page.request.get(`${base}/structure`),
    'структура чернетки',
  );
  const sheetColumn = draft.sheets[0]?.tables[0]?.columns.find((candidate) => candidate.code === 'SHEET')?.id ?? 0;
  await ok(
    await page.request.put(`${base}/tables/${String(table.id)}/formulas/column/${String(sheetColumn)}`, {
      data: { dialect: 'Template', expression: 'SHEETNAME()' },
    }),
    'формула SHEETNAME()',
  );
  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: SHEETNAME()' } }), 'публікація');

  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2ESnPrj${stamp}`,
        nameL10n: { en: `E2E sheet name ${stamp}` },
        periodKind: 'Monthly',
        timeZoneId: 'UTC',
        templateVersionId: version.versionId,
        periodPolicyId: policy?.id,
        year: new Date().getUTCFullYear(),
      },
    }),
    'проєкт',
  );
  await ok(await page.request.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація проєкту');
  const sheetDefId = (await ok<{ sheets: { id: number }[] }>(await page.request.get(`${base}/structure`), 'структура')).sheets[0]?.id ?? 0;
  const document = await ok<{ documentId: number }>(
    await page.request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: [sheetDefId], templateVersionId: version.versionId },
    }),
    'документ',
  );
  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
    await page.request.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар',
  );
  const periodKey = (calendar.periods.find((item) => item.state === 'Open') ?? calendar.periods[0])?.periodKey ?? 0;

  return { documentId: document.documentId, periodKey, sheetDefId };
}

async function tables(page: Page, doc: Arranged): Promise<TableRef[]> {
  return ok<TableRef[]>(
    await page.request.get(`/api/v1/documents/${String(doc.documentId)}/tables?periodKey=${String(doc.periodKey)}`),
    'таблиці документа',
  );
}

/** Значення колонки SHEET у першому рядку екземпляра — коли фонова задача його вже записала. */
async function sheetValue(page: Page, doc: Arranged, tableInstanceId: number): Promise<unknown> {
  const slice = await ok<Slice>(
    await page.request.get(`/api/v1/documents/${String(doc.documentId)}/tables/${String(tableInstanceId)}`),
    'зріз',
  );

  return slice.rows[0]?.cells['SHEET'] ?? null;
}

async function rename(page: Page, doc: Arranged, name: string): Promise<void> {
  await ok(
    await page.request.put(`/api/v1/documents/${String(doc.documentId)}/sheets/${String(doc.sheetDefId)}/name`, {
      data: { nameL10n: { en: name } },
    }),
    `перейменування «${name}»`,
  );
}

async function shot(page: Page, name: string): Promise<void> {
  await page.screenshot({ path: test.info().outputPath(`${name}.png`), fullPage: false });
  if (Shots !== undefined && Shots !== '') {
    await page.screenshot({ path: `${Shots}/${name}.png`, fullPage: false });
  }
}

test('SHEETNAME: copy marker — подана копія після перерахунку показує «Recalculated after submission»', async ({ page }) => {
  // F13 p218 #788: сервер позначав копію («VESSELS#id»), а сторінка шукала позначку за голим кодом — банер копії мовчав.
  await signIn(page);
  const doc = await arrange(page);

  await tables(page, doc);
  const copy = await ok<{ sheetCopyId: number }>(
    await page.request.post(`/api/v1/documents/${String(doc.documentId)}/sheets/${String(doc.sheetDefId)}/copies`),
    'копія аркуша',
  );
  const copyTable = (await tables(page, doc)).find((table) => table.sheetCopyId === copy.sheetCopyId);
  expect(copyTable, 'у відповіді немає таблиці копії').toBeDefined();
  await ok(
    await page.request.post(`/api/v1/documents/${String(doc.documentId)}/rows`, {
      data: { tableInstanceId: copyTable?.tableInstanceId ?? 0, rowKey: null },
    }),
    'рядок копії',
  );

  await ok(
    await page.request.post(`/api/v1/documents/${String(doc.documentId)}/submit`, {
      data: { sheetDefId: doc.sheetDefId, periodKey: doc.periodKey, acknowledgeWarnings: true, sheetCopyId: copy.sheetCopyId },
    }),
    'подання копії',
  );
  await ok(
    await page.request.post(`/api/v1/documents/${String(doc.documentId)}/recalculate`, { data: { periodKey: doc.periodKey } }),
    'ручний перерахунок',
  );

  const copyKey = `${SheetCode}#${String(copy.sheetCopyId)}`;
  await expect
    .poll(
      async () => {
        const summary = await ok<{ sheets?: { code: string; recalculatedAfterSubmission?: boolean }[] }>(
          await page.request.get(`/api/v1/documents/${String(doc.documentId)}?periodKey=${String(doc.periodKey)}`),
          'картка документа',
        );
        return summary.sheets?.find((s) => s.code === copyKey)?.recalculatedAfterSubmission ?? false;
      },
      { timeout: 180_000, intervals: [1_000, 2_000, 5_000] },
    )
    .toBe(true);

  await page.goto(
    `/documents/${String(doc.documentId)}?periodKey=${String(doc.periodKey)}&sheet=${SheetCode}~${String(copy.sheetCopyId)}`,
  );
  await expect(page.locator('[data-recalculated-after-submission]')).toBeVisible({ timeout: 60_000 });
  await shot(page, '04-submitted-copy-recalculated-marker');
});

test('SHEETNAME: копія аркуша показує «<Назва> 2» і оновлюється після перейменування групи', async ({ page }) => {
  await signIn(page);
  const doc = await arrange(page);

  const before = await tables(page, doc);
  const original = before[0];
  expect(original, 'у документа немає таблиць').toBeDefined();
  const copy = await ok<{ sheetCopyId: number; ordinal: number }>(
    await page.request.post(`/api/v1/documents/${String(doc.documentId)}/sheets/${String(doc.sheetDefId)}/copies`),
    'копія аркуша',
  );
  expect(copy.ordinal).toBe(2);

  const after = await tables(page, doc);
  const copyTable = after.find((table) => table.sheetCopyId === copy.sheetCopyId);
  expect(copyTable, 'у відповіді немає таблиці копії').toBeDefined();

  for (const instance of [original?.tableInstanceId ?? 0, copyTable?.tableInstanceId ?? 0]) {
    await ok(
      await page.request.post(`/api/v1/documents/${String(doc.documentId)}/rows`, { data: { tableInstanceId: instance, rowKey: null } }),
      'рядок',
    );
  }

  // Перейменування ставить перерахунок формул аркуша — значення пише фонова задача.
  await rename(page, doc, 'E2E vessel');
  await expect
    .poll(() => sheetValue(page, doc, copyTable?.tableInstanceId ?? 0), { timeout: 180_000, intervals: [1_000, 2_000, 5_000] })
    .toBe('E2E vessel 2');
  await expect
    .poll(() => sheetValue(page, doc, original?.tableInstanceId ?? 0), { timeout: 180_000, intervals: [1_000, 2_000, 5_000] })
    .toBe('E2E vessel 1');

  await page.goto(
    `/documents/${String(doc.documentId)}?periodKey=${String(doc.periodKey)}&sheet=${SheetCode}~${String(copy.sheetCopyId)}`,
  );
  const copyTab = page.getByRole('tab', { name: /E2E vessel 2/ });
  await expect(copyTab).toHaveAttribute('aria-selected', 'true', { timeout: 60_000 });
  // Значення в сітці (комірка-формула «ƒ …»), а не лише назва вкладки.
  await expect(page.getByRole('gridcell', { name: /E2E vessel 2/ })).toBeVisible({ timeout: 60_000 });
  await shot(page, '01-copy-sheetname-vessel-2');

  await rename(page, doc, 'E2E contract');
  await expect
    .poll(() => sheetValue(page, doc, copyTable?.tableInstanceId ?? 0), { timeout: 180_000, intervals: [1_000, 2_000, 5_000] })
    .toBe('E2E contract 2');
  await expect
    .poll(() => sheetValue(page, doc, original?.tableInstanceId ?? 0), { timeout: 180_000, intervals: [1_000, 2_000, 5_000] })
    .toBe('E2E contract 1');

  await page.reload();
  await expect(page.getByRole('tab', { name: /E2E contract 2/ })).toHaveAttribute('aria-selected', 'true', { timeout: 60_000 });
  await expect(page.getByRole('gridcell', { name: /E2E contract 2/ })).toBeVisible({ timeout: 60_000 });
  await shot(page, '02-copy-sheetname-after-rename');
});

test('SHEETNAME: редактор виразів пропонує функцію в автодоповненні', async ({ page }) => {
  test.slow();
  await signIn(page);
  await page.goto('/admin/expressions');

  const editor = page.locator('.monaco-editor').first();
  await expect(editor).toBeVisible({ timeout: 60_000 });
  await editor.click();
  await page.keyboard.type('SHEE', { delay: 20 });
  await page.keyboard.press('Control+Space');

  const suggestions = page.locator('.suggest-widget .monaco-list-row');
  await expect(suggestions.filter({ hasText: 'SHEETNAME' }).first()).toBeVisible({ timeout: 30_000 });
  await shot(page, '03-editor-autocomplete-sheetname');

  // Підстановка: «SHEETNAME()» з курсором після дужок — вираз одразу валідний і текстовий.
  await page.keyboard.press('Enter');
  await expect(page.getByText(/No findings/i)).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText(/Result type: Text/i)).toBeVisible();
});
