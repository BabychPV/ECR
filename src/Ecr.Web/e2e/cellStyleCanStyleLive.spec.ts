import { expect, test, type Browser, type Page } from '@playwright/test';
import { chooseSubmitPeriod } from './submitPeriod';

/**
 * CELL-STYLE P1A: ознака «можна фарбувати» (`styleAccess` у зрізі) РІВНА рішенню запису `PATCH …/cell-styles` на
 * РЕАЛЬНОМУ сервері стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep cellStyleCanStyleLive`).
 *
 * Сценарії (назва кожного містить `cellStyleCanStyleLive` для `-Grep`):
 *  1. Write -> `canStyle = true`, `deniedReason = null`;
 *  2. Read-грант -> `canStyle = false` + `deniedReason`;
 *  3. аркуш Submitted -> `canStyle = false`, `DocumentSubmitted`;
 *  4. закритий період (якщо в календарі стенда є `Closed`) -> `canStyle = false`, `PeriodClosed`;
 *  5. виконавець передачі: з ексклюзивною передачею -> `true`; після «Заповнено» -> `false`, `AssignmentFilled`;
 *  6. кожного разу узгодженість з реальним PATCH: де слайс каже «заборонено» - 403, де «можна» - 200.
 *
 * ⚠ Рядок «лише читання» (`styleDeniedRowKeys`) стенд не має (у стенді немає `RowDef.IsReadOnly`), тому тут він
 * перевіряється лише ЗАГАЛЬНИМ правилом узгодженості (якщо перелік не порожній - PATCH на ці рядки 403); сам випадок
 * доведено в `CellStylesCanStyleApiTests` на справжній СУБД (Api), не тут.
 *
 * ⚠ Написано без локального прогону (на машині автора немає стенда): перший живий прогін - CI.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Operator = { user: 'e2e-operator', password: 'E2E-Oper8tor-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const DocumentUrl = `/documents/${DocumentId}?periodKey=${PeriodKey}`;

interface StyleAccess {
  canStyle: boolean;
  deniedReason: string | null;
  styleDeniedRowKeys: string[];
  styleDeniedColumnCodes: string[];
  styleDeniedCells: Record<string, string>;
  truncated: boolean;
}

interface Slice {
  tableInstanceId: number;
  periodKey: number;
  columns: { code: string }[];
  rows: { rowKey: string }[];
  styleAccess: StyleAccess;
}

test.describe('cellStyleCanStyleLive: ознака «можна фарбувати» = рішення запису (CELL-STYLE P1A)', () => {
  // ⛔ Без стенда файл ПАДАЄ, а не пропускається (honesty-guard).
  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
  });
  test.describe.configure({ timeout: 240_000 });

  test('cellStyleCanStyleLive: Write - можна фарбувати, узгоджено з PATCH', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Write');
    const { page, context } = await openAs(browser, Operator);

    try {
      const slice = await openSlice(page);
      expect(slice.styleAccess.canStyle, 'Write мав дозволяти фарбування').toBe(true);
      expect(slice.styleAccess.deniedReason).toBeNull();
      await expectConsistent(page, slice);
    } finally {
      await context.close();
    }
  });

  test('cellStyleCanStyleLive: Read-грант - заборонено з причиною, PATCH відмовляє', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Read');
    const { page, context } = await openAs(browser, Operator);

    try {
      const slice = await openSlice(page);
      expect(slice.styleAccess.canStyle, 'Read не може фарбувати').toBe(false);
      expect(slice.styleAccess.deniedReason, 'немає причини відмови').toMatch(/\S+/);
      await expectConsistent(page, slice);
    } finally {
      await context.close();
      await ensureOperatorLevel(browser, 'Write');
    }
  });

  test('cellStyleCanStyleLive: поданий аркуш - заборонено (DocumentSubmitted), PATCH відмовляє', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Write');

    const admin = await openAs(browser, Admin);
    try {
      await makeSheetEditable(admin.page);
      await submitSheet(admin.page);
    } finally {
      await admin.context.close();
    }

    const { page, context } = await openAs(browser, Operator);
    try {
      const slice = await openSlice(page);
      expect(slice.styleAccess.canStyle, 'поданий аркуш не можна фарбувати').toBe(false);
      expect(slice.styleAccess.deniedReason).toBe('DocumentSubmitted');
      await expectConsistent(page, slice);
    } finally {
      await context.close();
      const cleanup = await openAs(browser, Admin);
      try {
        await makeSheetEditable(cleanup.page);
      } finally {
        await cleanup.context.close();
      }
    }
  });

  test('cellStyleCanStyleLive: закритий період - заборонено (PeriodClosed), якщо стенд має такий період', async ({
    browser,
  }) => {
    await ensureOperatorLevel(browser, 'Write');
    const { page, context } = await openAs(browser, Operator);

    try {
      const closed = await findClosedPeriod(page);
      if (closed === null) throw new Error('не доведено: у календарі стенда немає періоду зі станом Closed');

      const slice = await openSlice(page, closed).catch(() => null);
      if (slice === null) {
        throw new Error(`не доведено: документ не має зрізу таблиці в закритому періоді ${String(closed)}`);
      }

      expect(slice.styleAccess.canStyle, 'закритий період не можна фарбувати').toBe(false);
      expect(slice.styleAccess.deniedReason).toBe('PeriodClosed');
      await expectConsistent(page, slice);
    } finally {
      await context.close();
    }
  });

  test('cellStyleCanStyleLive: виконавець передачі - з передачею можна, після Fill заборонено', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Read');

    const admin = await openAs(browser, Admin);
    const exec = await openAs(browser, Operator);
    let assignmentId: number | null = null;

    try {
      // Без передачі виконавець (Read) фарбувати не може.
      const before = await openSlice(exec.page);
      expect(before.styleAccess.canStyle, 'без передачі Read не фарбує').toBe(false);
      await expectConsistent(exec.page, before);

      const sheetCode = await sheetCodeOf(admin.page, before.tableInstanceId);
      const executorId = await candidateId(admin.page, /E2E operator|e2e-operator/i);

      const created = await admin.page.request.post(`/api/v1/documents/${DocumentId}/edit-assignments`, {
        data: { slice: { sheetCode }, recipientUserIds: [executorId], isExclusive: true },
      });
      expect(created.ok(), `створення передачі: ${String(created.status())} ${await created.text()}`).toBe(true);
      assignmentId = ((await created.json()) as { id: number }).id;

      // Підйом передачею: Read -> Write у зрізі передачі.
      const lifted = await openSlice(exec.page);
      expect(lifted.styleAccess.canStyle, 'з передачею виконавець має фарбувати').toBe(true);
      await expectConsistent(exec.page, lifted);

      // «Заповнено»: виконавець більше не пише.
      const filled = await exec.page.request.post(
        `/api/v1/documents/${DocumentId}/edit-assignments/${String(assignmentId)}/fill`,
      );
      expect(filled.ok(), `fill: ${String(filled.status())} ${await filled.text()}`).toBe(true);

      const after = await openSlice(exec.page);
      expect(after.styleAccess.canStyle, 'після Fill виконавець не фарбує').toBe(false);
      expect(after.styleAccess.deniedReason).toBe('AssignmentFilled');
      await expectConsistent(exec.page, after);
    } finally {
      await exec.context.close();
      if (assignmentId !== null) {
        // Best effort: повернути стенд до лінії (передачу знімає власник).
        await admin.page.request
          .delete(`/api/v1/documents/${DocumentId}/edit-assignments/${String(assignmentId)}`)
          .catch(() => undefined);
      }

      await admin.context.close();
      await ensureOperatorLevel(browser, 'Write');
    }
  });

  test.afterAll(async ({ browser }, testInfo) => {
    if (PeriodKey === '' || DocumentId === '') return;
    testInfo.setTimeout(180_000);

    await ensureOperatorLevel(browser, 'Write');

    const admin = await openAs(browser, Admin);
    try {
      await makeSheetEditable(admin.page);
    } finally {
      await admin.context.close();
    }
  });
});

// ── Помічники ──────────────────────────────────────────────────────────────────

/**
 * Для вибірки комірок зрізу: передбачення зі `styleAccess` проти реального PATCH (200 / 403). Лише ті, що прийняті,
 * знімаються `reset`, тож стенд лишається без кольору.
 */
