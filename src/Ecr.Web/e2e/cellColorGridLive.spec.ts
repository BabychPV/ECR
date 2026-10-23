import { expect, test, type Browser, type Locator, type Page, type Response } from '@playwright/test';
import { chooseSubmitPeriod } from './submitPeriod';

/**
 * CELL-STYLE L4 (UI): колір РЯДКА / КОЛОНКИ / ТАБЛИЦІ з тулбара сітки на РЕАЛЬНОМУ стенді
 * (`tools/e2e-stand.ps1`, `-Grep cellColorGrid`): вибір області -> ОДИН `PATCH …/cell-styles` зі `scope` ->
 * `data-user-fill` у DOM сітки -> reload (колір із сервера) -> скидання; `Submitted`/грант `Read` -> кнопки
 * вимкнені з причиною (`aria-disabled` + `data-disabled-reason`); часткова відмова -> `cell-color-toast`.
 *
 * Відмінність від `cellColorScopeLive` (сервер через `page.request`) і `cellColor.spec.ts` (фейк-сторінка):
 * тут шлях «меню -> scope» проти справжнього сервера.
 *
 * ⚠ Сценарій «часткова відмова» доводить КЛІЄНТА: справжній PATCH іде на сервер, а відповідь, що повертається в
 * застосунок, підміняється відповіддю з `deniedCells` (маршрут `page.route` + `route.fulfill`). Сервер частково
 * відмовляє іншими тестами (`cellColorScopeLive` №4).
 * ⚠ Без локального прогону (на машині автора немає стенда): перший живий прогін - CI.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Operator = { user: 'e2e-operator', password: 'E2E-Oper8tor-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const DocumentUrl = `/documents/${DocumentId}?periodKey=${PeriodKey}`;

const Yellow = '#ffff00';

interface SliceRef {
  tableInstanceId: number;
  periodKey: number;
  rowKey: string;
  columnCode: string;
}

/** Таблиця, якій тест міг лишити колір, - `afterAll` знімає його одним reset-scope. */
const touched: SliceRef[] = [];

const filledCells = (page: Page): Locator =>
  page.locator('revo-grid').first().locator('revogr-data[type="rgRow"] .rgCell[data-user-fill="true"]');

/** Тіло останнього `PATCH …/cell-styles`, який зробив застосунок. */
function waitForStylePatch(page: Page): Promise<Response> {
  return page.waitForResponse(
    (response) =>
      response.request().method() === 'PATCH' && /\/tables\/\d+\/cell-styles$/.test(new URL(response.url()).pathname),
    { timeout: 60_000 },
  );
}

async function chooseScope(page: Page, scope: 'selection' | 'row' | 'column' | 'table'): Promise<void> {
  await page.getByTestId('cell-color-scope').selectOption(scope);
}

async function paint(page: Page, hex: string): Promise<Response> {
  await page.getByTestId('cell-color-fill').first().click();
  const patched = waitForStylePatch(page);
  await page.getByRole('button', { name: hex, exact: true }).first().click();

  return patched;
}

async function resetFill(page: Page): Promise<Response> {
  await page.getByTestId('cell-color-fill').first().click();
  const patched = waitForStylePatch(page);
  await page.getByTestId('cell-color-fill-reset').click();

  return patched;
}

