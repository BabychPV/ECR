import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { axeViolations } from './axeRule';

/**
 * Table chat (TC) on the live stand (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep tableChat`):
 * the button with the unread counter next to Issues / History, the badge in the table title, the right panel bound
 * to the table, the 15 s poll, @mention (server list) → an item in My tasks, edit / delete, a user without
 * `Document.Discuss` (read-only), a user who does NOT see the table (no chat at all), real-browser axe contrast in
 * light and dark, and screenshots (panel closed / open, light / dark, 500 px).
 *
 * Users of the stand: `e2e-admin` and `e2e-reviewer` (role E2EAdmin: Discuss + DiscussModerate) talk to each other;
 * `e2e-operator` (E2EOperator: no Discuss) reads. The scenario is SERIAL: each test builds on the messages before it.
 *
 * Screenshots go to `$ECR_TC_SHOTS` (default `test-results/tablechat-shots`).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!', shown: 'E2E administrator' };
const Reviewer = { user: 'e2e-reviewer', password: 'E2E-Rev1ewer-Work-2026!', shown: 'E2E reviewer' };
const Operator = { user: 'e2e-operator', password: 'E2E-Oper8tor-Work-2026!', shown: 'E2E operator' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const DocumentUrl = `/documents/${DocumentId}?periodKey=${PeriodKey}`;
const Shots = process.env['ECR_TC_SHOTS'] ?? path.join('test-results', 'tablechat-shots');

/** The chat polls every 15 s; one tick plus the request and a margin. */
const PollWait = 45_000;

interface TableInfo {
  readonly tableCode: string;
  readonly tableDefId: number;
}

let chatTable: TableInfo;

