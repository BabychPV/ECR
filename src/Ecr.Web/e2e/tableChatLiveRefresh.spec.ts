import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { expect, test, type APIRequestContext, type Browser, type BrowserContext, type Page, type Route } from '@playwright/test';
import { axeViolations } from './axeRule';

/**
 * LIVE-REFRESH (`.sync-local/plans/LIVE-REFRESH-PLAN.md`) on the live stand with TWO users at once
 * (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep "Live refresh"`):
 *  - chat: what one user writes reaches the other's header counter (summary poll 30 s) and the open panel
 *    (message poll 10 s) without a reload; scrolled up, the reader gets the "New messages: N" pill;
 *  - grid: the other user's cell edit raises the polite banner "Updated by … at HH:MM"; Refresh shows the value and
 *    KEEPS the reader's own edit that is still on its way to the server; own edits never raise the banner;
 *  - a user who does not see the changed table gets no banner; a sheet the user cannot see answers 404.
 *
 * ⚠ The file name sorts AFTER `tableChat.spec.ts` on purpose: that scenario starts from an empty chat of the stand.
 * ⚠ Real intervals (no test hook): each wait is one interval plus the request and a margin. Stand data is restored
 * in `afterAll` (messages deleted, cells back to their values, grants back).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!', shown: 'E2E administrator' };
const Reviewer = { user: 'e2e-reviewer', password: 'E2E-Rev1ewer-Work-2026!', shown: 'E2E reviewer' };
const Operator = { user: 'e2e-operator', password: 'E2E-Oper8tor-Work-2026!', shown: 'E2E operator' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const DocumentUrl = `/documents/${DocumentId}?periodKey=${PeriodKey}`;
const Shots = process.env['ECR_LR_SHOTS'] ?? path.join('test-results', 'live-refresh-shots');

/** Summary and sheet marker poll every 30 s (DoD: badge ≤ 35 s); one tick + request + margin. */
const SlowPoll = 45_000;
/** Open chat panel polls every 10 s (DoD: ≤ 15 s). */
const FastPoll = 15_000;

interface TableInfo {
  readonly tableCode: string;
  readonly tableDefId: number;
  readonly tableInstanceId: number;
  readonly sheetCode: string;
  readonly sheetDefId: number;
}

interface SliceBody {
  readonly tableInstanceId: number;
  readonly periodKey: number;
  readonly columns: { code: string }[];
  readonly rows: { rowKey: string; rowVersion: string; cells: Record<string, unknown> }[];
}

/** Cells the grid scenario wrote: restored in `afterAll`. */
const written = new Map<string, { tableInstanceId: number; rowKey: string; columnCode: string; original: unknown }>();
/** Chat messages the chat scenario wrote: deleted in `afterAll`. */
const messages: { tableCode: string; id: number }[] = [];

let table: TableInfo;

