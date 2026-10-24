import { expect, test, type APIRequestContext, type Browser, type BrowserContext } from '@playwright/test';

/**
 * ✎ D-449: «Повернуто на доопрацювання» у «My tasks» (`GET /api/v1/me/returned-sheets`) проти РЕАЛЬНОГО сервера стенда
 * (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep returnedToMeLive`).
 *
 * Сценарій (рішення людини 24.10: задача відкриває потрібний аркуш і період одним кліком і зникає сама):
 *  - власником документа стенда на час файла стає `e2e-operator` (Write на проєкт; передача D-443 — як у
 *    `documentParticipantsLive`); `e2e-admin` подає аркуш;
 *  - `e2e-reviewer` (погоджувач) повертає аркуш із причиною (два рядки);
 *  - власник бачить картку в «My tasks» з причиною й тим, хто повернув, і лічильник на кнопці; «Відкрити аркуш» веде
 *    на аркуш і період;
 *  - повторне подання — картка зникає (живий запит, збережених задач немає); погоджувач і подавач задачі не мають.
 *
 * ⛔ Без стенда файл ПАДАЄ (`beforeAll`), а не пропускається (honesty-guard). `afterAll` повертає власника й аркуш у
 * `Draft` (`Document.Reopen`), хоч би на якому кроці файл зупинився — наступні специфікації бачать базову лінію.
 *
 * ⚠ Написано без локального прогону (стенда на машині автора немає): перший живий прогін — CI `e2e-stand`.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Reviewer = { userName: 'e2e-reviewer', password: 'E2E-Rev1ewer-Work-2026!' };
const Operator = { userName: 'e2e-operator', password: 'E2E-Oper8tor-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

const ReasonFirst = 'e2e D-449: рядок 1 не збігається з актами';
const Reason = `${ReasonFirst}\nПеревірте одиниці виміру.`;

interface ReturnedItem {
  documentId: number;
  documentKey: string;
  sheetDefId: number;
  sheetCode: string;
  periodKey: number;
  returnedAt: string;
  returnedByDisplayName: string | null;
  reason: string;
}

interface ReturnedPage {
  items: ReturnedItem[];
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

async function signedIn(browser: Browser, user: { userName: string; password: string }): Promise<BrowserContext> {
  const context = await browser.newContext();
  const login = await context.request.post('/api/v1/login/local', {
    data: { userName: user.userName, password: user.password },
  });
  expect(login.ok(), `вхід ${user.userName}: ${String(login.status())} ${await login.text()}`).toBe(true);

  return context;
}

/** Увесь перелік за курсором (сторінка може бути коротшою за ліміт), лише рядки аркуша стенда. */
async function returnedOf(request: APIRequestContext, sheetDefId: number): Promise<ReturnedItem[]> {
  const all: ReturnedItem[] = [];
  let cursor: string | null = null;

  for (let guard = 0; guard < 20; guard += 1) {
    const query: string = cursor === null ? '?limit=200' : `?limit=200&cursor=${encodeURIComponent(cursor)}`;
    const response = await request.get(`/api/v1/me/returned-sheets${query}`);
    expect(response.ok(), `GET me/returned-sheets: ${String(response.status())} ${await response.text()}`).toBe(true);
    const page = (await response.json()) as ReturnedPage;
    all.push(...page.items);
    if (page.nextCursor === null) {
      return all.filter(
        (item) => item.documentId === Number(DocumentId) && item.sheetDefId === sheetDefId && item.periodKey === Number(PeriodKey),
      );
    }
    cursor = page.nextCursor;
  }

  throw new Error('перелік не закінчився за 20 сторінок');
}

async function reopenToDraft(request: APIRequestContext, sheetDefId: number, why: string): Promise<void> {
  const reopen = await request.post(`/api/v1/documents/${DocumentId}/reopen`, {
    data: { sheetDefId, periodKey: Number(PeriodKey), reason: why },
  });
  // 204 - повернуто; 409 - уже не Submitted/Approved (Draft чи Rejected): теж придатно для подання.
  expect([204, 409], `reopen: ${String(reopen.status())} ${await reopen.text()}`).toContain(reopen.status());
}

async function submit(request: APIRequestContext, sheetDefId: number): Promise<void> {
  const response = await request.post(`/api/v1/documents/${DocumentId}/submit`, {
    data: { sheetDefId, periodKey: Number(PeriodKey), acknowledgeWarnings: true },
  });
  expect(response.status(), `submit: ${await response.text()}`).toBe(204);
}

