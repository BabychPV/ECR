import { expect, test, type APIRequestContext, type APIResponse, type Browser, type BrowserContext, type Page } from '@playwright/test';

/**
 * Live status (LIVE-STATUS): two people on the same document × period in two browser contexts on the REAL stand
 * (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep "Live status"`), no reloads:
 *  - A submits the sheet → B's open page shows the new state (polite "Status changed by A: Submitted", the sheet
 *    banner, the approver's buttons) from the change-marker poll (`GET …/sheets/{id}/changes`, `statusRevision`);
 *  - B rejects from those refreshed buttons → A's open page shows "Rejected" the same way.
 * axe (wcag2a/aa) on the live region with the banner: light and dark × 1440 and 500.
 *
 * Users and the role are temporary (`E2ELS<stamp>`): Approve grant on project 1; `afterAll` returns the sheet to the
 * stand baseline (`Draft`: rejected is already editable; submitted/approved — `Document.Reopen` by e2e-admin), strips
 * the users' roles (users cannot be deleted by API), deletes the role and resets B's theme to light.
 * ⛔ Without a stand the file FAILS (`beforeAll`), it is not skipped (honesty-guard).
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Stamp = Date.now().toString(36).toUpperCase().slice(-6);
const Role = `E2ELS${Stamp}`;
const UserA = { user: `e2e-ls-a-${Stamp.toLowerCase()}`, display: `E2E live A ${Stamp}`, issued: 'E2E-LiveA-2026!', password: 'E2E-LiveA-Work-2026!' };
const UserB = { user: `e2e-ls-b-${Stamp.toLowerCase()}`, display: `E2E live B ${Stamp}`, issued: 'E2E-LiveB-2026!', password: 'E2E-LiveB-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

/** Poll cadence is 30 s; one missed tick plus the re-read. */
const LiveWait = 75_000;

let roleId = 0;
const userIds: number[] = [];
let sheetDefId = 0;
let sheetCode = '';

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

async function login(api: APIRequestContext, user: { user: string; password: string }): Promise<void> {
  const response = await api.post('/api/v1/login/local', { data: { userName: user.user, password: user.password } });
  expect(response.ok(), `вхід ${user.user}: ${String(response.status())}`).toBe(true);
}