test.describe('Live refresh: two users on the stand', () => {
  // ⛔ Without a stand the file FAILS, it is not skipped (honesty-guard).
  test.beforeAll(async ({ browser }) => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('no stand: ECR_E2E_PERIOD/ECR_E2E_DOCUMENT are not set (tools/e2e-stand.ps1)');
    }
    mkdirSync(Shots, { recursive: true });

    const admin = await openAs(browser, Admin);
    try {
      const response = await admin.page.request.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${PeriodKey}`);
      expect(response.ok(), `GET tables: ${String(response.status())}`).toBe(true);
      const tables = (await response.json()) as TableInfo[];
      expect(tables.length, 'the stand document has no tables').toBeGreaterThan(0);
      table = tables[0] as TableInfo;
    } finally {
      await admin.context.close();
    }
  });
  test.describe.configure({ mode: 'serial', timeout: 300_000 });

  test.afterAll(async ({ browser }) => {
    const admin = await openAs(browser, Admin);
    try {
      const request = admin.page.request;
      for (const message of messages) {
        await request.delete(`/api/v1/documents/${DocumentId}/tables/${encodeURIComponent(message.tableCode)}/messages/${String(message.id)}`);
      }
      for (const cell of written.values()) {
        const slice = await sliceOf(request, cell.tableInstanceId);
        const row = slice.rows.find((candidate) => candidate.rowKey === cell.rowKey);
        if (row === undefined) continue;
        await patchCell(request, slice, row, cell.columnCode, cell.original ?? null);
      }
      await setOperatorGrants(request, [{ resourceKind: 'Project', resourceId: 1, level: 'Write', isDeny: false }]);
    } finally {
      await admin.context.close();
    }
  });

  test('Live refresh: chat — the other user sees the counter without a reload; the open panel gets the message and the pill', async ({ browser }) => {
    const admin = await openAs(browser, Admin);
    const reviewer = await openAs(browser, Reviewer);

    try {
      // Enough messages for the list to scroll.
      for (let index = 1; index <= 25; index += 1) await post(admin.page.request, `history ${String(index)}`);

      // The reviewer: document open, panel CLOSED — only the summary poll can move the counter.
      await reviewer.page.goto(DocumentUrl);
      const button = reviewer.page.locator('[data-discussion-trigger]');
      await expect(button).toBeVisible({ timeout: 90_000 });
      await expect(button).toHaveAccessibleName(/Discussion, \d+ unread/, { timeout: SlowPoll });
      const before = unreadOf(await button.getAttribute('aria-label'));

      // The administrator: panel open on the table, scrolled up to read older messages.
      await admin.page.goto(`${DocumentUrl}&panel=discussion&table=${encodeURIComponent(table.tableCode)}`);
      const panel = admin.page.locator('[data-chat-panel]');
      await expect(panel.locator('[data-chat-message]')).toHaveCount(25, { timeout: 90_000 });
      const composer = panel.getByRole('textbox', { name: /^Message/ });
      await composer.fill('draft that must survive the poll');
      await panel.getByRole('log').evaluate((node) => {
        node.scrollTop = 0;
      });

      // A message of the administrator reaches the reviewer's counter (≤ 35 s by the DoD).
      await post(admin.page.request, 'counter check');
      await expect(panel.locator('[data-chat-message]')).toHaveCount(26, { timeout: FastPoll });
      await expect(button).toHaveAccessibleName(`Discussion, ${String(before + 1)} unread`, { timeout: SlowPoll });

      // A message of the reviewer reaches the open panel (≤ 15 s); scrolled up, the pill says so.
      await post(reviewer.page.request, 'live reply from the second user');
      await expect(panel.locator('[data-chat-message]')).toHaveCount(27, { timeout: FastPoll });
      const pill = panel.locator('[data-chat-new-pill]');
      await expect(pill).toHaveText('New messages: 1');
      await expect(composer).toHaveValue('draft that must survive the poll');
      await shot(admin.page, 'chat-pill-light');

      await pill.click();
      await expect(pill).toHaveCount(0);
      await expect(panel.locator('[data-chat-message]').last()).toBeInViewport();
      await expect(panel.locator('[data-chat-message]').last()).toContainText('live reply from the second user');
      await composer.fill('');
    } finally {
      await admin.context.close();
      await reviewer.context.close();
    }
  });

  test('Live refresh: grid — the other user\'s edit raises the banner; Refresh shows it and keeps my edit in flight', async ({ browser }) => {
    const admin = await openAs(browser, Admin);
    const reviewer = await openAs(browser, Reviewer);

    try {
      // The reviewer finds a column both rows 0 and 1 accept and writes a first value (setup).
      const target = await writableColumn(reviewer.page.request);
      const first = `${String(4000 + Math.floor(Math.random() * 999))}`;
      await reviewerWrites(reviewer.page.request, target.row0, target.column, Number(first));

      // The administrator opens the sheet on that table.
      const { page } = admin;
      await page.goto(`${DocumentUrl}&sheet=${encodeURIComponent(table.sheetCode)}&table=${encodeURIComponent(table.tableCode)}`);
      const grid = page.locator('revo-grid').first();
      const firstCell = grid.locator('revogr-data[type="rgRow"] .rgCell[data-rgrow="0"]', { hasText: first });
      await expect(firstCell).toBeVisible({ timeout: 90_000 });
      const col = await firstCell.getAttribute('data-rgcol');
      if (col === null) throw new Error('the cell has no data-rgcol');
      await expect(page.getByTestId('sheet-refresh')).toBeVisible();

      // Own edit in row 1 of the same column, held on its way to the server (in flight).
      let release: () => void = () => undefined;
      const gate = new Promise<void>((resolve) => {
        release = resolve;
      });
      let held = 0;
      await page.route(`**/api/v1/documents/${DocumentId}/cells`, async (route: Route) => {
        if (route.request().method() !== 'PATCH') return route.continue();
        held += 1;
        await gate;
        return route.continue();
      });
      const mine = grid.locator(`revogr-data[type="rgRow"] .rgCell[data-rgrow="1"][data-rgcol="${col}"]`);
      await mine.click();
      await page.waitForTimeout(300);
      await page.keyboard.type('777');
      await page.keyboard.press('Enter');
      await expect(mine).toHaveText('777');
      await expect.poll(() => held, { timeout: 15_000 }).toBeGreaterThan(0);
      written.set(`${target.row1.rowKey}:${target.column}`, {
        tableInstanceId: table.tableInstanceId,
        rowKey: target.row1.rowKey,
        columnCode: target.column,
        original: target.row1.cells[target.column],
      });

      // The reviewer changes row 0: the administrator gets the polite banner within one marker interval.
      const second = `${String(5000 + Math.floor(Math.random() * 999))}`;
      await reviewerWrites(reviewer.page.request, target.row0, target.column, Number(second));
      const banner = page.getByTestId('sheet-live-banner');
      await expect(banner).toBeVisible({ timeout: SlowPoll });
      await expect(banner).toContainText(`Updated by ${Reviewer.shown} at`);
      await expect(page.getByTestId('sheet-live-host')).toHaveAttribute('aria-live', 'polite');
      await expect(page.getByTestId('sheet-live-host')).toHaveAttribute('role', 'status');
      await shot(page, 'grid-banner-light');
      expect(await axeViolations(page, 'color-contrast'), 'light: axe color-contrast with the banner').toEqual([]);

      // Refresh while my PATCH is still held: the reload waits for it — my value stays on screen.
      await page.getByTestId('sheet-live-refresh').click();
      await page.waitForTimeout(1_500);
      await expect(mine).toHaveText('777');
      release();

      const firstCellNow = grid.locator(`revogr-data[type="rgRow"] .rgCell[data-rgrow="0"][data-rgcol="${col}"]`);
      await expect(firstCellNow).toHaveText(second, { timeout: 30_000 });
      await expect(mine).toHaveText('777');
      await expect(banner).toHaveCount(0);
      await page.unroute(`**/api/v1/documents/${DocumentId}/cells`);

      // The server has both: the reviewer's value and mine.
      const after = await sliceOf(admin.page.request, table.tableInstanceId);
      const row1 = after.rows.find((row) => row.rowKey === target.row1.rowKey);
      expect(String(row1?.cells[target.column])).toBe('777');

      // My own edit never raises the banner: one more marker interval without it.
      await page.waitForTimeout(SlowPoll);
      await expect(banner).toHaveCount(0);
      await shot(page, 'grid-after-refresh-light');
    } finally {
      await admin.context.close();
      await reviewer.context.close();
    }
  });

  test('Live refresh: a user who does not see the changed table gets no banner; a hidden sheet answers 404', async ({ browser }) => {
    const admin = await openAs(browser, Admin);

    try {
      await setOperatorGrants(admin.page.request, [
        { resourceKind: 'Project', resourceId: 1, level: 'Write', isDeny: false },
        { resourceKind: 'Table', resourceId: table.tableDefId, level: 'Read', isDeny: true },
      ]);

      const operator = await openAs(browser, Operator);
      try {
        const { page } = operator;
        const polls: number[] = [];
        page.on('response', (response) => {
          if (/\/sheets\/\d+\/changes/.test(response.url())) polls.push(response.status());
        });
        await page.goto(`${DocumentUrl}&sheet=${encodeURIComponent(table.sheetCode)}`);
        await expect(page.getByTestId('sheet-refresh')).toBeVisible({ timeout: 90_000 });

        // The administrator changes the table the operator cannot see.
        const target = await writableColumn(admin.page.request);
        await reviewerWrites(admin.page.request, target.row0, target.column, 6000 + Math.floor(Math.random() * 999));

        await expect.poll(() => polls.length, { timeout: SlowPoll }).toBeGreaterThan(1);
        expect(polls.every((status) => status === 200), `marker answers: ${polls.join(',')}`).toBe(true);
        await expect(page.getByTestId('sheet-live-banner')).toHaveCount(0);

        // A sheet the user cannot see (no such sheet in the document): 404, the poll would stop.
        const hidden = await page.request.get(`/api/v1/documents/${DocumentId}/sheets/987654/changes?periodKey=${PeriodKey}`);
        expect(hidden.status()).toBe(404);
        // A period the sheet does not accept: 422.
        const bad = await page.request.get(`/api/v1/documents/${DocumentId}/sheets/${String(table.sheetDefId)}/changes?periodKey=1`);
        expect([404, 422]).toContain(bad.status());
      } finally {
        await operator.context.close();
      }
    } finally {
      await setOperatorGrants(admin.page.request, [{ resourceKind: 'Project', resourceId: 1, level: 'Write', isDeny: false }]);
      await admin.context.close();
    }
  });
});

// ── Helpers ─────────────────────────────────────────────────────────────────────

async function post(request: APIRequestContext, body: string): Promise<void> {
  const response = await request.post(`/api/v1/documents/${DocumentId}/tables/${encodeURIComponent(table.tableCode)}/messages`, {
    data: { body, periodKey: Number(PeriodKey), clientMessageId: randomUUID() },
  });
  expect(response.status(), `POST message: ${await response.text()}`).toBeLessThan(300);
  const created = (await response.json()) as { id: number };
  messages.push({ tableCode: table.tableCode, id: created.id });
}

function unreadOf(label: string | null): number {
  const match = /(\d+) unread/.exec(label ?? '');

  return match === null ? 0 : Number(match[1]);
}

async function sliceOf(request: APIRequestContext, tableInstanceId: number): Promise<SliceBody> {
  const response = await request.get(`/api/v1/documents/${DocumentId}/tables/${String(tableInstanceId)}`);
  expect(response.ok(), `GET slice: ${String(response.status())}`).toBe(true);

  return (await response.json()) as SliceBody;
}

async function patchCell(
  request: APIRequestContext,
  slice: SliceBody,
  row: SliceBody['rows'][number],
  columnCode: string,
  value: unknown,
): Promise<number> {
  const response = await request.patch(`/api/v1/documents/${DocumentId}/cells`, {
    data: {
      tableInstanceId: slice.tableInstanceId,
      periodKey: slice.periodKey,
      origin: 'UserEdit',
      rows: [{ rowKey: row.rowKey, baseVersion: row.rowVersion, cells: [{ columnCode, value }] }],
    },
  });

  return response.status();
}

interface Target {
  readonly column: string;
  readonly row0: SliceBody['rows'][number];
  readonly row1: SliceBody['rows'][number];
}

/** A column of the first table both row 0 and row 1 accept (probed with their own values, so nothing changes). */
async function writableColumn(request: APIRequestContext): Promise<Target> {
  const slice = await sliceOf(request, table.tableInstanceId);
  const [row0, row1] = slice.rows;
  if (row0 === undefined || row1 === undefined) throw new Error('the stand table has fewer than two rows');

  for (const candidate of slice.columns.slice(0, 8)) {
    const probe = 1000 + Math.floor(Math.random() * 999);
    if ((await patchCell(request, slice, row0, candidate.code, probe)) !== 200) continue;
    written.set(`${row0.rowKey}:${candidate.code}`, {
      tableInstanceId: slice.tableInstanceId,
      rowKey: row0.rowKey,
      columnCode: candidate.code,
      original: row0.cells[candidate.code],
    });
    const fresh = await sliceOf(request, table.tableInstanceId);
    const freshRow0 = fresh.rows[0];
    const freshRow1 = fresh.rows[1];
    if (freshRow0 === undefined || freshRow1 === undefined) continue;

    return { column: candidate.code, row0: freshRow0, row1: freshRow1 };
  }

  throw new Error('no column of the first 8 accepts a number in row 0');
}

/** Writes `value` into row 0 with the CURRENT version of the row (the server's, not the cached one). */
async function reviewerWrites(request: APIRequestContext, row0: SliceBody['rows'][number], column: string, value: number): Promise<void> {
  const fresh = await sliceOf(request, table.tableInstanceId);
  const row = fresh.rows.find((candidate) => candidate.rowKey === row0.rowKey) ?? row0;
  const status = await patchCell(request, fresh, row, column, value);
  expect(status, 'PATCH by the other user').toBe(200);
  if (!written.has(`${row0.rowKey}:${column}`)) {
    written.set(`${row0.rowKey}:${column}`, {
      tableInstanceId: fresh.tableInstanceId,
      rowKey: row0.rowKey,
      columnCode: column,
      original: row0.cells[column],
    });
  }
}

async function shot(page: Page, name: string): Promise<void> {
  await page.waitForTimeout(400);
  await page.screenshot({ path: path.join(Shots, `${name}.png`) });
}

async function openAs(
  browser: Browser,
  user: { user: string; password: string },
  scheme: 'light' | 'dark' = 'light',
): Promise<{ page: Page; context: BrowserContext }> {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, colorScheme: scheme });
  const page = await context.newPage();
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(user.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });

  return { page, context };
}

interface GrantRow {
  resourceKind: string;
  resourceId: number;
  level: 'Read' | 'Write';
  isDeny: boolean;
}

/** Grants of `E2EOperator`: the whole list replaced through the API under the administrator (`If-Match`). */
async function setOperatorGrants(request: APIRequestContext, grants: GrantRow[]): Promise<void> {
  const rolesResponse = await request.get('/api/v1/roles');
  expect(rolesResponse.ok(), `GET /roles: ${String(rolesResponse.status())}`).toBe(true);

  const rolesBody = (await rolesResponse.json()) as unknown;
  const roles = (Array.isArray(rolesBody) ? rolesBody : ((rolesBody as { items?: unknown[] }).items ?? [])) as {
    id: number;
    code: string;
  }[];
  const role = roles.find((item) => item.code === 'E2EOperator');
  if (role === undefined) throw new Error('the stand has no role E2EOperator');

  const current = await request.get(`/api/v1/roles/${String(role.id)}/grants`);
  expect(current.ok(), `GET grants: ${String(current.status())}`).toBe(true);
  const etag = current.headers()['etag'];
  if (etag === undefined) throw new Error('GET /roles/{id}/grants returned no ETag');

  const put = await request.put(`/api/v1/roles/${String(role.id)}/grants`, {
    headers: { 'If-Match': etag },
    data: { grants },
  });
  expect(put.ok(), `PUT grants: ${String(put.status())} ${await put.text()}`).toBe(true);
}
