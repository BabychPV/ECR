import { expect, test, type APIRequestContext, type Browser, type BrowserContext } from '@playwright/test';

/**
 * Інспектор → History: журнал дій аркуша проти РЕАЛЬНОГО сервера стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`,
 * `-Grep sheetLogLive`).
 *
 * Сценарій: `e2e-admin` змінює значення комірки й подає аркуш; `e2e-reviewer` відхиляє з причиною;
 *  - API: журнал аркуша містить дію «Reject» з причиною та правку комірки, найновіші перші, keyset без дублів;
 *  - UI (автор): банер відхилення на аркуші (вже є) + History БЕЗ вибраної комірки = «Sheet log» (перемикач, заголовок з
 *    аркушем і періодом, «This cell» вимкнений), рядок відхилення з причиною, рядок правки;
 *  - клік по рядку правки веде в комірку: «This cell» стає доступним, АЛЕ вміст лишається журналом аркуша (вибір комірки
 *    не підміняє його); перемикання на «This cell» показує історію саме цієї комірки;
 *  - знімки обох режимів йдуть у вкладення прогону.
 *
 * ⛔ Без стенда файл ПАДАЄ (`beforeAll`), а не пропускається (honesty-guard). `afterAll` повертає аркуш у `Draft`.
 */
const Admin = { userName: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Reviewer = { userName: 'e2e-reviewer', password: 'E2E-Rev1ewer-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

const Reason = 'e2e sheet log: рядок 1 не збігається з актами';

interface ActivityItem {
  kind: string;
  at: string;
  byDisplayName: string;
  action: string | null;
  reason: string | null;
  rowKey: string | null;
  columnCode: string | null;
  newValue: string | null;
}

interface ActivityPage {
  items: ActivityItem[];
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

async function reopenToDraft(request: APIRequestContext, sheetDefId: number, why: string): Promise<void> {
  const reopen = await request.post(`/api/v1/documents/${DocumentId}/reopen`, {
    data: { sheetDefId, periodKey: Number(PeriodKey), reason: why },
  });
  const text = await reopen.text();
  const alreadyNotSubmitted =
    reopen.status() === 409 || (reopen.status() === 403 && text.includes('"reason":"BusinessRule"'));
  expect(reopen.status() === 204 || alreadyNotSubmitted, `reopen: ${String(reopen.status())} ${text}`).toBe(true);
}

async function activity(request: APIRequestContext, sheetDefId: number, query = ''): Promise<ActivityPage> {
  const response = await request.get(
    `/api/v1/documents/${DocumentId}/sheets/${String(sheetDefId)}/activity?periodKey=${PeriodKey}${query}`,
  );
  expect(response.ok(), `GET activity: ${String(response.status())} ${await response.text()}`).toBe(true);

  return (await response.json()) as ActivityPage;
}

test.describe('sheetLogLive: History = журнал аркуша на реальному стенді', () => {
  test.describe.configure({ mode: 'serial', timeout: 240_000 });

  let sheetDefId = 0;

  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
  });

  test.afterAll(async ({ browser }, testInfo) => {
    if (PeriodKey === '' || DocumentId === '' || sheetDefId === 0) return;
    testInfo.setTimeout(60_000);

    const admin = await signedIn(browser, Admin);
    try {
      await reopenToDraft(admin.request, sheetDefId, 'e2e: sheetLogLive, повернення базової лінії');
    } finally {
      await admin.close();
    }
  });

  test('правка + відхилення з причиною -> журнал аркуша в API та в History без вибраної комірки', async ({ browser }) => {
    const admin = await signedIn(browser, Admin);
    const reviewer = await signedIn(browser, Reviewer);

    try {
      // ── 0. Аркуш стенда й базова лінія (Draft) ────────────────────────────────────────────
      const tablesResponse = await admin.request.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${PeriodKey}`);
      expect(tablesResponse.ok()).toBe(true);
      const table = ((await tablesResponse.json()) as DocumentTable[])[0];
      if (table === undefined) throw new Error('у документа стенда немає таблиць');
      sheetDefId = table.sheetDefId;
      await reopenToDraft(admin.request, sheetDefId, 'e2e: sheetLogLive, базова лінія');

      // ── 1. Правка комірки людиною (UserEdit) ─────────────────────────────────────────────
      const sliceResponse = await admin.request.get(
        `/api/v1/documents/${DocumentId}/tables/${String(table.tableInstanceId)}?periodKey=${PeriodKey}`,
      );
      expect(sliceResponse.ok()).toBe(true);
      const slice = (await sliceResponse.json()) as Slice;
      const row = slice.rows[0];
      if (row === undefined) throw new Error('у зрізі немає рядків');
      let editedColumn = '';
      for (const column of slice.columns.slice(0, 12)) {
        const patch = await admin.request.patch(`/api/v1/documents/${DocumentId}/cells`, {
          data: {
            tableInstanceId: slice.tableInstanceId,
            periodKey: slice.periodKey,
            origin: 'UserEdit',
            rows: [{ rowKey: row.rowKey, baseVersion: row.rowVersion, cells: [{ columnCode: column.code, value: 217 }] }],
          },
        });
        if (patch.ok()) {
          editedColumn = column.code;
          break;
        }
      }
      expect(editedColumn, 'жодна з перших колонок рядка 0 не прийняла значення').not.toBe('');

      // ── 2. Подання (admin) і відхилення з причиною (reviewer) ───────────────────────────────
      const submit = await admin.request.post(`/api/v1/documents/${DocumentId}/submit`, {
        data: { sheetDefId, periodKey: Number(PeriodKey), acknowledgeWarnings: true },
      });
      expect(submit.status(), `submit: ${await submit.text()}`).toBe(204);
      const rejected = await reviewer.request.post(`/api/v1/documents/${DocumentId}/approve`, {
        data: { sheetDefId, periodKey: Number(PeriodKey), approved: false, reason: Reason },
      });
      expect(rejected.status(), `відхилення: ${await rejected.text()}`).toBe(204);

      // ── 3. API: журнал аркуша ───────────────────────────────────────────────────────────
      const log = await activity(admin.request, sheetDefId);
      const kinds = log.items.map((item) => `${item.kind}:${item.action ?? item.newValue ?? ''}`);
      expect(kinds[0], `найновіше - відхилення: ${kinds.join(', ')}`).toBe('Workflow:Reject');
      expect(log.items[0]?.reason).toBe(Reason);
      expect(log.items.some((item) => item.kind === 'CellEdit' && item.newValue?.startsWith('217') === true && item.columnCode === editedColumn)).toBe(
        true,
      );
      const times = log.items.map((item) => Date.parse(item.at));
      expect(times, 'найновіші першими').toEqual([...times].sort((a, b) => b - a));

      // keyset: по одному рядку на сторінку - без втрат і дублів.
      const seen: string[] = [];
      let cursor: string | null = null;
      for (let guard = 0; guard < 8; guard += 1) {
        const page: ActivityPage = await activity(
          admin.request,
          sheetDefId,
          `&limit=1${cursor === null ? '' : `&cursor=${encodeURIComponent(cursor)}`}`,
        );
        expect(page.items.length).toBeLessThanOrEqual(1);
        seen.push(...page.items.map((item) => `${item.at}|${item.kind}|${item.action ?? item.newValue ?? ''}`));
        cursor = page.nextCursor;
        if (cursor === null) break;
      }
      expect(new Set(seen).size, `дублі між сторінками: ${seen.join('; ')}`).toBe(seen.length);
      expect(seen.length).toBeGreaterThanOrEqual(3);

      // ── 4. UI: автор бачить банер і History = журнал аркуша ────────────────────────────────
      const page = await admin.newPage();
      await page.setViewportSize({ width: 1440, height: 900 });
      await page.goto(
        `/documents/${DocumentId}?periodKey=${PeriodKey}&sheet=${encodeURIComponent(table.sheetCode)}&panel=history`,
      );
      await expect(page.locator('revo-grid').first()).toBeVisible({ timeout: 60_000 });
      await expect(page.getByTestId('document-sheet-banner')).toContainText(Reason, { timeout: 30_000 });

      const aside = page.getByRole('complementary');
      await expect(aside).toBeVisible({ timeout: 30_000 });
      const sheetRadio = aside.getByRole('radio', { name: 'Sheet log' });
      const cellRadio = aside.getByRole('radio', { name: 'This cell' });
      await expect(sheetRadio).toBeChecked();
      await expect(cellRadio).toBeDisabled();
      await expect(aside.locator('[data-inspector-scope]')).toContainText(/^Sheet: /);

      const rejectLine = aside.locator('[data-inspector-sheet-event="Reject"]').first();
      await expect(rejectLine).toContainText(Reason, { timeout: 30_000 });
      await expect(rejectLine).toContainText('E2E reviewer');
      const editLine = aside.locator('[data-inspector-sheet-event="CellEdit"]').first();
      await expect(editLine).toBeVisible();
      await expect(editLine).toContainText('217');
      await aside.screenshot({ path: test.info().outputPath('history-sheet-log.png') });
      await page.screenshot({ path: test.info().outputPath('history-sheet-log-page.png') });

      // ── 5. Клік по правці: комірка вибрана, журнал НЕ підмінено, «This cell» доступний ─────────────
      await editLine.getByRole('button').click();
      await expect(cellRadio).toBeEnabled({ timeout: 30_000 });
      await expect(sheetRadio).toBeChecked();
      await expect(aside.locator('[data-inspector-sheet-event="Reject"]').first()).toBeVisible();

      // ── 6. Перемикач на «This cell»: історія саме цієї комірки; заголовок називає комірку ────────────
      await aside.locator('label', { hasText: 'This cell' }).click();
      await expect(aside.locator('[data-inspector-scope]')).toContainText(/^Cell: /);
      await expect(aside.locator('[data-inspector-history]')).toBeVisible({ timeout: 30_000 });
      await expect(aside.locator('[data-inspector-history]')).toContainText('217');
      await aside.screenshot({ path: test.info().outputPath('history-this-cell.png') });
      await page.screenshot({ path: test.info().outputPath('history-this-cell-page.png') });

      await page.close();
    } finally {
      await admin.close();
      await reviewer.close();
    }
  });
});
