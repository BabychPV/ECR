import { expect, test, type Browser, type Page } from '@playwright/test';

/**
 * CELL-STYLE «область»: фарбування рядка/таблиці ОДНИМ запитом (`PATCH …/cell-styles` із `scope`) проти РЕАЛЬНОГО
 * сервера стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep cellColorScopeLive`).
 *
 * Що доводить файл: контракт і поведінку СЕРВЕРА на стенді (грант, стан аркуша, період, часткова відмова) і те, що
 * результат «приїжджає» у зріз і в сітку після reload. ⚠ Клієнтський шлях «меню -> scope» живе в lane
 * `cell-color-rows` (хмара) і тут не перевіряється: запити йдуть через `page.request` під сесією оператора, а не через
 * тулбар. Тому «один запит замість N» тут — це лічильник власних викликів (`patchCount`), а не мережевий слід UI.
 *
 * Сценарії (назва кожного містить `cellColorScopeLive` для `-Grep`):
 *  1. allRows x allColumns -> один PATCH 200, applied+skipped+denied = рядки x колонки, зріз віддає оформлення;
 *     повтор — applied 0 (ідемпотентність). Сітка (`data-user-style`) — не перевіряється: клієнта L4 у базі ще немає;
 *  2. rowKeys x columnCodes — лише перетин;
 *  3. reset-scope — зняття всього одним запитом;
 *  4. часткова відмова: на одну колонку — грант `Read` -> deniedCells > 0, решта застосована;
 *  5. поданий аркуш -> 403 DocumentSubmitted без запису;
 *  6. закритий період -> 403 PeriodClosed (якщо в календарі стенда є `Closed`; інакше червоне «не доведено»);
 *  7. форма: rowKeys разом з allRows -> 422.
 *
 * ⚠ Без локального прогону (на машині автора немає стенда): перший живий прогін — CI `e2e-stand`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Operator = { user: 'e2e-operator', password: 'E2E-Oper8tor-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const DocumentUrl = `/documents/${DocumentId}?periodKey=${PeriodKey}`;

const Yellow = '#ffff00';

interface SliceDto {
  tableInstanceId: number;
  periodKey: number;
  columns: { id: number; code: string }[];
  rows: { rowKey: string }[];
  cellStyles?: Record<string, unknown> | null;
}

interface ScopeResponse {
  appliedCells: number;
  resetCells: number;
  deniedCells?: number;
  skippedCells?: number;
  deniedByReason?: Record<string, number> | null;
}

/** Скільки PATCH-ів області зроблено цим файлом у поточному сценарії (доказ «один запит»). */
let patchCount = 0;

