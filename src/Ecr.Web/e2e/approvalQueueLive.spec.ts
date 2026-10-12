import { expect, test, type APIRequestContext, type Browser, type BrowserContext } from '@playwright/test';

/**
 * ФВ-12.5: черга «До погодження» (`GET /api/v1/me/approval-queue`) проти РЕАЛЬНОГО сервера стенда
 * (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep approvalQueueLive`).
 *
 * Основні сценарії йдуть через API (плюс крок 2б - блок «До погодження» у My tasks) під справжніми сесіями
 * трьох користувачів стенда:
 *  - `e2e-admin` (Manage на проєкт 1) подає аркуш: подавач - не погоджувач власного подання (F-25);
 *  - `e2e-reviewer` (та сама роль, інша людина) - погоджувач: бачить аркуш у черзі, затверджує, аркуш зникає;
 *  - `e2e-operator` (Write) - права затвердження немає: черга порожня, чужого подання не бачить.
 *
 * ⛔ Без стенда файл ПАДАЄ (`beforeAll`), а не пропускається: пропуск червонить Evidence (honesty-guard), а
 * `globalSetup.ts` усе одно валить набір без стенда.
 *
 * ⚠ Файл іде за алфавітом ПЕРШИМ серед тих, що чіпають стан аркуша стенда; `afterAll` повертає аркуш у
 * `Draft` законною дією `Document.Reopen`, щоб `editAssignments`/`keyboardPath`/`security` бачили базову лінію.
 *
 * ⚠ Написано без локального прогону (на машині автора немає стенда): перший живий прогін - CI. Усе, чого код
 * не доводить, названо в звіті, а не вгадано мовчки.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Reviewer = { userName: 'e2e-reviewer', password: 'E2E-Rev1ewer-Work-2026!' };
const Operator = { userName: 'e2e-operator', password: 'E2E-Oper8tor-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

interface QueueItem {
  documentId: number;
  documentKey: string;
  projectCode: string;
  templateCode: string;
  sheetDefId: number;
  sheetCode: string;
  periodKey: number;
  submittedAt: string;
  submittedByDisplayName: string | null;
  stepOrdinal: number | null;
  stepCount: number | null;
  stepRoleCode: string | null;
}

interface QueuePage {
  items: QueueItem[];
  nextCursor: string | null;
}

interface DocumentTable {
  sheetDefId: number;
  sheetCode: string;
  tableInstanceId: number;
}

interface Slice {
  tableInstanceId: number;
  periodKey: number;
  columns: { code: string }[];
  rows: { rowKey: string; rowVersion: string }[];
}

/** Сесія користувача: окремий контекст браузера + вхід через API (cookie - в контексті). */
async function signedIn(browser: Browser, user: { userName: string; password: string }): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', {
    data: { userName: user.userName, password: user.password },
  });
  expect(login.ok(), `вхід ${user.userName}: ${String(login.status())} ${await login.text()}`).toBe(true);

  return context;
}

async function queue(request: APIRequestContext, query = ''): Promise<QueuePage> {
  const response = await request.get(`/api/v1/me/approval-queue${query}`);
  expect(response.ok(), `GET me/approval-queue${query}: ${String(response.status())} ${await response.text()}`).toBe(true);

  return (await response.json()) as QueuePage;
}

/** Ходить за курсором до кінця (сторінка може бути коротшою за ліміт) і збирає всі елементи. */
async function wholeQueue(request: APIRequestContext): Promise<QueueItem[]> {
  const all: QueueItem[] = [];
  let cursor: string | null = null;

  for (let guard = 0; guard < 20; guard += 1) {
    const page: QueuePage = await queue(request, cursor === null ? '?limit=200' : `?limit=200&cursor=${encodeURIComponent(cursor)}`);
    all.push(...page.items);
    if (page.nextCursor === null) return all;
    cursor = page.nextCursor;
  }

  throw new Error('черга не закінчилась за 20 сторінок');
}

function mine(items: QueueItem[], sheetDefId: number): QueueItem[] {
  return items.filter((item) => item.documentId === Number(DocumentId) && item.sheetDefId === sheetDefId);
}