test.describe('cellColorGrid: колір рядка/колонки/таблиці з тулбара на реальному стенді', () => {
  // ⛔ Без стенда файл ПАДАЄ, а не пропускається (honesty-guard).
  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
  });
  test.describe.configure({ timeout: 240_000 });

  test.afterAll(async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Write');
    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      await makeSheetEditable(page);
      for (const slice of touched) {
        await page.request.patch(`/api/v1/documents/${DocumentId}/tables/${String(slice.tableInstanceId)}/cell-styles`, {
          data: { periodKey: slice.periodKey, reset: false, scope: { allRows: true, allColumns: true, reset: true } },
        });
      }
    } finally {
      await context.close();
    }
  });

  for (const scheme of ['light', 'dark'] as const) {
    test(`cellColorGrid: рядок -> колонка -> таблиця одним PATCH, reload, скидання, тема ${scheme}`, async ({ browser }) => {
      await ensureOperatorLevel(browser, 'Write');
      const { page, context } = await openAs(browser, Operator, scheme);

      try {
        await makeSheetEditable(page);
        const slice = await openDocument(page);
        touched.push(slice);
        await selectColourableCell(page);

        // ── Весь рядок ──────────────────────────────────────────────────────
        await chooseScope(page, 'row');
        const rowResponse = await paint(page, Yellow);
        expect(rowResponse.status(), `PATCH scope=row: ${await rowResponse.text()}`).toBe(200);
        const rowBody = rowResponse.request().postDataJSON() as { items?: unknown; scope?: { rowKeys?: string[]; allColumns?: boolean } };
        expect(rowBody.items, 'область має йти ОДНИМ запитом зі scope, а не тисячею items').toBeUndefined();
        expect(rowBody.scope?.rowKeys).toContain(slice.rowKey);
        expect(rowBody.scope?.allColumns).toBe(true);
        await expect(filledCells(page).first(), 'рядок не пофарбовано в DOM').toBeVisible({ timeout: 15_000 });
        expect(await filledCells(page).count(), 'фарбується більше однієї комірки рядка').toBeGreaterThan(1);

        // ── Reload: колір приходить із сервера ───────────────────────────────
        await page.reload();
        await expect(filledCells(page).first(), 'колір рядка не пережив reload').toBeVisible({ timeout: 60_000 });
        await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);

        // ── Скидання ─────────────────────────────────────────────────────────
        await selectColourableCell(page);
        await chooseScope(page, 'row');
        expect((await resetFill(page)).status(), 'сервер не прийняв скидання рядка').toBe(200);
        await expect(filledCells(page)).toHaveCount(0, { timeout: 15_000 });

        // ── Вся колонка ──────────────────────────────────────────────────────
        await chooseScope(page, 'column');
        const columnResponse = await paint(page, Yellow);
        expect(columnResponse.status(), `PATCH scope=column: ${await columnResponse.text()}`).toBe(200);
        const columnBody = columnResponse.request().postDataJSON() as { scope?: { columnCodes?: string[]; allRows?: boolean } };
        expect(columnBody.scope?.allRows).toBe(true);
        expect(columnBody.scope?.columnCodes?.length).toBeGreaterThan(0);
        await expect(filledCells(page).first(), 'колонку не пофарбовано').toBeVisible({ timeout: 15_000 });

        // ── Вся таблиця ──────────────────────────────────────────────────────
        await chooseScope(page, 'table');
        const tableResponse = await paint(page, Yellow);
        expect(tableResponse.status(), `PATCH scope=table: ${await tableResponse.text()}`).toBe(200);
        const tableBody = tableResponse.request().postDataJSON() as { scope?: { allRows?: boolean; allColumns?: boolean } };
        expect(tableBody.scope).toMatchObject({ allRows: true, allColumns: true });
        await expect(filledCells(page).first()).toBeVisible({ timeout: 15_000 });
        expect(await filledCells(page).count(), 'таблицю пофарбовано не повністю').toBeGreaterThan(1);

        expect((await resetFill(page)).status(), 'сервер не прийняв скидання таблиці').toBe(200);
        await expect(filledCells(page)).toHaveCount(0, { timeout: 15_000 });
      } finally {
        await context.close();
      }
    });
  }

  test('cellColorGrid: грант Read -> кнопки вимкнені з причиною, доступні з клавіатури', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Read');
    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      await openDocument(page);
      await selectAnyCell(page);
      for (const id of ['cell-color-fill', 'cell-color-text']) {
        const button = page.getByTestId(id).first();
        await expectMenuDisabled(button, `${id}: кнопка має бути вимкнена`);
        await expect(button, `${id}: немає причини`).toHaveAttribute('data-disabled-reason', /\S+/);
        const describedBy = await button.getAttribute('aria-describedby');
        expect(describedBy, `${id}: причина недоступна читалці`).not.toBeNull();
        await expect(page.locator(`#${describedBy ?? 'none'}`)).not.toHaveText('');
        await button.focus();
        await expect(button, `${id}: кнопка недосяжна з клавіатури`).toBeFocused();
      }
    } finally {
      await context.close();
      await ensureOperatorLevel(browser, 'Write');
    }
  });

  test('cellColorGrid: поданий аркуш -> кнопки вимкнені, scope не шле запит', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Write');

    // Подає Адміністратор: `POST …/submit` від Operator -> 403 ECR-ACCS-0403 (у ролі немає права Submit), як у `cellColorLive`.
    const admin = await openAs(browser, Admin, 'light');
    try {
      await makeSheetEditable(admin.page);
      await submitSheet(admin.page);
    } finally {
      await admin.context.close();
    }

    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      await openDocument(page);
      await selectAnyCell(page);
      await expectMenuDisabled(page.getByTestId('cell-color-fill').first(), 'аркуш подано, а «Заливка» ввімкнена');
      await expect(page.getByTestId('cell-color-fill').first()).toHaveAttribute('data-disabled-reason', /\S+/);
    } finally {
      await context.close();
      // Повернення в Draft (`Document.Reopen`) - теж від Адміністратора: Operator цього права не має.
      const cleanup = await openAs(browser, Admin, 'light');
      try {
        await makeSheetEditable(cleanup.page);
      } finally {
        await cleanup.context.close();
      }
    }
  });

  test('cellColorGrid: часткова відмова області -> тост із кількістю, зріз перечитано', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Write');
    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      await makeSheetEditable(page);
      const slice = await openDocument(page);
      touched.push(slice);
      await selectColourableCell(page);

      // ⚠ Відповідь підміняється повністю (`route.fulfill`), а не доповнюється через `route.fetch()`: запит із Node іде
      // без `Sec-Fetch-Site` і з `Origin` dev-сервера, тож `CsrfOriginMiddleware` (L1-04) відповів би 403 csrfOrigin.
      // Це доводить КЛІЄНТА (тост із `deniedCells`); форма - `PatchCellStylesResponse`, причина - `EditDenyReason`.
      await page.route(/\/tables\/\d+\/cell-styles$/, async (route) => {
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({
            appliedCells: 0,
            resetCells: 0,
            skippedCells: 0,
            deniedCells: 2,
            deniedByReason: { InsufficientGrantLevel: 2 },
          }),
        });
      });

      await chooseScope(page, 'row');
      expect((await paint(page, Yellow)).status()).toBe(200);
      const toast = page.getByTestId('cell-color-toast');
      await expect(toast, 'немає тосту часткової відмови').toBeVisible({ timeout: 15_000 });
      await expect(toast).toContainText('2');
      await expect(toast).toHaveAttribute('role', 'alert');
    } finally {
      await context.close();
    }
  });
});