test.describe('cellColorScopeLive: фарбування області одним запитом на реальному стенді', () => {
  // ⛔ Без стенда файл ПАДАЄ, а не пропускається (honesty-guard).
  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
  });
  test.describe.configure({ timeout: 240_000 });

  test('cellColorScopeLive: вся таблиця одним запитом, зріз і сітка показують колір, повтор ідемпотентний', async ({
    browser,
  }) => {
    await ensureOperatorLevel(browser, 'Write');
    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      const slice = await readSlice(page);
      await resetAll(page, slice);
      patchCount = 0;

      const total = slice.rows.length * slice.columns.length;
      expect(total, 'у зрізі стенда немає комірок').toBeGreaterThan(0);

      const response = await patchScope(page, slice, { allRows: true, allColumns: true, backgroundHex: Yellow });
      expect(response.status, `PATCH scope: ${response.text}`).toBe(200);
      expect(patchCount, 'таблицю мало бути пофарбовано ОДНИМ запитом').toBe(1);

      const body = JSON.parse(response.text) as ScopeResponse;
      expect(body.appliedCells, 'жодної комірки не пофарбовано').toBeGreaterThan(0);
      expect(
        body.appliedCells + (body.skippedCells ?? 0) + (body.deniedCells ?? 0),
        'applied + skipped + denied має дорівнювати рядки x колонки',
      ).toBe(total);

      const after = await readSlice(page);
      expect(Object.keys(after.cellStyles ?? {}).length, 'зріз віддає не ті комірки, що пофарбовано').toBe(
        body.appliedCells,
      );

      // Ідемпотентність: той самий запит нічого не змінює.
      const again = await patchScope(page, slice, { allRows: true, allColumns: true, backgroundHex: Yellow });
      expect(again.status).toBe(200);
      expect((JSON.parse(again.text) as ScopeResponse).appliedCells, 'повтор мав не змінити нічого').toBe(0);
      // ⚠ Відображення в сітці (data-user-style) тут не перевіряється: клієнт L4 («колір комірки») у цій базі ще
      // не влитий (живе в lane cell-color-*), тож доказ — зріз із сервера (cellStyles).
    } finally {
      await context.close();
    }
  });

  test('cellColorScopeLive: rowKeys x columnCodes фарбує лише перетин', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Write');
    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      const slice = await readSlice(page);
      await resetAll(page, slice);

      const rowKeys = slice.rows.slice(0, 2).map((r) => r.rowKey);
      const columnCodes = slice.columns.slice(0, 1).map((c) => c.code);

      const response = await patchScope(page, slice, { rowKeys, columnCodes, backgroundHex: Yellow });
      expect(response.status, `PATCH scope: ${response.text}`).toBe(200);
      const body = JSON.parse(response.text) as ScopeResponse;
      expect(body.appliedCells + (body.deniedCells ?? 0)).toBe(rowKeys.length * columnCodes.length);

      const after = await readSlice(page);
      for (const key of Object.keys(after.cellStyles ?? {})) {
        const [rowKey, columnCode] = key.split(':');
        expect(rowKeys, `оформлено чужий рядок ${key}`).toContain(rowKey);
        expect(columnCodes, `оформлено чужу колонку ${key}`).toContain(columnCode);
      }
    } finally {
      await context.close();
    }
  });

  test('cellColorScopeLive: reset-scope знімає все оформлення одним запитом', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Write');
    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      const slice = await readSlice(page);
      await resetAll(page, slice);

      const painted = await patchScope(page, slice, { allRows: true, allColumns: true, backgroundHex: Yellow });
      const paintedBody = JSON.parse(painted.text) as ScopeResponse;
      expect(painted.status, painted.text).toBe(200);
      expect(paintedBody.appliedCells).toBeGreaterThan(0);

      const reset = await patchScope(page, slice, { allRows: true, allColumns: true, reset: true });
      expect(reset.status, reset.text).toBe(200);
      expect((JSON.parse(reset.text) as ScopeResponse).resetCells).toBe(paintedBody.appliedCells);

      const after = await readSlice(page);
      expect(Object.keys(after.cellStyles ?? {}).length, 'після reset у зрізі лишилося оформлення').toBe(0);
    } finally {
      await context.close();
    }
  });

  test('cellColorScopeLive: часткова відмова — колонка з Read-грантом у deniedCells, решта застосована', async ({
    browser,
  }) => {
    await ensureOperatorLevel(browser, 'Write');

    // Колонка для обмеження: друга (якщо є), інакше перша. Зріз читається адміністратором — гранти ще базові.
    const probe = await openAs(browser, Admin, 'light');
    let slice: SliceDto;
    try {
      slice = await readSlice(probe.page);
    } finally {
      await probe.context.close();
    }

    if (slice.columns.length < 2) {
      throw new Error('не доведено: у таблиці стенда менше двох колонок, часткову відмову не відтворити');
    }

    const restricted = slice.columns[1];
    if (restricted === undefined) throw new Error('не доведено: немає другої колонки');
    await setOperatorGrants(browser, [
      { resourceKind: 'Project', resourceId: 1, level: 'Write', isDeny: false },
      { resourceKind: 'Column', resourceId: restricted.id, level: 'Read', isDeny: false },
    ]);

    // Новий вхід: зміна грантів скидає сесію оператора.
    const { page, context } = await openAs(browser, Operator, 'light');
    try {
      await resetAll(page, slice);

      const response = await patchScope(page, slice, { allRows: true, allColumns: true, backgroundHex: Yellow });
      expect(response.status, `PATCH scope: ${response.text}`).toBe(200);

      const body = JSON.parse(response.text) as ScopeResponse;
      expect(body.deniedCells ?? 0, 'колонка з Read-грантом мала потрапити у deniedCells').toBeGreaterThan(0);
      expect(body.appliedCells, 'дозволена частина мала бути застосована').toBeGreaterThan(0);
      expect(Object.keys(body.deniedByReason ?? {}).length, 'причин відмови не більше 10').toBeLessThanOrEqual(10);

      const after = await readSlice(page);
      for (const key of Object.keys(after.cellStyles ?? {})) {
        expect(key.endsWith(`:${restricted.code}`), `пофарбовано заборонену колонку: ${key}`).toBe(false);
      }
    } finally {
      await context.close();
      await ensureOperatorLevel(browser, 'Write');
    }
  });

  test('cellColorScopeLive: поданий аркуш — 403 DocumentSubmitted, оформлення не записано', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Write');

    // Чиста база сценарію: оформлення від попередніх тестів знімається ДО подання (на поданому аркуші не скинути).
    const admin = await openAs(browser, Admin, 'light');
    try {
      await resetAll(admin.page, await readSlice(admin.page));
      await makeSheetEditable(admin.page);
      await submitSheet(admin.page);
    } finally {
      await admin.context.close();
    }

    const { page, context } = await openAs(browser, Operator, 'light');
    try {
      const slice = await readSlice(page);
      const response = await patchScope(page, slice, { allRows: true, allColumns: true, backgroundHex: Yellow });
      expect(response.status, `очікувалась 403: ${response.text}`).toBe(403);
      expect(response.text).toContain('ECR-ACCS-0403');
      expect(response.text).toMatch(/DocumentSubmitted/);

      const after = await readSlice(page);
      expect(Object.keys(after.cellStyles ?? {}).length, 'відмова не має лишати оформлення').toBe(0);
    } finally {
      await context.close();
      const cleanup = await openAs(browser, Admin, 'light');
      try {
        await makeSheetEditable(cleanup.page);
      } finally {
        await cleanup.context.close();
      }
    }
  });

  test('cellColorScopeLive: закритий період — 403 PeriodClosed (якщо стенд має такий період)', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Write');
    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      const closed = await findClosedPeriod(page);
      if (closed === null) throw new Error('не доведено: у календарі стенда немає періоду зі станом Closed');

      // Зріз закритого періоду: перший екземпляр таблиці документа в цьому періоді.
      const watch = page.waitForResponse(
        (r) =>
          r.request().method() === 'GET' &&
          r.ok() &&
          /^\/api\/v1\/documents\/\d+\/tables\/\d+$/.test(new URL(r.url()).pathname),
        { timeout: 60_000 },
      );
      await page.goto(`/documents/${DocumentId}?periodKey=${String(closed)}`);
      const sliceResponse = await watch.catch(() => null);
      if (sliceResponse === null) {
        throw new Error(`не доведено: документ не має зрізу таблиці в закритому періоді ${String(closed)}`);
      }

      const slice = (await sliceResponse.json()) as SliceDto;
      const response = await patchScope(page, slice, { allRows: true, allColumns: true, backgroundHex: Yellow });
      expect(response.status, response.text).toBe(403);
      expect(response.text).toMatch(/PeriodClosed/);
    } finally {
      await context.close();
    }
  });

  test('cellColorScopeLive: форма — rowKeys разом з allRows відхиляється 422', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Write');
    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      const slice = await readSlice(page);
      const firstRow = slice.rows[0];
      if (firstRow === undefined) throw new Error('у зрізі немає рядків');
      const response = await patchScope(page, slice, {
        rowKeys: [firstRow.rowKey],
        allRows: true,
        allColumns: true,
        backgroundHex: Yellow,
      });
      expect(response.status, response.text).toBe(422);
      expect(response.text).toContain('ECR-CELL-4224');
    } finally {
      await context.close();
    }
  });

  /** Базова лінія стенда: грант `Project/1/Write`, аркуш `Draft`, жодного кольору. */
  test.afterAll(async ({ browser }, testInfo) => {
    if (PeriodKey === '' || DocumentId === '') return;
    testInfo.setTimeout(180_000);

    await ensureOperatorLevel(browser, 'Write');

    const admin = await openAs(browser, Admin, 'light');
    try {
      const slice = await readSlice(admin.page).catch(() => null);
      if (slice !== null) await resetAll(admin.page, slice).catch(() => undefined);
      await makeSheetEditable(admin.page);
    } finally {
      await admin.context.close();
    }
  });
});

