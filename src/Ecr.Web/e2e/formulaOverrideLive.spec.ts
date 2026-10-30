import { expect, test, type APIResponse, type Locator, type Page } from '@playwright/test';
import { signIn } from './multiSheet';

/**
 * ✎ D-461 на живому стенді: «ручне значення замість формули» (сценарій людини 30.10).
 *
 * Шаблон — одна динамічна таблиця: дві колонки вводу (A, B) і дві колонки формул поруч — F1 = [A]*2
 * («Дозволити ручне значення» увімкнено ПІСЛЯ публікації: нова версія не потрібна) і F2 = [A]+[B] (без дозволу).
 * Документ — 5 рядків. Рядки 4–5: ручні значення в F1 з причиною → маркер і підказка «хто, коли, чому»; правка A
 * перераховує F2, а ручні F1 не перезаписує; Delete на рядку 5 повертає формулу; аркуш подається.
 *
 * E2EAdmin стенда має `Document.OverrideFormula` (`tools/e2e-stand.ps1`). Без стенда — пропуск.
 */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const Rows = 5;

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
    isReadOnly: false,
    isHidden: false,
    precision: 18,
    scale: 2,
    defaultValue: null,
    displayFormat: null,
    lookupFilter: null,
    lookupRegistryDefId: null,
    styleId: null,
    unitId: null,
  };
}

interface Slice {
  tableInstanceId: number;
  periodKey: number;
  columns: { code: string }[];
  rows: { rowKey: string; rowVersion: string; label: string | null; cells: Record<string, unknown> }[];
}

interface Arranged {
  documentId: number;
  periodKey: number;
  sheetDefId: number;
  tableInstanceId: number;
}

async function sliceOf(page: Page, arranged: Arranged): Promise<Slice> {
  return ok<Slice>(
    await page.request.get(`/api/v1/documents/${String(arranged.documentId)}/tables/${String(arranged.tableInstanceId)}`),
    'зріз',
  );
}

const asNumber = (value: unknown): number => (value === null || value === undefined ? Number.NaN : Number(value));

/** Шаблон із формулами, проєкт, документ, 5 рядків з A/B — через API (як `multiSheet.ts`). */
async function arrange(page: Page): Promise<Arranged> {
  const stamp = String(Date.now());
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2EFo${stamp}`, nameL10n: { en: `E2E formula override ${stamp}` } } }),
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
    await page.request.put(`${base}/sheets/S1`, {
      data: { nameL10n: { en: 'Sheet 1' }, ordinal: 1, isMandatory: true, isVisible: true, sheetGroup: null },
    }),
    'аркуш',
  );
  const table = await ok<{ id: number }>(
    await page.request.put(`${base}/sheets/S1/tables/T1`, {
      data: { nameL10n: { en: 'Readings' }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
    }),
    'таблиця',
  );
  const columns: [string, string][] = [
    ['A', 'Decimal'],
    ['B', 'Decimal'],
    ['F1', 'Formula'],
    ['F2', 'Formula'],
  ];
  for (const [index, [code, dataType]] of columns.entries()) {
    await ok(
      await page.request.put(`${base}/tables/${String(table.id)}/columns/${code}`, { data: column(index, code, dataType) }),
      `колонка ${code}`,
    );
  }

  const draft = await ok<{ sheets: { tables: { id: number; columns: { id: number; code: string }[] }[] }[] }>(
    await page.request.get(`${base}/structure`),
    'структура чернетки',
  );
  const idOf = (code: string): number =>
    draft.sheets[0]?.tables[0]?.columns.find((candidate) => candidate.code === code)?.id ?? 0;
  const f1 = await ok<{ id: number }>(
    await page.request.put(`${base}/tables/${String(table.id)}/formulas/column/${String(idOf('F1'))}`, {
      data: { dialect: 'Template', expression: '[A]*2' },
    }),
    'формула F1',
  );
  await ok(
    await page.request.put(`${base}/tables/${String(table.id)}/formulas/column/${String(idOf('F2'))}`, {
      data: { dialect: 'Template', expression: '[A]+[B]' },
    }),
    'формула F2',
  );
  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: D-461 ручне значення замість формули' } }), 'публікація');

  // ✎ D-461: прапорець — на ОПУБЛІКОВАНІЙ версії, без нової версії.
  const toggled = await ok<{ allowManualOverride: boolean; changed: boolean }>(
    await page.request.put(`${base}/formulas/${String(f1.id)}/manual-override`, { data: { allow: true, dryRun: false } }),
    'дозволити ручне значення F1',
  );
  expect(toggled.allowManualOverride).toBe(true);

  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2EFoPrj${stamp}`,
        nameL10n: { en: `E2E formula override ${stamp}` },
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
  const structure = await ok<{ sheets: { id: number }[] }>(await page.request.get(`${base}/structure`), 'структура');
  const sheetDefId = structure.sheets[0]?.id ?? 0;
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
  const tables = await ok<{ tableInstanceId: number }[]>(
    await page.request.get(`/api/v1/documents/${String(document.documentId)}/tables?periodKey=${String(periodKey)}`),
    'таблиці документа',
  );
  const arranged: Arranged = {
    documentId: document.documentId,
    periodKey,
    sheetDefId,
    tableInstanceId: tables[0]?.tableInstanceId ?? 0,
  };

  for (let n = 1; n <= Rows; n += 1) {
    await ok(
      await page.request.post(`/api/v1/documents/${String(arranged.documentId)}/rows`, {
        data: { tableInstanceId: arranged.tableInstanceId, rowKey: null },
      }),
      `рядок ${String(n)}`,
    );
  }

  const slice = await sliceOf(page, arranged);
  expect(slice.rows).toHaveLength(Rows);
  await ok(
    await page.request.patch(`/api/v1/documents/${String(arranged.documentId)}/cells`, {
      data: {
        tableInstanceId: arranged.tableInstanceId,
        periodKey,
        origin: 'UserEdit',
        rows: slice.rows.map((row, index) => ({
          rowKey: row.rowKey,
          baseVersion: row.rowVersion,
          cells: [
            { columnCode: 'A', value: String(index + 1), isEmpty: false },
            { columnCode: 'B', value: '10', isEmpty: false },
          ],
        })),
      },
    }),
    'значення A/B',
  );

  // Перерахунок асинхронний: F1 = A*2 у всіх рядках.
  await expect
    .poll(async () => (await sliceOf(page, arranged)).rows.map((row) => asNumber(row.cells['F1'])), { timeout: 60_000 })
    .toEqual([2, 4, 6, 8, 10]);

  return arranged;
}