test.describe('tableChat: table discussion on the stand', () => {
  // ⛔ Without a stand the file FAILS, it is not skipped (honesty-guard).
  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('no stand: ECR_E2E_PERIOD/ECR_E2E_DOCUMENT are not set (tools/e2e-stand.ps1)');
    }
    mkdirSync(Shots, { recursive: true });
  });
  test.describe.configure({ mode: 'serial', timeout: 300_000 });

  test('tableChat: the tables of the document are known and the chat is empty', async ({ browser }) => {
    const admin = await openAs(browser, Admin);

    try {
      const response = await admin.page.request.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${PeriodKey}`);
      expect(response.ok(), `GET tables: ${String(response.status())}`).toBe(true);
      const tables = (await response.json()) as TableInfo[];
      expect(tables.length, 'the stand document has no tables').toBeGreaterThan(0);
      chatTable = tables[0] as TableInfo;

      const summary = await admin.page.request.get(`/api/v1/documents/${DocumentId}/discussions/summary`);
      expect(summary.status(), `summary: ${await summary.text()}`).toBe(200);
      const body = (await summary.json()) as { items: unknown[]; canWrite: boolean };
      expect(body.canWrite).toBe(true);
      expect(body.items, 'the chat of a fresh stand must be empty').toEqual([]);
    } finally {
      await admin.context.close();
    }
  });

  test('tableChat: the button opens the panel, a message with an @mention goes in and shows up', async ({ browser }) => {
    const admin = await openAs(browser, Admin);

    try {
      const { page } = admin;
      await page.goto(DocumentUrl);
      const button = page.locator('[data-discussion-trigger]');
      await expect(button).toBeVisible({ timeout: 90_000 });
      await expect(button).toHaveAccessibleName('Discussion');
      await expect(page.locator('[data-chat-panel]')).toHaveCount(0);

      // "Before": the page with the panel closed (the header row and the table title).
      await shot(page, 'before-light-admin');

      await button.click();
      const panel = page.locator('[data-chat-panel]');
      await expect(panel).toBeVisible();
      await expect(page).toHaveURL(/[?&]panel=discussion/);
      await expect(panel.locator('[data-chat-empty]')).toBeVisible();

      // The composer takes the focus when a person opened the panel.
      const composer = panel.getByRole('textbox', { name: /^Message/ });
      await expect(composer).toBeFocused();

      await composer.pressSequentially('@E2E rev', { delay: 20 });
      const list = panel.getByRole('listbox');
      await expect(list).toBeVisible({ timeout: 15_000 });
      await list.getByRole('option', { name: /E2E reviewer/ }).click();
      await expect(composer).toHaveValue('@E2E reviewer ');

      await composer.pressSequentially('please check the fuel rate <b>x</b>', { delay: 5 });
      await composer.press('Control+Enter');

      const mine = panel.locator('[data-chat-message]').first();
      await expect(mine).toBeVisible({ timeout: 15_000 });
      await expect(mine.locator('[data-chat-body]')).toContainText('please check the fuel rate <b>x</b>');
      await expect(mine.locator('[data-chat-body] b')).toHaveCount(0);
      await expect(mine.locator('[data-chat-mentioned]')).toHaveText('@E2E reviewer');
      await expect(composer).toHaveValue('');

      // "After": the panel open on the table.
      await shot(page, 'after-light-admin');

      // The badge in the table title and the counter appear after the summary refresh.
      await page.reload();
      await expect(page.locator(`[data-chat-badge="${chatTable.tableCode}"]`)).toBeVisible({ timeout: 90_000 });
    } finally {
      await admin.context.close();
    }
  });

  test('tableChat: the second user sees the unread counter, the mention in My tasks, and reads it live', async ({ browser }) => {
    const reviewer = await openAs(browser, Reviewer);

    try {
      const { page } = reviewer;
      await page.goto(DocumentUrl);
      const button = page.locator('[data-discussion-trigger]');
      await expect(button).toBeVisible({ timeout: 90_000 });
      await expect(button).toHaveAccessibleName('Discussion, 1 unread');

      const badge = page.locator(`[data-chat-badge="${chatTable.tableCode}"]`);
      await expect(badge).toBeVisible();
      await expect(badge).toHaveText('1 · 1 new');

      // My tasks: the mention is an item, and it leads to the discussion.
      await page.getByRole('button', { name: 'My tasks' }).click();
      const block = page.getByTestId('my-tasks-mentions');
      await expect(block).toBeVisible({ timeout: 30_000 });
      await expect(block).toContainText('E2E administrator');
      await expect(block).toContainText('please check the fuel rate');
      await block.getByRole('link', { name: 'Open discussion' }).click();

      const panel = page.locator('[data-chat-panel]');
      await expect(panel).toBeVisible({ timeout: 30_000 });
      await expect(panel).toHaveAttribute('data-chat-table', chatTable.tableCode);
      const first = panel.locator('[data-chat-message]').first();
      await expect(first).toBeVisible();
      await expect(first.locator('[data-chat-new]')).toBeVisible();
      await expect(first.locator('[data-chat-mentioned]')).toHaveText('@E2E reviewer');

      // Opened from the address: the focus was NOT taken from the page.
      await expect(panel.getByRole('textbox', { name: /^Message/ })).not.toBeFocused();

      // Read: the header counter goes away and the badge shows the plain total.
      await expect(button).toHaveAccessibleName('Discussion', { timeout: 30_000 });
      await expect(badge).toHaveText('1', { timeout: 30_000 });

      // Reply.
      const composer = panel.getByRole('textbox', { name: /^Message/ });
      await composer.fill('ack, checking now');
      await composer.press('Control+Enter');
      await expect(panel.locator('[data-chat-message]')).toHaveCount(2, { timeout: 15_000 });

      // The moderator deletes someone else's message (the first one, by the administrator).
      const foreign = panel.locator('[data-chat-message]').first();
      await foreign.locator('[data-chat-delete]').click();
      await foreign.locator('[data-chat-delete-yes]').click();
      await expect(foreign.locator('[data-chat-deleted]')).toHaveText('Message deleted', { timeout: 15_000 });
    } finally {
      await reviewer.context.close();
    }
  });

  test('tableChat: the first user receives the reply and the deletion by the poll, without a reload', async ({ browser }) => {
    const admin = await openAs(browser, Admin);

    try {
      const { page } = admin;
      await page.goto(`${DocumentUrl}&panel=discussion&table=${encodeURIComponent(chatTable.tableCode)}`);
      const panel = page.locator('[data-chat-panel]');
      await expect(panel).toBeVisible({ timeout: 90_000 });
      await expect(panel.locator('[data-chat-message]')).toHaveCount(2, { timeout: 30_000 });
      await expect(panel.locator('[data-chat-deleted]')).toHaveText('Message deleted');
      await expect(panel.locator('[data-chat-message]').nth(1)).toContainText('ack, checking now');

      // A new message of the reviewer arrives with no action here: only the poll can bring it.
      const reviewer = await openAs(browser, Reviewer);
      try {
        await reviewer.page.goto(`${DocumentUrl}&panel=discussion&table=${encodeURIComponent(chatTable.tableCode)}`);
        const composer = reviewer.page.locator('[data-chat-panel]').getByRole('textbox', { name: /^Message/ });
        await expect(composer).toBeVisible({ timeout: 90_000 });
        await composer.fill('live message from the second user');
        await composer.press('Control+Enter');
        await expect(reviewer.page.locator('[data-chat-panel] [data-chat-message]')).toHaveCount(3, { timeout: 15_000 });
      } finally {
        await reviewer.context.close();
      }

      await expect(panel.locator('[data-chat-message]')).toHaveCount(3, { timeout: PollWait });
      await expect(panel.locator('[data-chat-message]').nth(2)).toContainText('live message from the second user');
      await expect(panel.locator('[data-chat-message]').nth(2).locator('[data-chat-new]')).toBeVisible();

      // Edit an own message within the window: "edited" mark, the text changes.
      await composerSend(panel, 'typo mesage');
      const own = panel.locator('[data-chat-message]').nth(3);
      await expect(own).toContainText('typo mesage', { timeout: 15_000 });
      await own.locator('[data-chat-edit]').click();
      const editor = panel.getByRole('textbox', { name: 'Edit message' });
      await editor.fill('typo message fixed');
      await panel.locator('[data-chat-save]').click();
      await expect(own).toContainText('typo message fixed', { timeout: 15_000 });
      await expect(own.locator('[data-chat-edited]')).toBeVisible();

      // The period filter asks the server for the document period only.
      const requested = page.waitForRequest((request) => request.url().includes('/messages?') && request.url().includes(`periodKey=${PeriodKey}`));
      await panel.locator('[data-chat-filter="period"]').click();
      await requested;
      await expect(panel.locator('[data-chat-filter="period"]')).toHaveAttribute('aria-pressed', 'true');

      // Esc closes the panel and returns the focus to the button.
      await page.keyboard.press('Escape');
      await expect(page.locator('[data-chat-panel]')).toHaveCount(0);
      await expect(page).not.toHaveURL(/[?&]panel=/);
      await expect(page.locator('[data-discussion-trigger]')).toBeFocused();
    } finally {
      await admin.context.close();
    }
  });

  test('tableChat: a user without Discuss reads the chat but cannot write in it', async ({ browser }) => {
    const operator = await openAs(browser, Operator);

    try {
      const { page } = operator;
      await page.goto(`${DocumentUrl}&panel=discussion&table=${encodeURIComponent(chatTable.tableCode)}`);
      const panel = page.locator('[data-chat-panel]');
      await expect(panel).toBeVisible({ timeout: 90_000 });
      await expect(panel.locator('[data-chat-message]').first()).toBeVisible({ timeout: 30_000 });
      await expect(panel.locator('[data-chat-readonly]')).toBeVisible();
      await expect(panel.getByRole('textbox')).toHaveCount(0);
      await expect(panel.locator('[data-chat-delete]')).toHaveCount(0);

      // The server says the same.
      const post = await page.request.post(`/api/v1/documents/${DocumentId}/tables/${encodeURIComponent(chatTable.tableCode)}/messages`, {
        data: { body: 'not allowed', periodKey: Number(PeriodKey) },
      });
      expect(post.status()).toBe(403);
    } finally {
      await operator.context.close();
    }
  });

  test('tableChat: a user who does not see the table gets no chat at all', async ({ browser }) => {
    await setOperatorGrants(browser, [
      { resourceKind: 'Project', resourceId: 1, level: 'Write', isDeny: false },
      { resourceKind: 'Table', resourceId: chatTable.tableDefId, level: 'Read', isDeny: true },
    ]);

    try {
      const operator = await openAs(browser, Operator);

      try {
        const { page } = operator;
        const code = encodeURIComponent(chatTable.tableCode);

        // The API: hidden table = hidden chat (404, not 403).
        const messages = await page.request.get(`/api/v1/documents/${DocumentId}/tables/${code}/messages`);
        expect(messages.status()).toBe(404);
        expect(await messages.text()).toContain('ECR-DOC-0404');

        const summary = await page.request.get(`/api/v1/documents/${DocumentId}/discussions/summary`);
        expect(summary.status()).toBe(200);
        const body = (await summary.json()) as { items: { tableCode: string }[] };
        expect(body.items.map((item) => item.tableCode)).not.toContain(chatTable.tableCode);

        // The UI: no badge for the table, no panel even when the address asks for it, no button (nothing to read).
        // The stand document has many tables: the address names the hidden one, so the panel (if it opens at all)
        // must fall back to a table the user sees, and no request may go to the hidden table's chat.
        const hiddenCalls: string[] = [];
        page.on('request', (request) => {
          if (request.url().includes(`/tables/${code}/`)) hiddenCalls.push(request.url());
        });
        await page.goto(`${DocumentUrl}&panel=discussion&table=${code}`);
        await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 90_000 });
        await page.waitForTimeout(3_000);
        await expect(page.locator(`[data-chat-badge="${chatTable.tableCode}"]`)).toHaveCount(0);
        await expect(page.locator(`[data-chat-panel][data-chat-table="${chatTable.tableCode}"]`)).toHaveCount(0);
        expect(hiddenCalls.filter((url) => url.includes('/messages')), 'the hidden table chat was requested').toEqual([]);
      } finally {
        await operator.context.close();
      }
    } finally {
      await setOperatorGrants(browser, [{ resourceKind: 'Project', resourceId: 1, level: 'Write', isDeny: false }]);
    }
  });

  for (const scheme of ['light', 'dark'] as const) {
    test(`tableChat: ${scheme} — axe colour contrast with the panel open, screenshots at desktop and 500 px`, async ({ browser }) => {
      const admin = await openAs(browser, Admin, scheme);

      try {
        const { page } = admin;
        await page.goto(`${DocumentUrl}&panel=discussion&table=${encodeURIComponent(chatTable.tableCode)}`);
        const panel = page.locator('[data-chat-panel]');
        await expect(panel).toBeVisible({ timeout: 90_000 });
        await expect(panel.locator('[data-chat-message]').first()).toBeVisible({ timeout: 30_000 });
        await expect(page.locator(`[data-chat-badge="${chatTable.tableCode}"]`)).toBeVisible();

        await shot(page, `after-${scheme}-panel-desktop`);

        // Real axe: contrast and names on the page with the panel, the badge and the counter.
        for (const rule of ['color-contrast', 'button-name', 'aria-allowed-attr', 'aria-valid-attr-value', 'label']) {
          expect(await axeViolations(page, rule), `${scheme}: axe ${rule}`).toEqual([]);
        }

        // 500 px: the panel is a layer above the page, still usable.
        await page.setViewportSize({ width: 500, height: 800 });
        await expect(panel).toBeVisible();
        const box = await panel.boundingBox();
        expect(box?.width ?? 0, 'the panel must not be wider than the viewport').toBeLessThanOrEqual(500);
        await expect(panel.getByRole('textbox', { name: /^Message/ })).toBeVisible();
        await shot(page, `after-${scheme}-panel-500`);
        expect(await axeViolations(page, 'color-contrast'), `${scheme} 500px: axe color-contrast`).toEqual([]);

        await panel.getByRole('button', { name: 'Close discussion' }).click();
        await shot(page, `before-${scheme}-closed-500`);
      } finally {
        await admin.context.close();
      }
    });
  }
});

async function composerSend(panel: ReturnType<Page['locator']>, text: string): Promise<void> {
  const composer = panel.getByRole('textbox', { name: /^Message/ });
  await composer.fill(text);
  await composer.press('Control+Enter');
}

async function shot(page: Page, name: string): Promise<void> {
  // Let the layout settle (the work area makes room for the column).
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
  await signIn(page, user);
  await ensureScheme(page, user.user, scheme);

  return { page, context };
}

async function signIn(page: Page, user: { user: string; password: string }): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(user.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Theme `scheme` on `<html>`; if it is another one, through the user menu (as in `cellColorLive.spec.ts`). */
async function ensureScheme(page: Page, userName: string, scheme: 'light' | 'dark'): Promise<void> {
  const html = page.locator('html');
  await expect(page.getByRole('button', { name: new RegExp(userName, 'i') }).first()).toBeVisible({ timeout: 30_000 });

  if ((await html.getAttribute('data-mantine-color-scheme')) !== scheme) {
    await page.getByRole('button', { name: new RegExp(userName, 'i') }).first().click();
    await page.getByText(scheme === 'dark' ? 'Dark' : 'Light', { exact: true }).first().click();
    await page.keyboard.press('Escape');
  }

  await expect(html, `theme ${scheme} was not applied`).toHaveAttribute('data-mantine-color-scheme', scheme, {
    timeout: 15_000,
  });
}

interface GrantRow {
  resourceKind: string;
  resourceId: number;
  level: 'Read' | 'Write';
  isDeny: boolean;
}

/** Grants of `E2EOperator`: the whole list replaced through the API under the administrator (`If-Match`). */
async function setOperatorGrants(browser: Browser, grants: GrantRow[]): Promise<void> {
  const admin = await openAs(browser, Admin);

  try {
    const request = admin.page.request;
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
  } finally {
    await admin.context.close();
  }
}