async function expectConsistent(page: Page, slice: Slice): Promise<void> {
  const access = slice.styleAccess;
  expect(access.truncated, 'переліки обрізано - вибірка неточна').toBe(false);

  const rows = slice.rows.slice(0, 3).map((r) => r.rowKey);
  const columns = slice.columns.slice(0, 3).map((c) => c.code);
  expect(rows.length, 'у зрізі немає рядків').toBeGreaterThan(0);
  expect(columns.length, 'у зрізі немає колонок').toBeGreaterThan(0);

  for (const rowKey of rows) {
    for (const columnCode of columns) {
      const predicted =
        access.canStyle &&
        !access.styleDeniedRowKeys.includes(rowKey) &&
        !access.styleDeniedColumnCodes.includes(columnCode) &&
        !(`${rowKey}:${columnCode}` in access.styleDeniedCells);

      const response = await page.request.patch(
        `/api/v1/documents/${DocumentId}/tables/${String(slice.tableInstanceId)}/cell-styles`,
        { data: { periodKey: slice.periodKey, items: [{ rowKey, columnCode, backgroundHex: '#112233' }] } },
      );
      const status = response.status();
      expect([200, 403], `${rowKey}:${columnCode}: ${String(status)} ${await response.text()}`).toContain(status);
      expect(status === 200, `${rowKey}:${columnCode}: слайс каже ${String(predicted)}, PATCH дав ${String(status)}`).toBe(
        predicted,
      );

      if (status === 200) {
        await page.request.patch(
          `/api/v1/documents/${DocumentId}/tables/${String(slice.tableInstanceId)}/cell-styles`,
          { data: { periodKey: slice.periodKey, items: [{ rowKey, columnCode }], reset: true } },
        );
      }
    }
  }
}