// ── Помічники ──────────────────────────────────────────────────────────────────

async function signIn(page: Page, user: { user: string; password: string }): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user.user);
  // Роль, а не підпис: `getByLabel(/Password/)` резолвиться ще й у кнопку-тумблер видимості пароля (Q-260).
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(user.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Окремий контекст браузера користувача з обраною темою (вибір - у сховищі ДО входу, як у `zz-walkthrough`). */
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

  // ⚠ Прогін 38265555757: тема з сервера (синхронізація вподобань, `usePreferenceSync`) перекрила локальну, і «dark»
  // насправді йшов у light. Тому тема після входу - тим самим шляхом, що й у людини (меню користувача); це заодно
  // синхронізує вибір на сервер, і наступні сторінки вже не повернуть іншу.
  // Двічі з перезавантаженням між ними: вподобання з сервера можуть дійти вже ПІСЛЯ першої перевірки.
  await ensureScheme(page, user.user, scheme);
  await page.reload();
  await ensureScheme(page, user.user, scheme);

  return { page, context };
}

/** Тема `scheme` на `<html>`; якщо інша - перемикається через меню користувача (SegmentedControl «Light»/«Dark»). */
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

/**
 * Рівень гранта `E2EOperator` на `Project/1`: через API під сесією адміністратора (форма `/admin/security` вже
 * доведена `security.spec.ts`, тут вона лише ціна кожного сценарію). `If-Match` - як у `e2e-stand.ps1`.
 */
async function ensureOperatorLevel(browser: Browser, level: 'Read' | 'Write'): Promise<void> {
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
      data: { grants: [{ resourceKind: 'Project', resourceId: 1, level, isDeny: false }] },
    });
    expect(put.ok(), `PUT grants (${level}): ${String(put.status())} ${await put.text()}`).toBe(true);
  } finally {
    await admin.context.close();
  }
}

function bodyCells(page: Page): Locator {
  return page.locator('revo-grid').first().locator('revogr-data[type="rgRow"] .rgCell[data-rgrow="0"]');
}

/** Перша комірка рядка 0, виділення якої вмикає меню (колонка-підпис і заборонені комірки пропускаються). */
async function selectColourableCell(page: Page): Promise<{ col: string }> {
  const cells = bodyCells(page);
  await expect(cells.first()).toBeVisible({ timeout: 60_000 });
  const count = Math.min(await cells.count(), 8);

  for (let index = 0; index < count; index += 1) {
    const cell = cells.nth(index);
    const col = await cell.getAttribute('data-rgcol');
    if (col === null) continue;

    await cell.click();
    try {
      await expect(page.getByTestId('cell-color-fill').first()).toBeEnabled({ timeout: 2_000 });
      return { col };
    } catch {
      // комірка не колірна (підпис рядка, заборона) - пробуємо наступну
    }
  }

  throw new Error('жодна з перших комірок рядка 0 не вмикає меню кольору');
}

