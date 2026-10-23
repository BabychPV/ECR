import { expect, test, type Browser, type BrowserContext, type Page, type Response } from '@playwright/test';

/**
 * D-442 (BRIEF-1021 П.3, AN-129) проти РЕАЛЬНОГО сервера стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`,
 * `-Grep approverEditLive`): після `Submit` поданий аркуш править ЛИШЕ погоджувач поточного кроку.
 *
 *  - `e2e-admin` (Manage на проєкт 1; маршруту в стенді немає — погоджувач той, кому `CanApprove` дозволяє)
 *    подає аркуш, а потім як погоджувач ПРАВИТЬ його в сітці: вставка доходить до сервера (PATCH 200), аркуш
 *    лишається `Submitted`, журнал — `ApproverEdit` («правка погоджувача»);
 *  - `e2e-operator` (Write, не погоджувач) ту саму комірку поданого аркуша не править: 403 `DocumentSubmitted`;
 *  - затвердження знімає НОВИЙ зріз (`GET …/versions` +1) — у ньому правка погоджувача;
 *  - затверджений аркуш — «лише читання» БЕЗ штриховки (`.ecr-sheet-locked`), але із замком у куті (D-128).
 *
 * ⛔ Без стенда файл ПАДАЄ (`beforeAll`), а не пропускається (honesty-guard, `globalSetup.ts`).
 *
 * ⚠ `afterAll` повертає аркуш у `Draft` законною дією `Document.Reopen` — базова лінія для решти файлів.
 *
 * ⚠ Написано без локального прогону (на машині автора немає стенда): перший живий прогін — CI.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Operator = { userName: 'e2e-operator', password: 'E2E-Oper8tor-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

/** Значення, яке погоджувач вставляє в сітку поданого аркуша. */
const ApproverValue = '442';

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
  const context = await browser.newContext({ permissions: ['clipboard-read', 'clipboard-write'] });
  const login = await context.request.post('/api/v1/login/local', {
    data: { userName: user.userName, password: user.password },
  });
  expect(login.ok(), `вхід ${user.userName}: ${String(login.status())} ${await login.text()}`).toBe(true);

  return context;
}

async function sliceOf(context: BrowserContext, table: DocumentTable): Promise<Slice> {
  const response = await context.request.get(
    `/api/v1/documents/${DocumentId}/tables/${String(table.tableInstanceId)}?periodKey=${PeriodKey}`,
  );
  expect(response.ok(), `GET slice: ${String(response.status())}`).toBe(true);

  return (await response.json()) as Slice;
}

async function versionCount(context: BrowserContext): Promise<number> {
  const response = await context.request.get(`/api/v1/documents/${DocumentId}/versions?periodKey=${PeriodKey}`);
  expect(response.ok(), `GET versions: ${String(response.status())} ${await response.text()}`).toBe(true);

  return ((await response.json()) as unknown[]).length;
}

function waitForWrite(page: Page, method: string, action: string): Promise<Response> {
  const path = `/api/v1/documents/${DocumentId}/${action}`;

  return page.waitForResponse(
    (response) => response.request().method() === method && new URL(response.url()).pathname === path,
    { timeout: 90_000 },
  );
}