/** Відкриває документ у (за потреби іншому) періоді й повертає відповідь зрізу з `styleAccess`. */
async function openSlice(page: Page, periodKey?: number): Promise<Slice> {
  const pending = page
    .waitForResponse(
      (response) =>
        response.request().method() === 'GET' &&
        response.ok() &&
        /^\/api\/v1\/documents\/\d+\/tables\/\d+$/.test(new URL(response.url()).pathname),
      { timeout: 90_000 },
    )
    // Тіло читається ОДРАЗУ: після очікування `response.json()` падає «No resource with given identifier found».
    .then(
      async (response) =>
        (await response.json()) as Omit<Slice, 'styleAccess'> & { styleAccess?: StyleAccess | null },
    )
    .catch(() => null);

  await page.goto(periodKey === undefined ? DocumentUrl : `/documents/${DocumentId}?periodKey=${String(periodKey)}`);
  await expect(page.getByRole('heading').first(), 'документ не відрендерився').toBeVisible({ timeout: 30_000 });

  const raw = await pending;
  if (raw === null) throw new Error('зріз таблиці не завантажився (GET …/tables/{id})');

  if (raw.styleAccess === undefined || raw.styleAccess === null) {
    throw new Error('зріз не має styleAccess (контракт CELL-STYLE P1A)');
  }

  return { ...raw, styleAccess: raw.styleAccess };
}

async function signIn(page: Page, user: { user: string; password: string }): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user.user);
  // Роль, а не підпис: `getByLabel(/Password/)` резолвиться ще й у кнопку-тумблер видимості пароля.
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(user.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function openAs(
  browser: Browser,
  user: { user: string; password: string },
): Promise<{ page: Page; context: Awaited<ReturnType<Browser['newContext']>> }> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await signIn(page, user);

  return { page, context };
}

/** Рівень гранта `E2EOperator` на `Project/1` через API під адміністратором (як `cellColorLive.spec.ts`). */
async function ensureOperatorLevel(browser: Browser, level: 'Read' | 'Write'): Promise<void> {
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
    if (role === undefined) throw new Error('у стенді немає ролі E2EOperator');

    const current = await request.get(`/api/v1/roles/${String(role.id)}/grants`);
    expect(current.ok(), `GET grants: ${String(current.status())}`).toBe(true);
    const etag = current.headers()['etag'];
    if (etag === undefined) throw new Error('GET /roles/{id}/grants не віддав ETag');

    const put = await request.put(`/api/v1/roles/${String(role.id)}/grants`, {
      headers: { 'If-Match': etag },
      data: { grants: [{ resourceKind: 'Project', resourceId: 1, level, isDeny: false }] },
    });
    expect(put.ok(), `PUT grants (${level}): ${String(put.status())} ${await put.text()}`).toBe(true);
  } finally {
    await admin.context.close();
  }
}

/** Код аркуша таблиці: `GET /documents/{id}/tables` (DocumentTableDto: sheetCode + tableInstanceId). */
async function sheetCodeOf(page: Page, tableInstanceId: number): Promise<string> {
  const response = await page.request.get(`/api/v1/documents/${DocumentId}/tables?periodKey=${PeriodKey}`);
  expect(response.ok(), `GET tables: ${String(response.status())}`).toBe(true);

  const body = (await response.json()) as unknown;
  const list = (Array.isArray(body)
    ? body
    : ((body as { tables?: unknown[]; items?: unknown[] }).tables ?? (body as { items?: unknown[] }).items ?? [])) as {
    sheetCode: string;
    tableInstanceId: number;
  }[];
  const found = list.find((table) => table.tableInstanceId === tableInstanceId);
  if (found === undefined) throw new Error(`не доведено: у списку таблиць немає екземпляра ${String(tableInstanceId)}`);

  return found.sheetCode;
}