test.describe('returnedToMe: «Повернуто на доопрацювання» на реальному стенді (D-449)', () => {
  test.describe.configure({ mode: 'serial', timeout: 240_000 });

  let sheetDefId = 0;
  let originalOwner: number | null = null;

  test.beforeAll(async ({ browser }) => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }

    const admin = await signedIn(browser, Admin);
    try {
      const document = await admin.request.get(`/api/v1/documents/${DocumentId}?periodKey=${PeriodKey}`);
      expect(document.ok(), `GET document: ${String(document.status())}`).toBe(true);
      originalOwner = ((await document.json()) as { ownerUserId: number | null }).ownerUserId;
    } finally {
      await admin.close();
    }
  });

  test.afterAll(async ({ browser }, testInfo) => {
    if (PeriodKey === '' || DocumentId === '') return;
    testInfo.setTimeout(60_000);

    const admin = await signedIn(browser, Admin);
    try {
      if (sheetDefId !== 0) {
        await reopenToDraft(admin.request, sheetDefId, 'e2e: returnedToMeLive, повернення базової лінії');
      }

      const current = ((await (await admin.request.get(`/api/v1/documents/${DocumentId}?periodKey=${PeriodKey}`)).json()) as {
        ownerUserId: number | null;
      }).ownerUserId;
      if (originalOwner !== null && current !== originalOwner) {
        let back = await admin.request.post(`/api/v1/documents/${DocumentId}/owner`, { data: { newOwnerUserId: originalOwner } });
        if (back.status() === 422) {
          const me = (await (await admin.request.get('/api/v1/me')).json()) as { userId: number };
          back = await admin.request.post(`/api/v1/documents/${DocumentId}/owner`, { data: { newOwnerUserId: me.userId } });
        }
        expect(back.ok(), `повернення власника: ${String(back.status())} ${await back.text()}`).toBe(true);
      }
    } finally {
      await admin.close();
    }
  });

  test('returnedToMe: подання -> повернення з причиною -> картка власника в My tasks -> відкрити -> повторне подання -> картка зникла', async ({
    browser,
  }) => {
    const admin = await signedIn(browser, Admin);
    const reviewer = await signedIn(browser, Reviewer);
    const operator = await signedIn(browser, Operator);

    try {
      // ── 0. Аркуш стенда й базова лінія (Draft) ────────────────────────────────────────────
      const tablesResponse = await admin.request.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${PeriodKey}`);
      expect(tablesResponse.ok(), `GET tables: ${String(tablesResponse.status())}`).toBe(true);
      const table = ((await tablesResponse.json()) as DocumentTable[])[0];
      if (table === undefined) throw new Error('у документа стенда немає таблиць');
      sheetDefId = table.sheetDefId;
      await reopenToDraft(admin.request, sheetDefId, 'e2e: returnedToMeLive, базова лінія перед поданням');

      // ── 1. Власником стає e2e-operator (Write на проєкт) ───────────────────────────────────
      const operatorId = ((await (await operator.request.get('/api/v1/me')).json()) as { userId: number }).userId;
      const transferred = await admin.request.post(`/api/v1/documents/${DocumentId}/owner`, {
        data: { newOwnerUserId: operatorId },
      });
      expect(transferred.ok(), `передача власника: ${String(transferred.status())} ${await transferred.text()}`).toBe(true);
      expect(await returnedOf(operator.request, sheetDefId), 'аркуш у Draft уже серед повернених').toEqual([]);

      // ── 2. Подання від e2e-admin: значення в першій комірці, що приймає запис ─────────────────
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
            rows: [{ rowKey: row.rowKey, baseVersion: row.rowVersion, cells: [{ columnCode: column.code, value: 102 }] }],
          },
        });
        if (patch.ok()) {
          written = true;
          break;
        }
      }
      expect(written, 'жодна з перших колонок рядка 0 не прийняла значення (PATCH …/cells)').toBe(true);
      await submit(admin.request, sheetDefId);

      // Подання саме по собі задачею власника не є.
      expect(await returnedOf(operator.request, sheetDefId), 'поданий аркуш серед повернених').toEqual([]);

      // ── 3. Погоджувач повертає з причиною ─────────────────────────────────────────────────
      const rejected = await reviewer.request.post(`/api/v1/documents/${DocumentId}/approve`, {
        data: { sheetDefId, periodKey: Number(PeriodKey), approved: false, reason: Reason },
      });
      expect(rejected.status(), `повернення погоджувачем: ${await rejected.text()}`).toBe(204);

      // ── 4. API: задача власника з причиною й іменем; у погоджувача й подавача її немає ────────
      const items = await returnedOf(operator.request, sheetDefId);
      expect(items, 'повернений аркуш не з’явився у власника').toHaveLength(1);
      const item = items[0] as ReturnedItem;
      expect(item.sheetCode).toBe(table.sheetCode);
      expect(item.reason).toBe(Reason);
      expect(item.returnedByDisplayName).toBe('E2E reviewer');
      expect(Number.isNaN(Date.parse(item.returnedAt))).toBe(false);
      expect(await returnedOf(reviewer.request, sheetDefId), 'погоджувач бачить чужу задачу').toEqual([]);
      expect(await returnedOf(admin.request, sheetDefId), 'подавач (не власник) бачить чужу задачу').toEqual([]);

      // ── 5. UI: лічильник на кнопці, картка в «My tasks», «Відкрити аркуш» ────────────────────
      const page = await operator.newPage();
      await page.goto('/');
      await expect(page.locator('[data-my-tasks-waiting]')).toBeVisible({ timeout: 30_000 });
      await page.getByRole('button', { name: /My tasks|Мои задачи/ }).click();
      const card = page.locator(`[data-returned-document="${DocumentId}"][data-returned-sheet="${table.sheetCode}"]`);
      await expect(card).toBeVisible({ timeout: 30_000 });
      await expect(card).toContainText(ReasonFirst);
      await expect(card).toContainText('E2E reviewer');
      await card.locator('[data-action="open-returned"]').click();
      await expect(page).toHaveURL(
        new RegExp(`/documents/${DocumentId}\\?periodKey=${PeriodKey}&sheet=${encodeURIComponent(table.sheetCode)}`),
      );

      // ── 6. Повторне подання - задача зникає сама ──────────────────────────────────────────
      await submit(admin.request, sheetDefId);
      expect(await returnedOf(operator.request, sheetDefId), 'повторно поданий аркуш лишився серед повернених').toEqual([]);

      await page.goto('/');
      await page.getByRole('button', { name: /My tasks|Мои задачи/ }).click();
      await expect(page.getByRole('dialog')).toBeVisible({ timeout: 30_000 });
      await expect(card).toHaveCount(0, { timeout: 30_000 });
      await page.close();
    } finally {
      await admin.close();
      await reviewer.close();
      await operator.close();
    }
  });
});