test.describe('approverEdit: правка погоджувача поданого аркуша на реальному стенді (D-442)', () => {
  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
  });

  test.describe.configure({ mode: 'serial', timeout: 240_000 });

  let sheetDefId = 0;

  async function reopen(browser: Browser, reason: string): Promise<void> {
    const admin = await signedIn(browser, Admin);
    try {
      const response = await admin.request.post(`/api/v1/documents/${DocumentId}/reopen`, {
        data: { sheetDefId, periodKey: Number(PeriodKey), reason },
      });
      // 204 — повернуто. Аркуш уже не Submitted/Approved (Draft) — це теж базова лінія: сервер відповідає
      // 403 з причиною `BusinessRule` (`EditRules.CanReopen`, живий прогін CI) або 409.
      const text = await response.text();
      const alreadyDraft =
        response.status() === 409 || (response.status() === 403 && text.includes('"reason":"BusinessRule"'));
      expect(response.status() === 204 || alreadyDraft, `reopen: ${String(response.status())} ${text}`).toBe(true);
    } finally {
      await admin.close();
    }
  }

  test.afterAll(async ({ browser }) => {
    if (PeriodKey === '' || DocumentId === '' || sheetDefId === 0) return;
    await reopen(browser, 'e2e: approverEditLive, повернення базової лінії');
  });

  test('погоджувач править поданий аркуш (журнал ApproverEdit), інші — ні; погодження знімає новий зріз; затверджений — без штриховки, із замком', async ({
    browser,
  }) => {
    const admin = await signedIn(browser, Admin);
    const operator = await signedIn(browser, Operator);

    try {
      // ── 0. Аркуш стенда в Draft і запис від автора ─────────────────────────────
      const tablesResponse = await admin.request.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${PeriodKey}`);
      expect(tablesResponse.ok(), `GET tables: ${String(tablesResponse.status())}`).toBe(true);
      const table = ((await tablesResponse.json()) as DocumentTable[])[0];
      if (table === undefined) throw new Error('у документа стенда немає таблиць');
      sheetDefId = table.sheetDefId;
      await reopen(browser, 'e2e: approverEditLive, базова лінія перед сценарієм');

      const draft = await sliceOf(admin, table);
      const row = draft.rows[0];
      if (row === undefined) throw new Error('у зрізі немає рядків');

      let column = '';
      for (const candidate of draft.columns.slice(0, 12)) {
        const patch = await admin.request.patch(`/api/v1/documents/${DocumentId}/cells`, {
          data: {
            tableInstanceId: draft.tableInstanceId,
            periodKey: draft.periodKey,
            origin: 'UserEdit',
            rows: [{ rowKey: row.rowKey, baseVersion: row.rowVersion, cells: [{ columnCode: candidate.code, value: 101 }] }],
          },
        });
        if (patch.ok()) {
          column = candidate.code;
          break;
        }
      }
      expect(column, 'жодна з перших колонок рядка 0 не прийняла значення (PATCH …/cells)').not.toBe('');

      const submit = await admin.request.post(`/api/v1/documents/${DocumentId}/submit`, {
        data: { sheetDefId, periodKey: Number(PeriodKey), acknowledgeWarnings: true },
      });
      expect(submit.status(), `submit: ${await submit.text()}`).toBe(204);
      const versionsAfterSubmit = await versionCount(admin);

      // ── 1. Не погоджувач (Write) поданий аркуш не править ───────────────────────
      const operatorSlice = await sliceOf(operator, table);
      const operatorRow = operatorSlice.rows.find((candidate) => candidate.rowKey === row.rowKey);
      const denied = await operator.request.patch(`/api/v1/documents/${DocumentId}/cells`, {
        data: {
          tableInstanceId: operatorSlice.tableInstanceId,
          periodKey: operatorSlice.periodKey,
          origin: 'UserEdit',
          rows: [{ rowKey: row.rowKey, baseVersion: operatorRow?.rowVersion ?? null, cells: [{ columnCode: column, value: 999 }] }],
        },
      });
      expect(denied.status(), `PATCH оператора на поданому: ${await denied.text()}`).toBe(403);
      expect(JSON.stringify(await denied.json())).toContain('DocumentSubmitted');

      // ── 2. Погоджувач править у СІТЦІ: вставка доходить до сервера ──────────────
      const page = await admin.newPage();
      await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
      const grid = page.locator('revo-grid').first();
      await expect(grid.locator('revogr-data .rgCell').first(), 'рядки сітки не завантажилися').toBeVisible({
        timeout: 60_000,
      });
      await expect(page.getByRole('tab', { selected: true })).toContainText('Submitted', { timeout: 30_000 });

      // Якір вставки — та сама комірка, що в `keyboardPath` (кут таблиці стрілками туди й назад).
      await grid.focus();
      await page.keyboard.press('ArrowRight');
      await page.keyboard.press('ArrowDown');
      await page.keyboard.press('ArrowLeft');
      await page.keyboard.press('ArrowUp');
      await page.evaluate(async (value) => {
        await navigator.clipboard.writeText(value);
      }, ApproverValue);

      const saved = waitForWrite(page, 'PATCH', 'cells');
      await page.keyboard.press('Control+v');
      await page.keyboard.press('Control+s');
      const savedResponse = await saved;
      expect(savedResponse.status(), `PATCH погоджувача на поданому: ${await savedResponse.text()}`).toBe(200);

      // Аркуш лишився поданим — нового статусу немає.
      await expect(page.getByRole('tab', { selected: true })).toContainText('Submitted');

      // ── 3. Журнал: «правка погоджувача» ─────────────────────────────────────────
      const from = new Date(Date.now() - 60 * 60 * 1000).toISOString();
      const to = new Date(Date.now() + 60 * 60 * 1000).toISOString();
      const audit = await admin.request.get(
        `/api/v1/audit/cells?from=${encodeURIComponent(from)}&to=${encodeURIComponent(to)}&documentId=${DocumentId}`
          + `&origin=ApproverEdit&periodKey=${PeriodKey}&limit=50`,
      );
      expect(audit.ok(), `GET audit/cells: ${String(audit.status())} ${await audit.text()}`).toBe(true);
      const auditPage = (await audit.json()) as { items: { origin: string }[] };
      expect(auditPage.items.length, 'у журналі немає «правки погоджувача»').toBeGreaterThan(0);
      expect(auditPage.items.every((item) => item.origin === 'ApproverEdit')).toBe(true);

      // ── 4. Погодження знімає новий зріз (із правкою погоджувача) ────────────────
      const approve = await admin.request.post(`/api/v1/documents/${DocumentId}/approve`, {
        data: { sheetDefId, periodKey: Number(PeriodKey), approved: true, reason: null },
      });
      expect(approve.status(), `approve: ${await approve.text()}`).toBe(204);
      expect(await versionCount(admin), 'погодження після правки погоджувача не зняло нового зрізу').toBe(
        versionsAfterSubmit + 1,
      );

      // ── 5. Затверджений: «лише читання» без штриховки, замок лишається (D-128) ───
      await page.reload();
      await expect(page.getByRole('tab', { selected: true })).toContainText('Approved', { timeout: 30_000 });
      const locked = page.locator('.ecr-sheet-locked [data-cell-state="readOnly"]').first();
      await expect(locked, 'у затвердженому аркуші немає комірки «лише читання» під .ecr-sheet-locked').toBeVisible({
        timeout: 60_000,
      });

      const look = await locked.evaluate((element) => {
        const own = getComputedStyle(element);
        const lock = getComputedStyle(element, '::after');
        return {
          hatch: own.backgroundImage,
          cursor: own.cursor,
          lockMask: lock.maskImage || lock.getPropertyValue('-webkit-mask-image'),
          lockContent: lock.content,
        };
      });
      expect(look.hatch, 'затверджений аркуш досі заштрихований').toBe('none');
      expect(look.cursor).toBe('not-allowed');
      expect(look.lockContent, 'замок у куті зник — «лише читання» без другого носія (D-128)').not.toBe('none');
      expect(look.lockMask).not.toBe('none');

      await page.close();
    } finally {
      await admin.close();
      await operator.close();
    }
  });
});