async function signIn(page: Page, user: { user: string; password: string }): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(user.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Theme on `<html>`: the server keeps the preference, so it is switched from the user menu. */
async function ensureScheme(page: Page, userName: string, scheme: 'light' | 'dark'): Promise<void> {
  const html = page.locator('html');
  const menu = page.getByRole('button', { name: new RegExp(userName, 'i') }).first();
  await expect(menu).toBeVisible({ timeout: 30_000 });
  if ((await html.getAttribute('data-mantine-color-scheme')) !== scheme) {
    await menu.click();
    await page.getByText(scheme === 'dark' ? 'Dark' : 'Light', { exact: true }).first().click();
    await page.keyboard.press('Escape');
  }
  await expect(html, `тема ${scheme} не застосувалась`).toHaveAttribute('data-mantine-color-scheme', scheme, { timeout: 15_000 });
}

/** axe wcag2a/aa on the live region only (the banner state), with the violating nodes named. */
async function axeLiveHost(page: Page): Promise<string[]> {
  await page.addScriptTag({ path: 'node_modules/axe-core/axe.min.js' });

  return page.evaluate(async () => {
    const axe = (window as unknown as {
      axe: { run: (c: Element, o: object) => Promise<{ violations: { id: string; nodes: { target: unknown[] }[] }[] }> };
    }).axe;
    const host = document.querySelector('[data-testid="sheet-live-host"]');
    if (host === null) return ['sheet-live-host: немає'];
    const result = await axe.run(host, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa'] } });

    return result.violations.flatMap((v) => v.nodes.map((n) => `${v.id}: ${String(n.target.join(' '))}`));
  });
}

async function openDocument(browser: Browser, user: { user: string; password: string }): Promise<{ page: Page; context: BrowserContext }> {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  await signIn(page, user);
  await ensureScheme(page, user.user, 'light');
  // The baseline marker is taken on mount: wait for the first answer of the poll.
  const baseline = page.waitForResponse((r) => r.url().includes(`/sheets/${String(sheetDefId)}/changes`), { timeout: 60_000 });
  await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}&sheet=${encodeURIComponent(sheetCode)}`);
  await baseline;
  // A reload would clear this mark: it proves the update came without one.
  await page.evaluate(() => {
    (window as unknown as { __liveStatusNoReload?: boolean }).__liveStatusNoReload = true;
  });

  return { page, context };
}

async function notReloaded(page: Page): Promise<void> {
  expect(
    await page.evaluate(() => (window as unknown as { __liveStatusNoReload?: boolean }).__liveStatusNoReload === true),
    'сторінку перезавантажено — оновлення мало прийти без перезавантаження',
  ).toBe(true);
}

/** The sheet back to `Draft` (stand baseline): rejected is already editable; submitted/approved — reopen. */
async function toDraft(api: APIRequestContext): Promise<void> {
  // Draft/Rejected are already editable: reopen there is a business-rule refusal (403), not a no-op.
  const summary = await ok<{ sheetStates: Record<string, string> }>(
    await api.get(`/api/v1/documents/${DocumentId}?periodKey=${PeriodKey}`),
    'стан документа',
  );
  const state = summary.sheetStates[sheetCode] ?? 'Draft';
  if (state !== 'Submitted' && state !== 'Approved') return;

  const reopen = await api.post(`/api/v1/documents/${DocumentId}/reopen`, {
    data: { sheetDefId, periodKey: Number(PeriodKey), reason: 'e2e: Live status, базова лінія' },
  });
  expect([204, 409], `reopen: ${String(reopen.status())} ${await reopen.text()}`).toContain(reopen.status());
}

test.describe('Live status: sheet state reaches the other open page without reload', () => {
  test.describe.configure({ mode: 'serial', timeout: 300_000 });
  let admin: APIRequestContext;

  test.beforeAll(async ({ playwright }, info) => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
    test.setTimeout(180_000);
    const baseURL = info.project.use.baseURL;
    admin = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
    await login(admin, Admin);

    const tables = await ok<{ sheetDefId: number; sheetCode: string }[]>(
      await admin.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${PeriodKey}`),
      'таблиці документа',
    );
    const first = tables[0];
    if (first === undefined) throw new Error('у документа стенда немає таблиць');
    sheetDefId = first.sheetDefId;
    sheetCode = first.sheetCode;
    await toDraft(admin);

    const role = await ok<{ roleId?: number; id?: number }>(
      await admin.post('/api/v1/roles', {
        data: { code: Role, nameL10n: { en: `E2E live status ${Stamp}` }, permissionCodes: ['Document.View', 'Document.Reopen'] },
      }),
      'тимчасова роль',
    );
    roleId = role.roleId ?? role.id ?? 0;
    expect(roleId, 'id тимчасової ролі').toBeGreaterThan(0);

    const grants = await admin.get(`/api/v1/roles/${String(roleId)}/grants`);
    const version = grants.headers()['etag'] ?? '';
    expect(version, 'GET grants без ETag').not.toBe('');
    await ok(
      await admin.put(`/api/v1/roles/${String(roleId)}/grants`, {
        headers: { 'If-Match': version },
        data: { grants: [{ resourceKind: 'Project', resourceId: 1, level: 'Approve', isDeny: false }] },
      }),
      'грант Approve на проєкт 1',
    );

    for (const account of [UserA, UserB]) {
      const created = await ok<{ userId?: number; id?: number }>(
        await admin.post('/api/v1/users', {
          data: { userName: account.user, provider: 'Local', sid: null, displayName: account.display, initialPassword: account.issued, roleCodes: [Role] },
        }),
        `користувач ${account.user}`,
      );
      userIds.push(created.userId ?? created.id ?? 0);
      const api = await playwright.request.newContext(baseURL === undefined ? {} : { baseURL });
      await login(api, { user: account.user, password: account.issued });
      await ok(
        await api.post('/api/v1/auth/change-password', { data: { currentPassword: account.issued, newPassword: account.password } }),
        `пароль ${account.user}`,
      );
      await api.dispose();
    }
  });

  test.afterAll(async ({ browser }) => {
    test.setTimeout(180_000);
    const problems: string[] = [];
    if (sheetDefId !== 0) {
      try {
        await toDraft(admin);
      } catch (error) {
        problems.push(`аркуш: ${String(error)}`);
      }
    }
    // B's theme preference (server-side) back to light — while B still has the role and signs in normally.
    if (userIds.length === 2) {
      try {
        const context = await browser.newContext();
        const page = await context.newPage();
        await signIn(page, UserB);
        await ensureScheme(page, UserB.user, 'light');
        await context.close();
      } catch (error) {
        problems.push(`тема B: ${String(error).slice(0, 300)}`);
      }
    }
    for (const id of userIds.filter((value) => value > 0)) {
      const response = await admin.put(`/api/v1/users/${String(id)}/roles`, { data: { roleCodes: [] } });
      if (response.status() >= 300) problems.push(`user ${String(id)}: ${String(response.status())}`);
    }
    if (roleId !== 0) {
      // A role with grants is "in use" (409): the grant set goes first.
      const grants = await admin.get(`/api/v1/roles/${String(roleId)}/grants`);
      const cleared = await admin.put(`/api/v1/roles/${String(roleId)}/grants`, {
        headers: { 'If-Match': grants.headers()['etag'] ?? '' },
        data: { grants: [] },
      });
      if (cleared.status() >= 300) problems.push(`grants: ${String(cleared.status())} ${(await cleared.text()).slice(0, 300)}`);
      const response = await admin.delete(`/api/v1/roles/${String(roleId)}`);
      if (response.status() >= 300 && response.status() !== 404) problems.push(`role: ${String(response.status())} ${(await response.text()).slice(0, 300)}`);
    }
    await admin.dispose();
    expect(problems, 'прибирання стенда').toEqual([]);
  });

  test('Live status: A submits → B sees Submitted; B rejects → A sees Rejected (no reload, axe light/dark 1440/500)', async ({
    browser,
  }) => {
    const a = await openDocument(browser, UserA);
    const b = await openDocument(browser, UserB);

    try {
      // ── 1. A submits (A's own session; a value first, as the stand's empty sheet may have nothing to submit) ──
      const aTables = await ok<{ tableInstanceId: number }[]>(
        await a.context.request.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${PeriodKey}`),
        'таблиці A',
      );
      const slice = await ok<{ tableInstanceId: number; periodKey: number; columns: { code: string }[]; rows: { rowKey: string; rowVersion: string }[] }>(
        await a.context.request.get(`/api/v1/documents/${DocumentId}/tables/${String(aTables[0]?.tableInstanceId ?? 0)}?periodKey=${PeriodKey}`),
        'зріз A',
      );
      const row = slice.rows[0];
      if (row !== undefined) {
        for (const column of slice.columns.slice(0, 12)) {
          const patch = await a.context.request.patch(`/api/v1/documents/${DocumentId}/cells`, {
            data: {
              tableInstanceId: slice.tableInstanceId,
              periodKey: slice.periodKey,
              origin: 'UserEdit',
              rows: [{ rowKey: row.rowKey, baseVersion: row.rowVersion, cells: [{ columnCode: column.code, value: 202 }] }],
            },
          });
          if (patch.ok()) break;
        }
      }
      const submit = await a.context.request.post(`/api/v1/documents/${DocumentId}/submit`, {
        data: { sheetDefId, periodKey: Number(PeriodKey), acknowledgeWarnings: true },
      });
      expect(submit.status(), `submit A: ${await submit.text()}`).toBe(204);

      // ── 2. B: polite banner, the sheet banner and the approver's buttons — without reload ──
      const bBanner = b.page.locator('[data-testid="sheet-status-banner"][data-live-status="Submitted"]');
      await expect(bBanner, 'B не побачив подання без перезавантаження').toBeVisible({ timeout: LiveWait });
      await expect(bBanner).toContainText(UserA.display);
      await expect(b.page.locator('[data-testid="sheet-live-host"]')).toHaveAttribute('aria-live', 'polite');
      await expect(b.page.getByTestId('document-sheet-banner')).toHaveAttribute('data-document-lock', 'sheetSubmitted');
      const reject = b.page.getByRole('button', { name: /^(Reject|Отклонить|Қабылдамау)$/ }).first();
      await expect(reject, 'кнопка відхилення не з’явилась після живого оновлення').toBeVisible({ timeout: 30_000 });
      await expect(reject).not.toHaveAttribute('aria-disabled', 'true');
      await notReloaded(b.page);

      // ── 3. axe on the banner state: light/dark × 1440/500 ──
      for (const scheme of ['light', 'dark'] as const) {
        await ensureScheme(b.page, UserB.user, scheme);
        for (const width of [1440, 500]) {
          await b.page.setViewportSize({ width, height: 900 });
          await expect(bBanner).toBeVisible();
          await expect(b.page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);
          expect(await axeLiveHost(b.page), `axe ${scheme} ${String(width)}`).toEqual([]);
        }
      }
      await b.page.setViewportSize({ width: 1440, height: 900 });
      await ensureScheme(b.page, UserB.user, 'light');
      await notReloaded(b.page);

      // ── 4. B rejects from the refreshed buttons ──
      await reject.click();
      const dialog = b.page.getByRole('dialog');
      await expect(dialog).toBeVisible({ timeout: 15_000 });
      await dialog.getByRole('textbox').first().fill('e2e: Live status — повернути');
      await dialog.getByRole('button', { name: /^(Reject|Отклонить|Қабылдамау)$/ }).click();
      await expect(dialog).toBeHidden({ timeout: 30_000 });

      // ── 5. A: "Rejected" without reload ──
      const aBanner = a.page.locator('[data-testid="sheet-status-banner"][data-live-status="Rejected"]');
      await expect(aBanner, 'A не побачив відхилення без перезавантаження').toBeVisible({ timeout: LiveWait });
      await expect(aBanner).toContainText(UserB.display);
      await expect(a.page.getByTestId('document-sheet-banner')).toContainText('e2e: Live status');
      await notReloaded(a.page);
    } finally {
      await a.context.close();
      await b.context.close();
    }
  });
});