/** Id кандидата на передачу за підписом (виконавець - `e2e-operator`). */
async function candidateId(page: Page, name: RegExp): Promise<number> {
  const response = await page.request.get(`/api/v1/documents/${DocumentId}/edit-assignments/candidates?q=${encodeURIComponent('E2E operator')}`);
  expect(response.ok(), `GET candidates: ${String(response.status())}`).toBe(true);

  const list = (await response.json()) as { displayName: string; userId: number }[];
  const found = list.find((candidate) => name.test(candidate.displayName));
  if (found === undefined) throw new Error('не доведено: серед кандидатів передачі немає e2e-operator');

  return found.userId;
}

/** Період зі станом `Closed` із календаря першого проєкту; `null` - такого немає. */
async function findClosedPeriod(page: Page): Promise<number | null> {
  const projects = await page.request.get('/api/v1/projects');
  if (!projects.ok()) return null;

  const first = ((await projects.json()) as { items?: { id: number }[] }).items?.[0];
  if (first === undefined) return null;

  const calendar = await page.request.get(`/api/v1/projects/${String(first.id)}/periods`);
  if (!calendar.ok()) return null;

  const periods = ((await calendar.json()) as { periods?: { periodKey: number; state: string }[] }).periods ?? [];

  return periods.find((period) => period.state === 'Closed')?.periodKey ?? null;
}

/** Повертає аркуш у `Draft` (законна дія `Document.Reopen`), ідемпотентно. */
async function makeSheetEditable(page: Page): Promise<void> {
  await page.goto(DocumentUrl);
  await expect(page.getByRole('heading').first(), 'документ не відрендерився').toBeVisible({ timeout: 30_000 });

  const activeTab = page.getByRole('tab', { selected: true });
  await expect(activeTab, 'у документа немає жодного аркуша').toBeVisible({ timeout: 30_000 });

  const state = (await activeTab.textContent()) ?? '';
  if (!/Submitted|Approved/.test(state)) return;

  const reopen = page.getByRole('button', { name: /Return for edits|Повернути/i }).first();
  await expect(reopen, 'у шапці немає кнопки повернення в роботу').toBeVisible({ timeout: 10_000 });
  await reopen.click();

  const dialog = page.getByRole('dialog');
  await expect(dialog, 'діалог причини не відкрився').toBeVisible({ timeout: 10_000 });
  await dialog.getByRole('textbox', { name: /Reason|Причина/i }).fill('e2e: cellStyleCanStyleLive, повернення лінії');
  await dialog.getByRole('button', { name: /Return for edits|Повернути/i }).click();

  await expect(activeTab, 'аркуш не повернувся в Draft').toContainText('Draft', { timeout: 15_000 });
}

/** Подає аркуш так само, як `keyboardPath.spec.ts`: вставка -> збереження -> перевірка (якщо є) -> «Submit» -> 204. */
async function submitSheet(page: Page): Promise<void> {
  await page.goto(DocumentUrl);
  const grid = page.locator('revo-grid').first();
  await expect(grid.locator('revogr-data .rgCell').first(), 'рядки сітки не завантажилися').toBeVisible({
    timeout: 60_000,
  });

  await grid.locator('revogr-data .rgCell').first().click();
  await page.evaluate(async () => {
    await navigator.clipboard.writeText('101');
  });

  const saved = page.waitForResponse(
    (response) =>
      response.request().method() === 'PATCH' &&
      new URL(response.url()).pathname === `/api/v1/documents/${DocumentId}/cells`,
    { timeout: 90_000 },
  );
  await page.keyboard.press('Control+v');
  expect((await saved).status(), 'сервер не прийняв вставку').toBe(200);

  const validate = page.getByRole('button', { name: /Validate|Перевір/i }).first();
  if ((await validate.count()) > 0) {
    const validated = page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        new URL(response.url()).pathname === `/api/v1/documents/${DocumentId}/validate`,
      { timeout: 90_000 },
    );
    await validate.click();
    expect((await validated).status(), 'сервер не виконав перевірку').toBe(200);
    await page.waitForTimeout(1_500);
  }

  // ✎ D-441: «Подати» подає найраніший неподаний Open/Grace — відкритий період вибирається явно.
  const submit = await chooseSubmitPeriod(page, Number(PeriodKey));
  const submitted = page.waitForResponse(
    (response) =>
      response.request().method() === 'POST' &&
      new URL(response.url()).pathname === `/api/v1/documents/${DocumentId}/submit`,
    { timeout: 90_000 },
  );
  await submit.click();
  expect((await submitted).status(), 'сервер не прийняв подання').toBe(204);

  await expect(page.getByRole('tab', { selected: true }), 'аркуш не перейшов у Submitted').toContainText('Submitted', {
    timeout: 15_000,
  });
}