const cellAt = (page: Page, col: number, row: number): Locator =>
  page.locator(`revo-grid revogr-data[type="rgRow"] .rgCell[data-rgcol="${String(col)}"][data-rgrow="${String(row)}"]`);

/** Індекс колонки сітки за кодом: колонка підписів рядків (коли в рядків є підписи) зсуває індекси на 1. */
function columnIndex(slice: Slice, code: string): number {
  const offset = slice.rows.some((row) => row.label !== null && row.label.length > 0) ? 1 : 0;
  const index = slice.columns.findIndex((candidate) => candidate.code === code);
  expect(index, `колонка ${code}`).toBeGreaterThanOrEqual(0);

  return index + offset;
}

/** Число в комірці сітки (формат показу — мовою інтерфейсу; масштаб `.00` не важливий). */
const shows = (value: number): RegExp => new RegExp(`^\\s*${String(value)}([.,]0+)?\\s*$`);

async function enterManual(page: Page, col: number, row: number, value: string, reason: string): Promise<void> {
  await cellAt(page, col, row).click();
  await page.waitForTimeout(200);
  await page.keyboard.type(value);
  await page.keyboard.press('Enter');

  const field = page.locator('textarea[data-formula-override-reason]');
  await expect(field, 'діалог причини').toBeVisible({ timeout: 10_000 });
  const save = page.locator('[data-formula-override-save]');
  await expect(save, 'без причини зберегти не можна').toBeDisabled();
  await field.fill(reason);
  await save.click();
  await expect(field).toBeHidden();
}

