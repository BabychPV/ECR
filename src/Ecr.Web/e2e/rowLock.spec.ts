import { expect, test, type Browser, type BrowserContext } from '@playwright/test';

/**
 * B139 (B75 lane 2) проти РЕАЛЬНОГО сервера стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep rowLock`):
 * блокування рядків документа в сітці.
 *
 *  - `e2e-admin` (роль E2EAdmin має `Document.RowLock`) ставить курсор у рядок, відкриває «Lock rows…», задає дату
 *    «по» і блокує: `POST …/row-locks` 200, у першій комірці рядка — значок замка, у рядку кнопок — «Locked rows: 1»;
 *  - `e2e-operator` (Write на проєкт, без права) бачить той самий значок, кнопки «Lock rows…» у нього немає, а
 *    правка комірки заблокованого рядка — 403 з причиною `RowLocked`;
 *  - `e2e-admin` знімає блокування з переліку: `DELETE …/row-locks/{id}` 204, значок зникає, оператор знову править.
 *
 * ⛔ Без стенда файл ПАДАЄ (`beforeAll`), а не пропускається (honesty-guard, `globalSetup.ts`).
 *
 * ⚠ `afterAll` знімає всі блокування документа за період (базова лінія для решти файлів): незнятий замок робив би
 * рядок 0 нередагованим для `e2e-operator` в інших специфікаціях.
 *
 * ⚠ Режим «без подання» (`workflowMode = None`) тут не перевіряється: він задається на ЧЕРНЕТЦІ версії шаблону до
 * публікації, а документ стенда вже на опублікованій версії; приховування подання покриває vitest
 * (`SheetActions.workflowModeNone.test.tsx`), на живому шаблоні — вікно B140 (HSE401).
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Operator = { userName: 'e2e-operator', password: 'E2E-Oper8tor-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

interface DocumentTable {
  sheetDefId: number;
  tableInstanceId: number;
}

interface Slice {
  tableInstanceId: number;
  periodKey: number;
  columns: { code: string }[];
  rows: { rowKey: string; rowVersion: string }[];
}

interface RowLock {
  id: number;
  rowKey: string;
  isActive: boolean;
}

async function signedIn(browser: Browser, user: { userName: string; password: string }): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', {
    data: { userName: user.userName, password: user.password },
  });
  expect(login.ok(), `вхід ${user.userName}: ${String(login.status())} ${await login.text()}`).toBe(true);

  return context;
}

async function sliceOf(context: BrowserContext, tableInstanceId: number): Promise<Slice> {
  const response = await context.request.get(
    `/api/v1/documents/${DocumentId}/tables/${String(tableInstanceId)}?periodKey=${PeriodKey}`,
  );
  expect(response.ok(), `GET slice: ${String(response.status())}`).toBe(true);

  return (await response.json()) as Slice;
}

async function patchCell(context: BrowserContext, tableInstanceId: number, rowKey: string, column: string, value: number) {
  const slice = await sliceOf(context, tableInstanceId);
  const row = slice.rows.find((candidate) => candidate.rowKey === rowKey);

  return context.request.patch(`/api/v1/documents/${DocumentId}/cells`, {
    data: {
      tableInstanceId: slice.tableInstanceId,
      periodKey: slice.periodKey,
      origin: 'UserEdit',
      rows: [{ rowKey, baseVersion: row?.rowVersion ?? null, cells: [{ columnCode: column, value }] }],
    },
  });
}

async function releaseAll(context: BrowserContext): Promise<void> {
  const list = await context.request.get(`/api/v1/documents/${DocumentId}/row-locks?periodKey=${PeriodKey}`);
  if (!list.ok()) return;
  for (const lock of (await list.json()) as RowLock[]) {
    await context.request.delete(`/api/v1/documents/${DocumentId}/row-locks/${String(lock.id)}`);
  }
}

/** Дата `YYYY-MM-DD` через `days` днів від сьогодні (UTC; запас у тиждень перекриває різницю поясу проєкту). */
function dayFromNow(days: number): string {
  return new Date(Date.now() + days * 86_400_000).toISOString().slice(0, 10);
}

