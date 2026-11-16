import { expect, test, type APIResponse, type Browser, type BrowserContext } from '@playwright/test';

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
/** B152: власник документа стенда, якого виставляє `tools/e2e-stand.ps1` (`e2e-admin`), — базова лінія всіх файлів. */
const StandOwner = Number(process.env['ECR_E2E_DOCUMENT_OWNER'] ?? '0');

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

/**
 * Повертає власність документа стенда ТОЧНО початковому власникові (B143, B152). Початкового власника виставляє
 * `tools/e2e-stand.ps1` — `e2e-admin` (Write на проєкт є), тож запасного «тоді власником стає e2e-admin» більше немає:
 * відмова сервера тут означає зламану базову лінію і має валити файл, а не ховатися.
 */
async function restoreOwner(admin: BrowserContext, originalOwner: number): Promise<APIResponse> {
  return admin.request.post(`/api/v1/documents/${DocumentId}/owner`, { data: { newOwnerUserId: originalOwner } });
}

/** Дата `YYYY-MM-DD` через `days` днів від сьогодні (UTC; запас у тиждень перекриває різницю поясу проєкту). */
function dayFromNow(days: number): string {
  return new Date(Date.now() + days * 86_400_000).toISOString().slice(0, 10);
}

test.describe('rowLock: блокування рядків документа на реальному стенді (B139, B75)', () => {
  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '' || !(StandOwner > 0)) {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT/ECR_E2E_DOCUMENT_OWNER (tools/e2e-stand.ps1)');
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
   * `e2e-operator` (Write на проєкт 1, права `Document.RowLock` немає) документом стенда не володіє. Тест на свій час
   * передає йому власність документа стенда (`POST …/owner`, як `documentParticipantsLive`) і повертає її у `finally`:
   *  - до передачі картка оператора: `canLockRows = false`, кнопки «Lock rows…» немає;
   *  - власником: `canLockRows = true`, кнопку видно, блокування через діалог — `POST …/row-locks` 200, значок є;
   *  - після повернення власності: знову `false` і без кнопки.
   * Нових облікових записів, ролей і документів немає.
   *
   * ⚠ Перша версія заводила оператору ВЛАСНИЙ документ через API — локальний стенд показав, що так не можна: таблиці
   * шаблону стенда (`Ecr.DataGen`) мають `RowMode = Fixed` без рядків шаблону (рядки документа стенда вставляє
   * генератор), тож у новому документі рядків немає і додати їх не можна (`409 ECR-ROW-0409`).
   */
  test('B143: власник без права Document.RowLock блокує рядок свого документа; не власником кнопки немає', async ({
    browser,
  }) => {
    const admin = await signedIn(browser, Admin);
    const operator = await signedIn(browser, Operator);
    let originalOwner = 0;
    let moved = false;

    try {
      // ── 0. Оператор: права немає, документом стенда не володіє, сервер блокувати не дає ───────
      await releaseAll(admin);
      const meResponse = await operator.request.get('/api/v1/me');
      expect(meResponse.ok(), `GET /me: ${String(meResponse.status())}`).toBe(true);
      const me = (await meResponse.json()) as { userId: number; permissions: string[] };
      expect(me.permissions, 'e2e-operator отримав Document.RowLock — тест власника втратив сенс').not.toContain('Document.RowLock');

      const before = await cardOf(operator, DocumentId);
      // B152: базова лінія — власник, якого виставив стенд; чужий залишок ловиться тут, а не трьома файлами пізніше.
      expect(before.ownerUserId, 'власник документа стенда не той, що виставив e2e-stand.ps1: залишок іншої специфікації').toBe(
        StandOwner,
      );
      originalOwner = StandOwner;
      expect(originalOwner, 'e2e-operator уже власник документа стенда: порівнювати ні з чим').not.toBe(me.userId);
      expect(before.canLockRows, 'картка не власника без права: canLockRows').toBe(false);

      const page = await operator.newPage();
      await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
      const grid = page.locator('revo-grid').first();
      const firstCell = grid.locator('revogr-data .rgCell').first();
      await expect(firstCell, 'рядки сітки не завантажилися').toBeVisible({ timeout: 60_000 });
      await expect(page.locator('[data-row-lock-open]'), 'не власник без права бачить «Lock rows…»').toHaveCount(0);

      // ── 1. Власність документа стенда — операторові ───────────────────────────────────────────
      const transfer = await admin.request.post(`/api/v1/documents/${DocumentId}/owner`, { data: { newOwnerUserId: me.userId } });
      expect(transfer.ok(), `POST owner → e2e-operator: ${String(transfer.status())} ${await transfer.text()}`).toBe(true);
      moved = true;

      const owned = await cardOf(operator, DocumentId);
      expect(owned.ownerUserId, 'власність не перейшла до e2e-operator').toBe(me.userId);
      expect(owned.canLockRows, 'картка власника без права: canLockRows').toBe(true);

      // ── 2. Власник без права бачить «Lock rows…» і блокує рядок через діалог ──────────────────
      await page.reload();
      await expect(firstCell, 'рядки сітки не завантажилися після перезавантаження').toBeVisible({ timeout: 60_000 });
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
          response.request().method() === 'POST' && new URL(response.url()).pathname === `/api/v1/documents/${DocumentId}/row-locks`,
        { timeout: 60_000 },
      );
      await save.click();
      const createdResponse = await created;
      expect(createdResponse.status(), `POST row-locks (власник без права): ${await createdResponse.text()}`).toBe(200);
      const lock = ((await createdResponse.json()) as RowLock[])[0];
      if (lock === undefined) throw new Error('POST row-locks не повернув блокування');

      await expect(dialog).toBeHidden({ timeout: 30_000 });
      await expect(grid.locator(`[data-row-lock-badge="${String(lock.id)}"]`), 'значка замка у власника немає').toBeVisible({
        timeout: 30_000,
      });

      // ── 3. Власність повернуто: той самий оператор знову без кнопки ───────────────────────────
      await releaseAll(admin);
      const back = await restoreOwner(admin, originalOwner);
      expect(back.ok(), `POST owner → назад: ${String(back.status())} ${await back.text()}`).toBe(true);
      moved = false;
      const after = await cardOf(operator, DocumentId);
      expect(after.canLockRows, 'картка після повернення власності: canLockRows').toBe(false);
      await page.reload();
      await expect(firstCell, 'рядки сітки не завантажилися після повернення власності').toBeVisible({ timeout: 60_000 });
      await expect(page.locator('[data-row-lock-open]'), 'після повернення власності кнопка лишилась').toHaveCount(0);
      await page.close();
    } finally {
      try {
        // Базова лінія для решти файлів: без блокувань і з початковим власником.
        await releaseAll(admin);
        if (moved) {
          const back = await restoreOwner(admin, originalOwner);
          // `soft`: відмова прибирання не має перекривати причину падіння самого тесту.
          expect.soft(back.ok(), `повернення власності документа стенда: ${String(back.status())} ${await back.text()}`).toBe(true);
        }
      } finally {
        await admin.close();
        await operator.close();
      }
    }
  });
});