test.describe('D-461 formula override', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('D-461 formula override: manual values with reason, marker, recalculation keeps them, Delete restores, submit', async ({ page }) => {
    test.slow();
    await signIn(page);
    const arranged = await arrange(page);

    await page.goto(`/documents/${String(arranged.documentId)}?periodKey=${String(arranged.periodKey)}`);
    await expect(page.locator('revo-grid:visible').first()).toBeVisible({ timeout: 60_000 });
    const initial = await sliceOf(page, arranged);
    const colA = columnIndex(initial, 'A');
    const colF1 = columnIndex(initial, 'F1');
    const colF2 = columnIndex(initial, 'F2');
    await expect(cellAt(page, colF1, 4)).toHaveText(shows(10), { timeout: 30_000 });

    // F1 — обчислена, але її можна замінити: знак формули й пояснення словами; F2 — без дозволу.
    await expect(cellAt(page, colF1, 0)).toHaveAttribute('data-cell-state', 'calculated');
    await expect(cellAt(page, colF1, 0)).toHaveAttribute('title', /manual value/i);

    // Рядки 4–5 (індекси 3, 4): ручні значення з причиною.
    await enterManual(page, colF1, 3, '100', 'Meter replaced on site');
    await expect(cellAt(page, colF1, 3)).toHaveAttribute('data-formula-manual', 'true', { timeout: 15_000 });
    await expect
      .poll(async () => asNumber((await sliceOf(page, arranged)).rows[3]?.cells['F1']), { timeout: 30_000 })
      .toBe(100);

    // Ctrl+Z: заміну скасовано — формула повернулась (4 × 2 = 8), маркера немає; Ctrl+Y — знову ручне з тією
    // самою причиною.
    await cellAt(page, colA, 0).click();
    await page.keyboard.press('Control+z');
    await expect
      .poll(async () => asNumber((await sliceOf(page, arranged)).rows[3]?.cells['F1']), { timeout: 60_000 })
      .toBe(8);
    await expect(cellAt(page, colF1, 3)).toHaveText(shows(8), { timeout: 30_000 });
    await expect(cellAt(page, colF1, 3)).not.toHaveAttribute('data-formula-manual', 'true');

    await page.keyboard.press('Control+y');
    await expect
      .poll(async () => asNumber((await sliceOf(page, arranged)).rows[3]?.cells['F1']), { timeout: 60_000 })
      .toBe(100);
    await expect(cellAt(page, colF1, 3)).toHaveAttribute('title', /Manual value · Meter replaced on site · /, {
      timeout: 15_000,
    });

    await enterManual(page, colF1, 4, '200', 'Estimated by engineer');

    for (const [row, reason] of [
      [3, 'Meter replaced on site'],
      [4, 'Estimated by engineer'],
    ] as const) {
      const cell = cellAt(page, colF1, row);
      await expect(cell).toHaveAttribute('data-formula-manual', 'true', { timeout: 15_000 });
      await expect(cell).toHaveAttribute('title', new RegExp(`Manual value · ${reason} · `));
    }
    // Колонки формул поруч: маркер — лише на ручних комірках, сусідня F2 тих самих рядків — без нього.
    await expect(cellAt(page, colF2, 3)).not.toHaveAttribute('data-formula-manual', 'true');
    await expect(cellAt(page, colF1, 2)).not.toHaveAttribute('data-formula-manual', 'true');

    // Сервер записав саме ручні значення.
    await expect
      .poll(async () => (await sliceOf(page, arranged)).rows.map((row) => asNumber(row.cells['F1'])), { timeout: 30_000 })
      .toEqual([2, 4, 6, 100, 200]);

    // Фільтр «Лише рядки з ручними значеннями»: лічильник 2, у сітці лише рядки 4–5; вимкнення — знову 5.
    const toggle = page.locator('[data-manual-only-toggle]');
    await expect(page.locator('[data-manual-count]')).toHaveAttribute('data-manual-count', '2');
    const renderedRows = page.locator(`revo-grid revogr-data[type="rgRow"] .rgCell[data-rgcol="${String(colA)}"]`);
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'true');
    await expect(renderedRows).toHaveCount(2);
    await expect(cellAt(page, colF1, 0)).toHaveText(shows(100));
    await expect(cellAt(page, colF1, 1)).toHaveText(shows(200));
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-pressed', 'false');
    await expect(renderedRows).toHaveCount(Rows);

    // Правка A в рядку 4: F2 перераховується, ручне F1 — ні.
    await cellAt(page, colA, 3).click();
    await page.waitForTimeout(200);
    await page.keyboard.type('50');
    await page.keyboard.press('Enter');
    await expect
      .poll(async () => {
        const row = (await sliceOf(page, arranged)).rows[3];
        return [asNumber(row?.cells['F1']), asNumber(row?.cells['F2'])];
      }, { timeout: 60_000 })
      .toEqual([100, 60]);
    await expect(cellAt(page, colF2, 3)).toHaveText(shows(60), { timeout: 30_000 });
    await expect(cellAt(page, colF1, 3)).toHaveText(shows(100));

    // Delete на рядку 5 — формула повертається (без причини): 5 × 2 = 10.
    await cellAt(page, colF1, 4).click();
    await page.waitForTimeout(200);
    await page.keyboard.press('Delete');
    await expect(page.locator('textarea[data-formula-override-reason]')).toHaveCount(0);
    await expect
      .poll(async () => asNumber((await sliceOf(page, arranged)).rows[4]?.cells['F1']), { timeout: 60_000 })
      .toBe(10);
    await expect(cellAt(page, colF1, 4)).toHaveText(shows(10), { timeout: 30_000 });
    await expect(cellAt(page, colF1, 4)).not.toHaveAttribute('data-formula-manual', 'true');
    await expect(cellAt(page, colF1, 3)).toHaveAttribute('data-formula-manual', 'true');

    // Аркуш подається з ручним значенням у рядку 4.
    const submit = await page.request.post(`/api/v1/documents/${String(arranged.documentId)}/submit`, {
      data: { sheetDefId: arranged.sheetDefId, periodKey: arranged.periodKey, acknowledgeWarnings: true },
    });
    expect(submit.status(), `submit: ${await submit.text()}`).toBe(204);
  });
});