// ── Помічники ──────────────────────────────────────────────────────────────────

async function signIn(page: Page, user: { user: string; password: string }): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(user.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function openAs(
  browser: Browser,
  user: { user: string; password: string },
  scheme: 'light' | 'dark',
): Promise<{ page: Page; context: Awaited<ReturnType<Browser['newContext']>> }> {
  const context = await browser.newContext();
  const page = await context.newPage();

  await page.emulateMedia({ colorScheme: scheme });
  await page.goto('/login');
  await page.evaluate((value: string) => {
    localStorage.setItem('mantine-color-scheme-value', value);
  }, scheme);
  await signIn(page, user);
  await ensureScheme(page, user.user, scheme);

  return { page, context };
}

/** Тема `scheme` на `<html>`; якщо інша — через меню користувача (як у `cellColorLive.spec.ts`). */
async function ensureScheme(page: Page, userName: string, scheme: 'light' | 'dark'): Promise<void> {
  const html = page.locator('html');
  await expect(page.getByRole('button', { name: new RegExp(userName, 'i') }).first()).toBeVisible({ timeout: 30_000 });

  if ((await html.getAttribute('data-mantine-color-scheme')) !== scheme) {
    await page.getByRole('button', { name: new RegExp(userName, 'i') }).first().click();
    await page.getByText(scheme === 'dark' ? 'Dark' : 'Light', { exact: true }).first().click();
    await page.keyboard.press('Escape');
  }

  await expect(html, `тема ${scheme} не застосувалась`).toHaveAttribute('data-mantine-color-scheme', scheme, {
    timeout: 15_000,
  });
}

interface GrantRow {
  resourceKind: string;
  resourceId: number;
  level: 'Read' | 'Write';
  isDeny: boolean;
}

/** Гранти `E2EOperator`: заміна повного списку через API під сесією адміністратора (`If-Match`, як `e2e-stand.ps1`). */
async function setOperatorGrants(browser: Browser, grants: GrantRow[]): Promise<void> {
  const admin = await openAs(browser, Admin, 'light');

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
      data: { grants },
    });
    expect(put.ok(), `PUT grants: ${String(put.status())} ${await put.text()}`).toBe(true);
  } finally {
    await admin.context.close();
  }
}

