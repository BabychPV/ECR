import { expect, test, type Browser, type Locator, type Page, type Response } from '@playwright/test';

/**
 * CELL-STYLE L4: колір комірки «як в Excel» проти РЕАЛЬНОГО сервера стенда (`tools/e2e-stand.ps1`, workflow
 * `e2e-stand`, `-Grep cellColor`): тулбар сітки «Заливка»/«Колір тексту» -> `PATCH …/tables/{id}/cell-styles` ->
 * зріз із сервера.
 *
 * Чим це відрізняється від `cellColor.spec.ts` (L4, сторінка-стенд `DocumentGridGateStandPage`, сервер у браузері):
 * там немає ні справжнього PATCH, ні грантів, ні стану аркуша, ні закритого періоду. Тут — лише вони.
 *
 * Сценарії (кожен - окремий `test`, назва містить `cellColor` для `-Grep`):
 *  1-2. оператор із `Write`: комірка -> заливка -> PATCH 200 -> `data-user-style` і `background-color`; reload (колір
 *       приходить із сервера, не з клієнтського кешу); «Без заливки» -> колір зник і після reload; теми Light і Dark;
 *  3.   відмова сервера через зміну гранта ПІСЛЯ завантаження сторінки (меню ще ввімкнене) -> PATCH 403 -> тост,
 *       колір не лишився;
 *  4.   `Read`-грант: кнопки вимкнені з поясненням; сервер на PATCH -> 403;
 *  5.   поданий аркуш: кнопки вимкнені з причиною «аркуш закрито»; сервер на PATCH -> 403;
 *  6.   закритий період: лише якщо в календарі стенда є `Closed`-період із зрізом (інакше - «не доведено»).
 *
 * ⚠ Тестові дані синтетичні (стенд заводить їх сам). Порядок і стан: файл іде за алфавітом ПЕРЕД
 * `editAssignments`/`keyboardPath`/`security`, тож `afterAll` повертає стенд у його базову лінію: грант оператора
 * `Project/1/Write`, аркуш `Draft`, жодного користувацького кольору.
 *
 * ⚠ Написано без локального прогону (на машині автора немає стенда): перший живий прогін - CI. Усе, що не доведено
 * кодом (`role="alert"` у тоста, `.rgCell` несе `background-color`), названо в звіті, а не вгадано мовчки.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const Operator = { user: 'e2e-operator', password: 'E2E-Oper8tor-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const DocumentUrl = `/documents/${DocumentId}?periodKey=${PeriodKey}`;

/** Зразок із стандартних кольорів палітри (`shared/theme/cellPalette.ts`): жовтий, лише в одному ряду. */
const Yellow = '#ffff00';
const YellowRgb = 'rgb(255, 255, 0)';

interface SliceRef {
  tableInstanceId: number;
  periodKey: number;
  rowKey: string;
  columnCode: string;
}

/** Комірки, яким тест міг лишити колір, - `afterAll` знімає їх через API. */
const touched: SliceRef[] = [];

