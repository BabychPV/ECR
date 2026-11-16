import { expect, test, type Browser, type BrowserContext } from '@playwright/test';

/**
 * B139 (B75 lane 2) проти РЕАЛЬНОГО сервера стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep rowLock`):
 * блокування рядків документа в сітці.
 *
 *  - `e2e-admin` (роль E2EAdmin має `Document.RowLock`) ставить курсор у рядок, відкриває «Lock rows…», задає дату
 *    «по» і блокує: `POST …/row-locks` 200, у першій комірці рядка — значок замка, у рядку кнопок — «Locked rows: 1»;
 *  - `e2e-operator` (Write на проєкт, без права) бачить той самий значок, кнопки «Lock rows…» у нього немає, а
 *    правка комірки заблокованого рядка — 403 з причиною `RowLocked`;
 *  - `e2e-admin` знімає блокування з переліку: `DELETE …/row-locks/{id}` 204, значок зникає, оператор знову править;
 *  - ✎ B143: `e2e-operator` як ВЛАСНИК власного документа (без права) бачить «Lock rows…» і блокує рядок — другий тест.
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

interface DocumentCard {
  projectId: number;
  ownerUserId: number | null;
  canLockRows?: boolean | null;
}

async function signedIn(browser: Browser, user: { userName: string; password: string }): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', {
    data: { userName: user.userName, password: user.password },
  });
  expect(login.ok(), `вхід ${user.userName}: ${String(login.status())} ${await login.text()}`).toBe(true);

  return context;
}

async function sliceOf(context: BrowserContext, tableInstanceId: number, documentId: string = DocumentId): Promise<Slice> {
  const response = await context.request.get(
    `/api/v1/documents/${documentId}/tables/${String(tableInstanceId)}?periodKey=${PeriodKey}`,
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

async function releaseAll(context: BrowserContext, documentId: string = DocumentId): Promise<void> {
  const list = await context.request.get(`/api/v1/documents/${documentId}/row-locks?periodKey=${PeriodKey}`);
  if (!list.ok()) return;
  for (const lock of (await list.json()) as RowLock[]) {
    await context.request.delete(`/api/v1/documents/${documentId}/row-locks/${String(lock.id)}`);
  }
}

/** Картка документа за період стенда — те саме `GET`, яке робить сторінка документа (B143: `canLockRows`). */
async function cardOf(context: BrowserContext, documentId: string): Promise<DocumentCard> {
  const response = await context.request.get(`/api/v1/documents/${documentId}?periodKey=${PeriodKey}`);
  expect(response.ok(), `GET document ${documentId}: ${String(response.status())} ${await response.text()}`).toBe(true);

  return (await response.json()) as DocumentCard;
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

  /*
   * ✎ B143 (D-538): ВЛАСНИК документа без права `Document.RowLock` блокує рядки свого документа.
   *
   * `e2e-operator` (роль E2EOperator: `Document.Create`, Write на проєкт 1, права `Document.RowLock` немає) документом
   * стенда не володіє (його заводить генератор до появи користувачів стенда), тому тест заводить ВЛАСНИЙ документ
   * оператора в тому самому проєкті через API (нових облікових записів і ролей немає):
   *  - картка власного документа: `canLockRows = true`; картка документа стенда: `false` і кнопки немає;
   *  - у власному документі «Lock rows…» видно, блокування через діалог — `POST …/row-locks` 200.
   * Прибирання (`finally`): блокування зняті, документ видаляє `e2e-admin` (`Document.Delete`, чернетка).
   *
   * ⚠ Написано без локального прогону (правило «e2e — на CI»): перший живий прогін — CI `e2e-stand`.
   */
  test('B143: власник без права Document.RowLock блокує рядок свого документа; у чужому документі кнопки немає', async ({
    browser,
  }) => {
    const admin = await signedIn(browser, Admin);
    const operator = await signedIn(browser, Operator);
    let ownDocumentId = '';

    try {
      // ── 0. Оператор: права немає, документом стенда не володіє, сервер там блокувати не дає ─────
      const meResponse = await operator.request.get('/api/v1/me');
      expect(meResponse.ok(), `GET /me: ${String(meResponse.status())}`).toBe(true);
      const me = (await meResponse.json()) as { userId: number; permissions: string[] };
      expect(me.permissions, 'e2e-operator отримав Document.RowLock — тест власника втратив сенс').not.toContain('Document.RowLock');

      const standCard = await cardOf(operator, DocumentId);
      expect(standCard.ownerUserId, 'e2e-operator — власник документа стенда: «чужого» документа для порівняння немає').not.toBe(
        me.userId,
      );
      expect(standCard.canLockRows, 'картка чужого документа: canLockRows').toBe(false);

      // ── 1. Власний документ оператора в проєкті стенда (той самий склад аркушів) ──────────────
      const standTables = await operator.request.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${PeriodKey}`);
      expect(standTables.ok(), `GET tables: ${String(standTables.status())}`).toBe(true);
      const sheetDefIds = [...new Set(((await standTables.json()) as DocumentTable[]).map((table) => table.sheetDefId))];
      const createdDocument = await operator.request.post('/api/v1/documents', {
        data: { projectId: standCard.projectId, sheetDefIds },
      });
      expect(createdDocument.ok(), `POST documents (e2e-operator): ${String(createdDocument.status())} ${await createdDocument.text()}`).toBe(
        true,
      );
      ownDocumentId = String(((await createdDocument.json()) as { documentId: number }).documentId);

      const ownCard = await cardOf(operator, ownDocumentId);
      expect(ownCard.ownerUserId, 'власник нового документа — не e2e-operator').toBe(me.userId);
      expect(ownCard.canLockRows, 'картка власного документа: canLockRows').toBe(true);

      // Рядок для блокування: у новому документі динамічна таблиця порожня — оператор додає рядок.
      const ownTables = await operator.request.get(`/api/v1/documents/${ownDocumentId}/tables?periodKey=${PeriodKey}`);
      expect(ownTables.ok(), `GET tables власного документа: ${String(ownTables.status())}`).toBe(true);
      const table = ((await ownTables.json()) as DocumentTable[])[0];
      if (table === undefined) throw new Error('у власного документа немає таблиць');
      let rows = (await sliceOf(operator, table.tableInstanceId, ownDocumentId)).rows;
      if (rows.length === 0) {
        const added = await operator.request.post(`/api/v1/documents/${ownDocumentId}/rows`, {
          data: { tableInstanceId: table.tableInstanceId, rowKey: null },
        });
        expect(added.ok(), `POST rows: ${String(added.status())} ${await added.text()}`).toBe(true);
        rows = (await sliceOf(operator, table.tableInstanceId, ownDocumentId)).rows;
      }
      expect(rows.length, 'у першій таблиці власного документа немає рядків').toBeGreaterThan(0);

      // ── 2. Власник без права бачить «Lock rows…» і блокує рядок через діалог ──────────────────
      const page = await operator.newPage();
      await page.goto(`/documents/${ownDocumentId}?periodKey=${PeriodKey}`);
      const grid = page.locator('revo-grid').first();
      const firstCell = grid.locator('revogr-data .rgCell').first();
      await expect(firstCell, 'рядки сітки власного документа не завантажилися').toBeVisible({ timeout: 60_000 });

      const lockButton = page.locator('[data-row-lock-open]').first();
      await expect(lockButton, 'кнопки «Lock rows…» немає у власника документа без права Document.RowLock').toBeVisible({
        timeout: 30_000,
      });
      await firstCell.click();
      await expect(lockButton).toBeEnabled({ timeout: 15_000 });
      await lockButton.click();

      const dialog = page.getByRole('dialog');
      await expect(dialog).toBeVisible({ timeout: 30_000 });
      const save = dialog.locator('[data-row-lock-save]');
      const until = dialog.getByRole('textbox', { name: /^To/ });
      await until.fill(dayFromNow(7));
      await until.press('Tab');
      await expect(save).toBeEnabled({ timeout: 15_000 });

      const created = page.waitForResponse(
        (response) =>
          response.request().method() === 'POST' &&
          new URL(response.url()).pathname === `/api/v1/documents/${ownDocumentId}/row-locks`,
        { timeout: 60_000 },
      );
      await save.click();
      const createdResponse = await created;
      expect(createdResponse.status(), `POST row-locks (власник без права): ${await createdResponse.text()}`).toBe(200);
      const lock = ((await createdResponse.json()) as RowLock[])[0];
      if (lock === undefined) throw new Error('POST row-locks не повернув блокування');
      expect(rows.map((row) => row.rowKey), 'заблоковано рядок не цієї таблиці').toContain(lock.rowKey);

      await expect(dialog).toBeHidden({ timeout: 30_000 });
      await expect(grid.locator(`[data-row-lock-badge="${String(lock.id)}"]`), 'значка замка у власника немає').toBeVisible({
        timeout: 30_000,
      });

      // ── 3. У чужому документі (стенда) той самий оператор кнопки не має ────────────────────────
      await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
      await expect(page.locator('revo-grid').first().locator('revogr-data .rgCell').first(), 'сітка документа стенда').toBeVisible({
        timeout: 60_000,
      });
      // Права в оператора немає, тож кнопки немає і до картки (запасне правило), і з нею (`canLockRows = false`, крок 0).
      await expect(page.locator('[data-row-lock-open]')).toHaveCount(0);
      await page.close();
    } finally {
      try {
        if (ownDocumentId !== '') {
          await releaseAll(admin, ownDocumentId);
          const removed = await admin.request.delete(`/api/v1/documents/${ownDocumentId}`);
          // `soft`: відмова прибирання не має перекривати причину падіння самого тесту.
          expect.soft(removed.ok(), `DELETE власного документа оператора: ${String(removed.status())} ${await removed.text()}`).toBe(true);
        }
      } finally {
        await admin.close();
        await operator.close();
      }
    }
  });
});