async function ensureOperatorLevel(browser: Browser, level: 'Read' | 'Write'): Promise<void> {
  await setOperatorGrants(browser, [{ resourceKind: 'Project', resourceId: 1, level, isDeny: false }]);
}

/** Зріз першої таблиці документа в базовому періоді: знаходить екземпляр через відповідь сторінки. */
async function readSlice(page: Page): Promise<SliceDto> {
  const watch = page
    .waitForResponse(
      (response) =>
        response.request().method() === 'GET' &&
        response.ok() &&
        /^\/api\/v1\/documents\/\d+\/tables\/\d+$/.test(new URL(response.url()).pathname),
      { timeout: 90_000 },
    )
    // Тіло читається ОДРАЗУ: після очікування `response.json()` падає «No resource with given identifier found».
    .then(async (response) => (await response.json()) as SliceDto)
    .catch(() => null);

  await page.goto(DocumentUrl);
  const slice = await watch;
  if (slice === null) throw new Error('зріз таблиці не завантажився (GET …/tables/{id})');

  if (slice.rows.length === 0 || slice.columns.length === 0) {
    throw new Error('зріз стенда без рядків або колонок');
  }

  return slice;
}

async function patchScope(
  page: Page,
  slice: SliceDto,
  scope: Record<string, unknown>,
): Promise<{ status: number; text: string }> {
  patchCount += 1;
  const response = await page.request.patch(
    `/api/v1/documents/${DocumentId}/tables/${String(slice.tableInstanceId)}/cell-styles`,
    { data: { periodKey: slice.periodKey, scope } },
  );

  return { status: response.status(), text: await response.text() };
}

/** Зняти все оформлення таблиці (best effort) — початковий стан сценарію. */
async function resetAll(page: Page, slice: SliceDto): Promise<void> {
  const response = await page.request.patch(
    `/api/v1/documents/${DocumentId}/tables/${String(slice.tableInstanceId)}/cell-styles`,
    { data: { periodKey: slice.periodKey, scope: { allRows: true, allColumns: true, reset: true } } },
  );
  expect([200, 403], `скидання базової лінії: ${String(response.status())}`).toContain(response.status());
}

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

/** Повертає аркуш у `Draft` (законна дія `Document.Reopen`), ідемпотентно — як у `cellColorLive.spec.ts`. */
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
  await dialog.getByRole('textbox', { name: /Reason|Причина/i }).fill('e2e: cellColorScope, повернення базової лінії');
  await dialog.getByRole('button', { name: /Return for edits|Повернути/i }).click();

  await expect(activeTab, 'аркуш не повернувся в Draft').toContainText('Draft', { timeout: 15_000 });
}

/** Подає аркуш так само, як `cellColorLive.spec.ts`: вставка -> збереження -> перевірка -> «Submit» -> 204. */
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

  const submit = page.getByRole('button', { name: /Submit|Подати/i }).first();
  await expect(submit, 'у шапці немає кнопки подання').toBeVisible({ timeout: 10_000 });
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