test.describe('approvalQueue: черга «До погодження» на реальному стенді (ФВ-12.5)', () => {
  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
  });

  test.describe.configure({ mode: 'serial', timeout: 180_000 });

  let sheetDefId = 0;

  test.afterAll(async ({ browser }) => {
    if (PeriodKey === '' || DocumentId === '' || sheetDefId === 0) return;

    // Базова лінія стенда: аркуш у Draft. Ідемпотентно: Draft - нічого робити.
    const admin = await signedIn(browser, Admin);
    try {
      const reopen = await admin.request.post(`/api/v1/documents/${DocumentId}/reopen`, {
        data: { sheetDefId, periodKey: Number(PeriodKey), reason: 'e2e: approvalQueueLive, повернення базової лінії' },
      });
      // 204 - повернуто; 409 - уже не Submitted/Approved (Draft): це теж базова лінія.
      expect([204, 409], `reopen: ${String(reopen.status())} ${await reopen.text()}`).toContain(reopen.status());
    } finally {
      await admin.close();
    }
  });

  test('approvalQueue: подання -> черга погоджувача -> затвердження -> аркуш зникає; без права й подавач - порожньо', async ({
    browser,
  }) => {
    const admin = await signedIn(browser, Admin);
    const reviewer = await signedIn(browser, Reviewer);
    const operator = await signedIn(browser, Operator);

    try {
      // ── 0. Адреса аркуша: перший аркуш документа стенда ─────────────────────────
      const tablesResponse = await admin.request.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${PeriodKey}`);
      expect(tablesResponse.ok(), `GET tables: ${String(tablesResponse.status())}`).toBe(true);
      const tables = (await tablesResponse.json()) as DocumentTable[];
      const table = tables[0];
      if (table === undefined) throw new Error('у документа стенда немає таблиць');
      sheetDefId = table.sheetDefId;

      // До подання аркуша в черзі немає ні в кого.
      expect(mine(await wholeQueue(reviewer.request), sheetDefId), 'аркуш у Draft уже в черзі погоджувача').toEqual([]);

      // ── 1. Подання від e2e-admin: значення в першій комірці, що приймає запис, потім submit ───────
      const sliceResponse = await admin.request.get(
        `/api/v1/documents/${DocumentId}/tables/${String(table.tableInstanceId)}?periodKey=${PeriodKey}`,
      );
      expect(sliceResponse.ok(), `GET slice: ${String(sliceResponse.status())}`).toBe(true);
      const slice = (await sliceResponse.json()) as Slice;
      const row = slice.rows[0];
      if (row === undefined) throw new Error('у зрізі немає рядків');

      let written = false;
      for (const column of slice.columns.slice(0, 12)) {
        const patch = await admin.request.patch(`/api/v1/documents/${DocumentId}/cells`, {
          data: {
            tableInstanceId: slice.tableInstanceId,
            periodKey: slice.periodKey,
            origin: 'UserEdit',
            rows: [{ rowKey: row.rowKey, baseVersion: row.rowVersion, cells: [{ columnCode: column.code, value: 101 }] }],
          },
        });
        if (patch.ok()) {
          written = true;
          break;
        }
      }
      expect(written, 'жодна з перших колонок рядка 0 не прийняла значення (PATCH …/cells)').toBe(true);

      const submit = await admin.request.post(`/api/v1/documents/${DocumentId}/submit`, {
        data: { sheetDefId, periodKey: Number(PeriodKey), acknowledgeWarnings: true },
      });
      expect(submit.status(), `submit: ${await submit.text()}`).toBe(204);

      // ── 2. Погоджувач бачить аркуш у черзі ───────────────────────────────────────
      const reviewerItems = mine(await wholeQueue(reviewer.request), sheetDefId);
      expect(reviewerItems, 'поданий аркуш не з’явився в черзі погоджувача').toHaveLength(1);
      const item = reviewerItems[0] as QueueItem;
      expect(item.periodKey).toBe(Number(PeriodKey));
      expect(item.sheetCode).toBe(table.sheetCode);
      expect(item.documentKey.length).toBeGreaterThan(0);
      expect(item.projectCode.length).toBeGreaterThan(0);
      expect(item.templateCode.length).toBeGreaterThan(0);
      expect(item.submittedByDisplayName).toBe('E2E administrator');
      expect(Number.isNaN(Date.parse(item.submittedAt))).toBe(false);

      // ── 2б. UI: блок «До погодження» у «My tasks» (клієнт ФВ-12.5) ───────────────────────────
      const page = await reviewer.newPage();
      await page.goto('/');
      await page.getByRole('button', { name: /My tasks|Мои задачи/ }).click();
      const card = page.locator(`[data-approval-document="${DocumentId}"][data-approval-sheet="${table.sheetCode}"]`);
      await expect(card).toBeVisible({ timeout: 30_000 });
      await expect(card).toContainText(item.documentKey);
      await expect(card).toContainText('E2E administrator');
      await card.locator('[data-action="review"]').click();
      await expect(page).toHaveURL(
        new RegExp(`/documents/${DocumentId}\\?periodKey=${PeriodKey}&sheet=${encodeURIComponent(table.sheetCode)}`),
      );
      await page.close();

      // ── 3. Подавач (F-25) і оператор без права затвердження - порожньо ─────────────
      expect(mine(await wholeQueue(admin.request), sheetDefId), 'власне подання в черзі подавача').toEqual([]);
      expect(mine(await wholeQueue(operator.request), sheetDefId), 'аркуш без права Approve у черзі оператора').toEqual([]);

      // ── 4. Зіпсований курсор - 422 з ключем каталогу ─────────────────────────────
      const bad = await reviewer.request.get('/api/v1/me/approval-queue?cursor=not-a-cursor');
      expect(bad.status()).toBe(422);
      expect(((await bad.json()) as { messageKey?: string }).messageKey).toBe(
        'err.ECR-REQ-0422.approvalQueueCursorInvalid',
      );

      // ── 5. Затвердження: аркуш зникає з черги ──────────────────────────────────
      const approve = await reviewer.request.post(`/api/v1/documents/${DocumentId}/approve`, {
        data: { sheetDefId, periodKey: Number(PeriodKey), approved: true, reason: null },
      });
      expect(approve.status(), `approve: ${await approve.text()}`).toBe(204);

      expect(mine(await wholeQueue(reviewer.request), sheetDefId), 'затверджений аркуш лишився в черзі').toEqual([]);
    } finally {
      await admin.close();
      await reviewer.close();
      await operator.close();
    }
  });
});