test.describe('cellColor: колір комірки на реальному стенді (CELL-STYLE L4)', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );
  // ⚠ НЕ `serial`: кожен сценарій сам виставляє грант/стан (`ensureOperatorLevel`, `makeSheetEditable`), тож падіння
  // першого (напр. UI-шлях запису) не має ховати решту. Порядок - за файлом (`workers: 1`).
  test.describe.configure({ timeout: 240_000 });

  for (const scheme of ['light', 'dark'] as const) {
    test(`cellColor: заливка -> збереження на сервері -> скидання, тема ${scheme}`, async ({ browser }) => {
      await ensureOperatorLevel(browser, 'Write');
      const { page, context } = await openAs(browser, Operator, scheme);

      try {
        const slice = await openDocument(page);
        const target = await selectColourableCell(page);
        touched.push(slice);

        // ── 1. Заливка: відповідь сервера, потім видимий результат ──────────────
        await page.getByTestId('cell-color-fill').first().click();
        const applied = waitForStylePatch(page);
        await page.getByRole('button', { name: Yellow, exact: true }).first().click();
        const appliedResponse = await applied;
        expect(
          appliedResponse.status(),
          `сервер не прийняв заливку (PATCH cell-styles): ${await appliedResponse.text()} | запит: ${String(
            appliedResponse.request().postData(),
          )}`,
        ).toBe(200);

        await expect(cellAt(page, target)).toHaveAttribute('data-user-style', 'true');
        await expect(cellAt(page, target)).toHaveCSS('background-color', YellowRgb);

        // ── 2. Колір тексту теж доходить до сервера ──────────────────────
        await page.getByTestId('cell-color-text').first().click();
        const textApplied = waitForStylePatch(page);
        await page.getByRole('button', { name: '#c00000', exact: true }).first().click();
        expect((await textApplied).status(), 'сервер не прийняв колір тексту').toBe(200);

        // ── 3. Reload: джерело істини - сервер, а не клієнтський кеш ───────────
        await page.reload();
        await expect(cellAt(page, target), 'після reload комірки немає').toBeVisible({ timeout: 60_000 });
        await expect(cellAt(page, target), 'колір не пережив reload - живе лише в клієнті').toHaveAttribute(
          'data-user-style',
          'true',
        );
        await expect(cellAt(page, target)).toHaveCSS('background-color', YellowRgb);
        await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);

        // ── 4. Скидання заливки і кольору тексту ───────────────────────────────
        await selectCell(page, target);
        await page.getByTestId('cell-color-fill').first().click();
        const resetFill = waitForStylePatch(page);
        await page.getByTestId('cell-color-fill-reset').click();
        expect((await resetFill).status(), 'сервер не прийняв «Без заливки»').toBe(200);

        await page.getByTestId('cell-color-text').first().click();
        const resetText = waitForStylePatch(page);
        await page.getByTestId('cell-color-text-reset').click();
        expect((await resetText).status(), 'сервер не прийняв «Автоматичний»').toBe(200);

        await expect(cellAt(page, target)).not.toHaveAttribute('data-user-style', 'true');
        await expect(cellAt(page, target)).not.toHaveCSS('background-color', YellowRgb);

        await page.reload();
        await expect(cellAt(page, target)).toBeVisible({ timeout: 60_000 });
        await expect(cellAt(page, target), 'скинутий колір повернувся з сервера').not.toHaveAttribute(
          'data-user-style',
          'true',
        );
      } finally {
        await context.close();
      }
    });
  }

  test('cellColor: відмова сервера (грант знижено після завантаження) - тост, колір не застосовано', async ({
    browser,
  }) => {
    await ensureOperatorLevel(browser, 'Write');
    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      const slice = await openDocument(page);
      const target = await selectColourableCell(page);
      touched.push(slice);

      // Меню ВВІМКНЕНЕ (клієнт ще вважає комірку редагованою), а сервер уже ні: це і є «відмова на сервері».
      await ensureOperatorLevel(browser, 'Read');

      await page.getByTestId('cell-color-fill').first().click();
      const denied = waitForStylePatch(page);
      await page.getByRole('button', { name: Yellow, exact: true }).first().click();
      const response = await denied;
      expect(response.status(), 'очікувалась відмова 403 ECR-ACCS-0403').toBe(403);

      // Штатний тост Mantine з текстом відмови (`showApiError`); мова стенда - en.
      const toast = page.getByRole('alert').filter({ hasText: /may not edit|не можна змінювати|нельзя изменять/i });
      await expect(toast.first(), 'тосту з текстом відмови немає').toBeVisible({ timeout: 15_000 });

      // Оптимістичний колір відкочено: у комірці його немає.
      await expect(cellAt(page, target)).not.toHaveAttribute('data-user-style', 'true');
      await expect(cellAt(page, target)).not.toHaveCSS('background-color', YellowRgb);
    } finally {
      await context.close();
      await ensureOperatorLevel(browser, 'Write');
    }
  });

  test('cellColor: Read-грант - кнопки вимкнені з поясненням, сервер відхиляє 403', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Read');
    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      const slice = await openDocument(page);
      await selectAnyCell(page);

      for (const id of ['cell-color-fill', 'cell-color-text']) {
        const button = page.getByTestId(id).first();
        await expect(button, `${id} має бути вимкнена без права Write`).toBeDisabled();
        // Причина - у `title` (CellColorMenu: `title={disabled && hint !== null ? hint : undefined}`).
        await expect(button, `${id}: немає пояснення причини`).toHaveAttribute('title', /\S+/);
      }

      const status = await patchStyles(page, slice, { backgroundHex: Yellow });
      expect(status.status, 'сервер мав відмовити Read-гранту').toBe(403);
      expect(status.text).toContain('ECR-ACCS-0403');
    } finally {
      await context.close();
      await ensureOperatorLevel(browser, 'Write');
    }
  });

  test('cellColor: поданий аркуш - кнопки вимкнені («аркуш закрито»), сервер відхиляє 403', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Write');

    const admin = await openAs(browser, Admin, 'light');
    try {
      await makeSheetEditable(admin.page);
      await submitSheet(admin.page);
    } finally {
      await admin.context.close();
    }

    const { page, context } = await openAs(browser, Operator, 'light');
    try {
      const slice = await openDocument(page);
      await expect(page.getByRole('tab', { selected: true })).toContainText('Submitted', { timeout: 30_000 });
      await selectAnyCell(page);

      const fill = page.getByTestId('cell-color-fill').first();
      await expect(fill).toBeDisabled();
      // `DocumentGrid`: `readOnly` -> підказка `grid.cellColor.readOnly`, а не «виділіть комірки».
      await expect(fill).toHaveAttribute('title', /locked for editing|закрит|закрыт/i);

      const status = await patchStyles(page, slice, { backgroundHex: Yellow });
      expect(status.status, 'сервер мав відмовити на поданому аркуші').toBe(403);
      expect(status.text).toContain('ECR-ACCS-0403');
      expect(status.text).toMatch(/DocumentSubmitted/);
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

  test('cellColor: закритий період - меню вимкнене, сервер відхиляє PeriodClosed (якщо стенд має такий період)', async ({
    browser,
  }) => {
    await ensureOperatorLevel(browser, 'Write');
    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      // Стенд не вміє закрити період з e2e (стан періоду - `PeriodStateJob`, ручного «закрити» немає), тож шукаємо
      // вже закритий у календарі проєкту. Немає - чесно «не доведено», а не зелене.
      const closed = await findClosedPeriod(page);
      test.skip(closed === null, 'не доведено: у календарі стенда немає періоду зі станом Closed');
      if (closed === null) return;

      const watch = watchSlice(page);
      await page.goto(`/documents/${DocumentId}?periodKey=${String(closed)}`);
      await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
      const slice = await watch().catch(() => null);
      test.skip(slice === null, `не доведено: документ не має зрізу таблиці в закритому періоді ${String(closed)}`);
      if (slice === null) return;

      const status = await patchStyles(page, slice, { backgroundHex: Yellow });
      expect(status.status, 'закритий період мав відмовити').toBe(403);
      expect(status.text).toMatch(/ECR-ACCS-0403|PeriodClosed/);
      expect(status.text).toMatch(/PeriodClosed/);

      await selectAnyCell(page);
      await expect(page.getByTestId('cell-color-fill').first()).toBeDisabled();
    } finally {
      await context.close();
    }
  });

  /**
   * Повертає стенд у базову лінію: грант `Project/1/Write`, аркуш `Draft`, жодного кольору, що лишився від тестів.
   * ⚠ Виконується і при падінні посеред файла - наступні файли (`editAssignments`, `keyboardPath`, `security`)
   * спираються саме на цю лінію.
   */
  test.afterAll(async ({ browser }, testInfo) => {
    if (PeriodKey === '' || DocumentId === '') return;
    testInfo.setTimeout(180_000);

    await ensureOperatorLevel(browser, 'Write');

    const admin = await openAs(browser, Admin, 'light');
    try {
      for (const ref of touched) {
        // Скидання - best effort: комірки, яких вже немає, сервер відхилить, і це не привід валити хук.
        await admin.page.request
          .patch(`/api/v1/documents/${DocumentId}/tables/${String(ref.tableInstanceId)}/cell-styles`, {
            data: {
              periodKey: ref.periodKey,
              items: [{ rowKey: ref.rowKey, columnCode: ref.columnCode }],
              reset: true,
            },
          })
          .catch(() => undefined);
      }

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

  return { page, context };
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

/** Слухач ДО навігації: відповідь на зріз таблиці дає `tableInstanceId`, період і першу комірку. */
function watchSlice(page: Page): () => Promise<SliceRef> {
  const pending = page
    .waitForResponse(
      (response) =>
        response.request().method() === 'GET' &&
        response.ok() &&
        /^\/api\/v1\/documents\/\d+\/tables\/\d+$/.test(new URL(response.url()).pathname),
      { timeout: 90_000 },
    )
    .catch(() => null);

  return async () => {
    const response = await pending;
    if (response === null) throw new Error('зріз таблиці не завантажився (GET …/tables/{id})');

    const body = (await response.json()) as {
      tableInstanceId?: number;
      periodKey?: number;
      columns?: { code: string }[];
      rows?: { rowKey: string }[];
    };
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

function bodyCells(page: Page): Locator {
  return page.locator('revo-grid').first().locator('revogr-data[type="rgRow"] .rgCell[data-rgrow="0"]');
}

function cellAt(page: Page, target: { col: string }): Locator {
  return page
    .locator('revo-grid')
    .first()
    .locator(`revogr-data[type="rgRow"] .rgCell[data-rgrow="0"][data-rgcol="${target.col}"]`);
}

async function selectCell(page: Page, target: { col: string }): Promise<void> {
  await cellAt(page, target).click();
  await expect(page.getByTestId('cell-color-fill').first()).toBeEnabled({ timeout: 10_000 });
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

function waitForStylePatch(page: Page): Promise<Response> {
  return page.waitForResponse(
    (response) =>
      response.request().method() === 'PATCH' && /\/tables\/\d+\/cell-styles$/.test(new URL(response.url()).pathname),
    { timeout: 60_000 },
  );
}

/** PATCH за ПРАВИЛЬНИМ контрактом (`PatchCellStylesRequest`) під сесією сторінки - доводить сервер, не клієнт. */
async function patchStyles(
  page: Page,
  slice: SliceRef,
  change: { backgroundHex: string },
): Promise<{ status: number; text: string }> {
  const response = await page.request.patch(
    `/api/v1/documents/${DocumentId}/tables/${String(slice.tableInstanceId)}/cell-styles`,
    {
      data: {
        periodKey: slice.periodKey,
        items: [{ rowKey: slice.rowKey, columnCode: slice.columnCode, backgroundHex: change.backgroundHex }],
      },
    },
  );

  return { status: response.status(), text: await response.text() };
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
