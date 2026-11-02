import { expect, test, type APIRequestContext, type Browser, type BrowserContext } from '@playwright/test';

/**
 * Number format: числова комірка БЕЗ формату показується найкоротшим точним записом — `3`, а не
 * `3.0000000000000000` (баг людини 02.11, документ 8 «Stationary Equipment», 202609).
 *
 * Причина: сервер віддає `decimal` рядком у масштабі сховища, а колонки імпорту мають `scale` 16/12 —
 * точність сховища, яку показ доповнював нулями як формат комірки. Тепер доповнення — лише до
 * `MaxPaddedScale` (`shared/format/number.ts`).
 *
 * Стенд: `tools/e2e-stand.ps1 -Grep "Number format"`. ⛔ Без стенда файл ПАДАЄ (`beforeAll`), не
 * пропускається (honesty-guard). `afterAll` повертає попереднє значення комірки.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

/** Найбільший масштаб-формат (`MaxPaddedScale` клієнта). Більший — точність сховища. */
const MaxPaddedScale = 6;

interface DocumentTable {
  sheetDefId: number;
  sheetCode: string;
  tableInstanceId: number;
}

interface SliceColumn {
  code: string;
  header: string;
  dataType: string;
  isReadOnly: boolean;
  scale?: number | null;
}

interface Slice {
  tableInstanceId: number;
  periodKey: number;
  columns: SliceColumn[];
  rows: { rowKey: string; rowVersion: string; cells: Record<string, unknown> }[];
}

interface Target {
  table: DocumentTable;
  column: SliceColumn;
  rowKey: string;
  previous: unknown;
}

async function signedIn(browser: Browser): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', { data: Admin });
  expect(login.ok(), `вхід: ${String(login.status())} ${await login.text()}`).toBe(true);

  return context;
}

async function sliceOf(request: APIRequestContext, tableInstanceId: number): Promise<Slice> {
  const response = await request.get(
    `/api/v1/documents/${DocumentId}/tables/${String(tableInstanceId)}?periodKey=${PeriodKey}`,
  );
  expect(response.ok(), `GET slice: ${String(response.status())} ${await response.text()}`).toBe(true);

  return (await response.json()) as Slice;
}

async function patchCell(request: APIRequestContext, slice: Slice, rowKey: string, code: string, value: unknown) {
  const row = slice.rows.find((r) => r.rowKey === rowKey);
  if (row === undefined) throw new Error(`рядка ${rowKey} немає`);

  return request.patch(`/api/v1/documents/${DocumentId}/cells`, {
    data: {
      tableInstanceId: slice.tableInstanceId,
      periodKey: slice.periodKey,
      origin: 'UserEdit',
      rows: [{ rowKey, baseVersion: row.rowVersion, cells: [{ columnCode: code, value }] }],
    },
  });
}

/** Колонка без формату: `Decimal`, редагована, масштаб відсутній або більший за формат (першими — більші). */
function unformatted(slice: Slice): SliceColumn[] {
  return slice.columns
    .filter((c) => c.dataType === 'Decimal' && !c.isReadOnly && (c.scale == null || c.scale > MaxPaddedScale))
    .sort((a, b) => (b.scale ?? -1) - (a.scale ?? -1));
}

test.describe('Number format: числа без формату — без хвостових нулів сховища', () => {
  test.describe.configure({ mode: 'serial', timeout: 240_000 });

  let target: Target | null = null;

  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
  });

  test.afterAll(async ({ browser }) => {
    if (target === null) return;
    const admin = await signedIn(browser);
    try {
      const slice = await sliceOf(admin.request, target.table.tableInstanceId);
      const restored = await patchCell(admin.request, slice, target.rowKey, target.column.code, target.previous ?? null);
      expect(restored.ok(), `відновлення: ${String(restored.status())} ${await restored.text()}`).toBe(true);
    } finally {
      await admin.close();
    }
  });

  test('Number format: комірка й підсумок показують «3», не «3.0000…»', async ({ browser }) => {
    const admin = await signedIn(browser);

    try {
      const tablesResponse = await admin.request.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${PeriodKey}`);
      expect(tablesResponse.ok()).toBe(true);
      const tables = (await tablesResponse.json()) as DocumentTable[];

      // ── 1. Записати 3 у першу колонку без формату, яка приймає значення ─────────────────────
      for (const table of tables) {
        if (target !== null) break;
        const slice = await sliceOf(admin.request, table.tableInstanceId);
        const row = slice.rows[0];
        if (row === undefined) continue;
        for (const column of unformatted(slice)) {
          const patch = await patchCell(admin.request, slice, row.rowKey, column.code, 3);
          if (patch.ok()) {
            target = { table, column, rowKey: row.rowKey, previous: row.cells[column.code] ?? null };
            break;
          }
        }
      }
      if (target === null) throw new Error('у документа стенда немає Decimal-колонки без формату, що приймає значення');
      const { table, column, rowKey } = target;

      // Корінь: сервер віддає рядок у масштабі сховища — показ мусить зрізати нулі сам.
      const after = await sliceOf(admin.request, table.tableInstanceId);
      const stored = after.rows.find((r) => r.rowKey === rowKey)?.cells[column.code];
      expect(typeof stored, `значення ${column.code} після запису: ${JSON.stringify(stored)}`).toBe('string');
      test.info().annotations.push({ type: 'stored', description: `${column.code} scale=${String(column.scale)} → ${String(stored)}` });

      // ── 2. UI: сітка аркуша ──────────────────────────────────────────────────────────────
      const page = await admin.newPage();
      await page.setViewportSize({ width: 1440, height: 900 });
      await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}&sheet=${encodeURIComponent(table.sheetCode)}`);
      await expect(page.locator('revo-grid').first()).toBeVisible({ timeout: 60_000 });

      const header = page.locator('revo-grid revogr-header .rgHeaderCell', { hasText: column.header }).first();
      await header.scrollIntoViewIfNeeded();
      await expect(header, `заголовка «${column.header}» немає в сітці`).toBeVisible({ timeout: 60_000 });
      const index = await header.getAttribute('data-rgcol');
      if (index === null) throw new Error(`заголовок «${column.header}» без data-rgcol`);

      const cell = page.locator(`revo-grid revogr-data[type="rgRow"] .rgCell[data-rgrow="0"][data-rgcol="${index}"]`).first();
      await expect(cell).toHaveText('3', { timeout: 30_000 });
      await expect(cell).not.toHaveText(/3[.,]0{3,}/);

      // Рядок підсумків — якщо таблиця його має: той самий шаблон, без нулів сховища.
      const total = page.locator(`revo-grid revogr-data[type="rowPinEnd"] .rgCell[data-rgcol="${index}"]`);
      if ((await total.count()) > 0) {
        await expect(total.first()).not.toHaveText(/[.,]0{7,}$/);
      }

      await page.screenshot({ path: test.info().outputPath('number-format-grid.png') });
      await page.close();
    } finally {
      await admin.close();
    }
  });
});