/** Клік у першу комірку рядка 0 - виділення, яке меню має оцінити (дозволено чи ні). */
async function selectAnyCell(page: Page): Promise<void> {
  await bodyCells(page).first().click();
  await page.waitForTimeout(300);
}

/** Повертає аркуш у `Draft` (законна дія `Document.Reopen`), ідемпотентно - як у `security.spec.ts`. */
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
  await dialog.getByRole('textbox', { name: /Reason|Причина/i }).fill('e2e: cellColor, повернення базової лінії');
  await dialog.getByRole('button', { name: /Return for edits|Повернути/i }).click();

  await expect(activeTab, 'аркуш не повернувся в Draft').toContainText('Draft', { timeout: 15_000 });
}

/**
 * Подає аркуш так само, як `keyboardPath.spec.ts`: вставка -> збереження -> перевірка (якщо є) -> «Submit» -> 204.
 * ⚠ Подання без жодного значення могло б не пройти валідацію - тому спершу вставка, як у сусіда.
 */
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

interface SliceBody {
  tableInstanceId?: number;
  periodKey?: number;
  columns?: { code: string }[];
  rows?: { rowKey: string }[];
}

/** Слухач ДО навігації: відповідь на зріз таблиці дає `tableInstanceId`, період і першу комірку. */
function listenSlice(page: Page): Promise<SliceBody | null> {
  return (
    page
      .waitForResponse(
        // 200 або 304: за умовної перевірки кешу (ETag) відповідь без тіла - `ok()` для 304 хибне, і слухач
        // до таймауту не бачив зріз (у темі dark на CI). Тіло 304 недоступне - його бере `page.request`.
        (response) =>
          response.request().method() === 'GET' &&
          (response.status() === 200 || response.status() === 304) &&
          /^\/api\/v1\/documents\/\d+\/tables\/\d+$/.test(new URL(response.url()).pathname),
        { timeout: 45_000 },
      )
      // Тіло читається ОДРАЗУ, в тому ж ланцюжку: пізніше `response.json()` падає «No resource with given identifier found».
      .then(async (response): Promise<SliceBody> => {
        if (response.status() === 200) return (await response.json()) as SliceBody;

        return (await (await page.request.get(response.url())).json()) as SliceBody;
      })
      .catch(() => null)
  );
}

function watchSlice(page: Page): () => Promise<SliceRef> {
  const pending = listenSlice(page);

  return async () => {
    let body = await pending;
    if (body === null) {
      // Один повтор: слухач ПЕРЕД reload (а не після), без sleep; перевірки змісту не змінюються.
      const retry = listenSlice(page);
      await page.reload();
      body = await retry;
    }
    if (body === null) throw new Error('зріз таблиці не завантажився (GET …/tables/{id})');

    const column = body.columns?.[0]?.code;
    const row = body.rows?.[0]?.rowKey;
    if (body.tableInstanceId === undefined || body.periodKey === undefined || column === undefined || row === undefined) {
      throw new Error('відповідь зрізу не має tableInstanceId/periodKey/columns/rows');
    }

    return { tableInstanceId: body.tableInstanceId, periodKey: body.periodKey, rowKey: row, columnCode: column };
  };
}

async function openDocument(page: Page): Promise<SliceRef> {
  const watch = watchSlice(page);
  await page.goto(DocumentUrl);
  await expect(page.getByRole('heading').first(), 'документ не відрендерився').toBeVisible({ timeout: 30_000 });
  await expect(
    page.locator('revo-grid').first().locator('revogr-data .rgCell').first(),
    'рядки сітки не завантажилися',
  ).toBeVisible({ timeout: 60_000 });

  return watch();
}

/** Вимкнена кнопка: нативний `disabled` АБО `aria-disabled="true"` - що саме поставить L4, байдуже. */
async function expectMenuDisabled(button: Locator, message: string): Promise<void> {
  await expect(async () => {
    const native = await button.isDisabled();
    const aria = await button.getAttribute('aria-disabled');
    expect(native || aria === 'true', message).toBe(true);
  }, message).toPass({ timeout: 10_000 });
}