test.describe('rowLock: блокування рядків документа на реальному стенді (B139, B75)', () => {
  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
  });

  test.describe.configure({ mode: 'serial', timeout: 240_000 });

  test.afterAll(async ({ browser }) => {
    if (PeriodKey === '' || DocumentId === '') return;
    const admin = await signedIn(browser, Admin);
    try {
      await releaseAll(admin);
    } finally {
      await admin.close();
    }
  });

  test('власник блокує рядок із сітки: значок, інший користувач не править; зняття з переліку повертає правку', async ({
    browser,
  }) => {
    const admin = await signedIn(browser, Admin);
    const operator = await signedIn(browser, Operator);

    try {
      // ── 0. Базова лінія: без блокувань; рядок 0 і колонка, яку оператор править ────────────────
      await releaseAll(admin);
      const tablesResponse = await admin.request.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${PeriodKey}`);
      expect(tablesResponse.ok(), `GET tables: ${String(tablesResponse.status())}`).toBe(true);
      const table = ((await tablesResponse.json()) as DocumentTable[])[0];
      if (table === undefined) throw new Error('у документа стенда немає таблиць');

      const draft = await sliceOf(operator, table.tableInstanceId);
      const row = draft.rows[0];
      if (row === undefined) throw new Error('у зрізі немає рядків');
      let column = '';
      for (const candidate of draft.columns.slice(0, 12)) {
        if ((await patchCell(operator, table.tableInstanceId, row.rowKey, candidate.code, 139)).ok()) {
          column = candidate.code;
          break;
        }
      }
      expect(column, 'жодна з перших колонок рядка 0 не прийняла значення оператора (PATCH …/cells)').not.toBe('');

      // ── 1. Власник блокує рядок 0 із сітки ───────────────────────────────────────────────────
      const page = await admin.newPage();
      await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
      const grid = page.locator('revo-grid').first();
      const firstCell = grid.locator('revogr-data .rgCell').first();
      await expect(firstCell, 'рядки сітки не завантажилися').toBeVisible({ timeout: 60_000 });

      const lockButton = page.locator('[data-row-lock-open]').first();
      await expect(lockButton, 'кнопки «Lock rows…» немає у власника з правом Document.RowLock').toBeVisible({ timeout: 30_000 });
      // Без рядка у фокусі кнопка вимкнена; клік у комірку рядка 0 її вмикає.
      await firstCell.click();
      await expect(lockButton).toBeEnabled({ timeout: 15_000 });
      await lockButton.click();

      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible({ timeout: 30_000 });
      const save = dialog.locator('[data-row-lock-save]');
      await expect(save, '«Lock» доступна без дати «по»').toBeDisabled();
      const until = dialog.getByRole('textbox', { name: /^To/ });
      await until.fill(dayFromNow(7));
      await until.press('Tab');
      await expect(save).toBeEnabled({ timeout: 15_000 });

      const created = page.waitForResponse(
        (response) =>
          response.request().method() === 'POST' && new URL(response.url()).pathname === `/api/v1/documents/${DocumentId}/row-locks`,
        { timeout: 60_000 },
      );
      await save.click();
      const createdResponse = await created;
      expect(createdResponse.status(), `POST row-locks: ${await createdResponse.text()}`).toBe(200);
      const lock = ((await createdResponse.json()) as RowLock[])[0];
      if (lock === undefined) throw new Error('POST row-locks не повернув блокування');
      expect(lock.rowKey, 'заблоковано не рядок 0').toBe(row.rowKey);
      expect(lock.isActive, 'блокування від сьогодні не чинне').toBe(true);

      await expect(dialog).toBeHidden({ timeout: 30_000 });
      const badge = grid.locator(`[data-row-lock-badge="${String(lock.id)}"]`);
      await expect(badge, 'значка замка в заблокованому рядку немає').toBeVisible({ timeout: 30_000 });
      await expect(badge).toHaveAttribute('aria-label', /e2e-admin|locked/i);
      const listButton = page.locator('[data-row-locks-open]').first();
      await expect(listButton).toHaveAttribute('data-row-locks-count', '1', { timeout: 30_000 });

      // ── 2. Інший користувач: значок бачить, кнопки блокування немає, правка — 403 RowLocked ───
      const denied = await patchCell(operator, table.tableInstanceId, row.rowKey, column, 999);
      expect(denied.status(), `PATCH оператора в заблокований рядок: ${await denied.text()}`).toBe(403);
      expect(JSON.stringify(await denied.json())).toContain('RowLocked');

      const operatorPage = await operator.newPage();
      await operatorPage.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
      const operatorGrid = operatorPage.locator('revo-grid').first();
      await expect(operatorGrid.locator(`[data-row-lock-badge="${String(lock.id)}"]`), 'оператор не бачить значка').toBeVisible({
        timeout: 60_000,
      });
      await expect(operatorPage.locator('[data-row-lock-open]')).toHaveCount(0);
      await operatorPage.close();

      // ── 3. Власник знімає блокування з переліку ──────────────────────────────────────────────
      await listButton.click();
      const panel = page.getByRole('dialog');
      await expect(panel).toBeVisible({ timeout: 30_000 });
      const released = page.waitForResponse(
        (response) =>
          response.request().method() === 'DELETE' &&
          new URL(response.url()).pathname === `/api/v1/documents/${DocumentId}/row-locks/${String(lock.id)}`,
        { timeout: 60_000 },
      );
      await panel.locator(`[data-row-lock-item="${String(lock.id)}"]`).getByRole('button').click();
      expect((await released).status()).toBe(204);

      await page.keyboard.press('Escape');
      await expect(badge, 'значок лишився після зняття блокування').toHaveCount(0, { timeout: 30_000 });
      await expect(page.locator('[data-row-locks-open]')).toHaveCount(0);

      const allowed = await patchCell(operator, table.tableInstanceId, row.rowKey, column, 140);
      expect(allowed.ok(), `PATCH оператора після зняття: ${String(allowed.status())} ${await allowed.text()}`).toBe(true);
    } finally {
      await admin.close();
      await operator.close();
    }
  });
});
