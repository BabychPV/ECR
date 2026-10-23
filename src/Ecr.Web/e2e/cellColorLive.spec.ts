import { expect, test, type Browser, type Locator, type Page, type Response } from '@playwright/test';
import { chooseSubmitPeriod } from './submitPeriod';

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
  // ⛔ Без стенда файл ПАДАЄ, а не пропускається (honesty-guard: пропуск червонить Evidence; те саме робить
  // `globalSetup.ts` для всього набору). Стенд: `tools/e2e-stand.ps1`.
  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
  });
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
        // «До» знімається перед кліком: скидання порівнюється з ним, а не з жорстким `transparent`.
        const baseline = normalizeRgb(await backgroundOf(cellAt(page, target)));
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

        await expectBackground(cellAt(page, target), YellowRgb, 'заливка не застосувалась (computed)');
        await expect(cellAt(page, target)).toHaveAttribute('data-user-style', 'true');
        // Закриваючий дропдаун «Заливки» лишається в DOM на час переходу: без цього `.first()` нижче
        // міг би влучити в його зразок, а не у відкриту палітру тексту.
        await expect(page.locator('.ecr-cell-color-dropdown:visible')).toHaveCount(0);

        // ── 2. Колір тексту теж доходить до сервера ──────────────────────
        await page.getByTestId('cell-color-text').first().click();
        const textApplied = waitForStylePatch(page);
        await page
          .locator('.ecr-cell-color-dropdown:visible')
          .getByRole('button', { name: '#c00000', exact: true })
          .click();
        expect((await textApplied).status(), 'сервер не прийняв колір тексту').toBe(200);

        // ── 3. Reload: джерело істини - сервер, а не клієнтський кеш ───────────
        await page.reload();
        await expect(cellAt(page, target), 'після reload комірки немає').toBeVisible({ timeout: 60_000 });
        await expect(cellAt(page, target), 'колір не пережив reload - живе лише в клієнті').toHaveAttribute(
          'data-user-style',
          'true',
        );
        await expectBackground(cellAt(page, target), YellowRgb, 'після reload заливки немає (computed)');
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

        await expectBackground(cellAt(page, target), baseline, 'після скидання фон не повернувся до базового');
        await expect(cellAt(page, target)).not.toHaveAttribute('data-user-style', 'true');

        await page.reload();
        await expect(cellAt(page, target)).toBeVisible({ timeout: 60_000 });
        await expect(cellAt(page, target), 'скинутий колір повернувся з сервера').not.toHaveAttribute(
          'data-user-style',
          'true',
        );
        await expect
          .poll(async () => normalizeRgb(await backgroundOf(cellAt(page, target))), {
            message: 'після reload фон не базовий (скинутий колір повернувся)',
            timeout: 15_000,
          })
          .not.toBe(normalizeRgb(YellowRgb));
      } finally {
        await context.close();
      }
    });
  }

  test('cellColor: відмова сервера (аркуш подано після завантаження сторінки) - тост, колір не застосовано', async ({
    browser,
  }) => {
    await ensureOperatorLevel(browser, 'Write');
    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      const slice = await openDocument(page);
      const target = await selectColourableCell(page);
      touched.push(slice);

      // Меню ВВІМКНЕНЕ (клієнт ще вважає аркуш редагованим), а сервер уже ні: це і є «відмова на сервері».
      // ⚠ Не зниженням гранта: перший прогін (38259512984) показав, що зміна гранта скидає сесію оператора - PATCH
      // відповідає 401, а не 403. Подання аркуша сесію не чіпає.
      // Палітра відкривається ДО подання: зразок клікається в одному кроці з очікуванням PATCH (`Promise.all`).
      // ⚠ Обмеження: якщо клієнт рефетчне стан аркуша ще до кліку, кнопка стане disabled, палітра закриється, і
      // сценарій впаде на `swatch.click` - обійти це без `page.route` (потрібен справжній сервер) не вдасться.
      await page.getByTestId('cell-color-fill').first().click();
      const swatch = page.getByRole('button', { name: Yellow, exact: true }).first();
      await expect(swatch).toBeVisible({ timeout: 10_000 });

      const admin = await openAs(browser, Admin, 'light');
      try {
        await makeSheetEditable(admin.page);
        await submitSheet(admin.page);
      } finally {
        await admin.context.close();
      }

      const [response] = await Promise.all([
        waitForStylePatch(page),
        swatch.click({ timeout: 15_000 }),
      ]);
      expect(response.status(), `очікувалась відмова 403: ${await response.text()}`).toBe(403);

      // Штатний тост Mantine з текстом відмови (`showApiError`); `data-testid` - якщо додасть L4, інакше role=alert.
      await expect(
        errorToast(page).first(),
        'тосту з текстом відмови немає',
      ).toBeVisible({ timeout: 15_000 });

      // Оптимістичний колір відкочено: у комірці його немає.
      await expect(cellAt(page, target)).not.toHaveAttribute('data-user-style', 'true');
      await expect
        .poll(async () => normalizeRgb(await backgroundOf(cellAt(page, target))), { timeout: 15_000 })
        .not.toBe(normalizeRgb(YellowRgb));
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

  test('cellColor: Read-грант - кнопки вимкнені з поясненням, сервер відхиляє 403', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Read');
    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      const slice = await openDocument(page);
      await selectAnyCell(page);

      for (const id of ['cell-color-fill', 'cell-color-text']) {
        const button = page.getByTestId(id).first();
        await expectMenuDisabled(button, `${id} має бути вимкнена без права Write`);
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
      await expectMenuDisabled(fill, 'кнопка «Заливка» має бути вимкнена на поданому аркуші');
      // `DocumentGrid`: `readOnly` -> підказка `grid.cellColor.readOnly`, а не «виділіть комірки».
      // `disabledHint` = deny-hint сервера («The document is submitted: it must be returned for rework first.»).
      await expect(fill).toHaveAttribute(
        'title',
        /locked for editing|submitted|returned for rework|закрит|закрыт|подан|возвращ/i,
      );

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
      // вже закритий у календарі проєкту. Немає - червоне з «не доведено» (honesty-guard).
      const closed = await findClosedPeriod(page);
      if (closed === null) throw new Error('не доведено: у календарі стенда немає періоду зі станом Closed');

      const watch = watchSlice(page);
      await page.goto(`/documents/${DocumentId}?periodKey=${String(closed)}`);
      await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
      const slice = await watch().catch(() => null);
      if (slice === null) {
        throw new Error(`не доведено: документ не має зрізу таблиці в закритому періоді ${String(closed)}`);
      }

      const status = await patchStyles(page, slice, { backgroundHex: Yellow });
      expect(status.status, 'закритий період мав відмовити').toBe(403);
      expect(status.text).toMatch(/ECR-ACCS-0403|PeriodClosed/);
      expect(status.text).toMatch(/PeriodClosed/);

      await selectAnyCell(page);
      await expectMenuDisabled(page.getByTestId('cell-color-fill').first(), 'закритий період: «Заливка» має бути вимкнена');
    } finally {
      await context.close();
    }
  });

  // ── Контракт сервера: від клієнта L4 не залежить ───────────────────────────────

  test('cellColor: контракт сервера - тіло {periodKey, items} дає 200, скидання {reset} дає 200', async ({
    browser,
  }) => {
    await ensureOperatorLevel(browser, 'Write');
    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      const slice = await openDocument(page);
      touched.push(slice);
      const item = { rowKey: slice.rowKey, columnCode: slice.columnCode };

      const set = await patchRaw(page, slice, {
        periodKey: slice.periodKey,
        items: [{ ...item, backgroundHex: Yellow }],
      });
      expect(set.status, `PATCH {periodKey, items}: ${set.text}`).toBe(200);
      expect(set.text).toMatch(/"appliedCells"\s*:\s*1/);

      const reset = await patchRaw(page, slice, { periodKey: slice.periodKey, items: [item], reset: true });
      expect(reset.status, `PATCH {reset}: ${reset.text}`).toBe(200);
      expect(reset.text).toMatch(/"resetCells"\s*:\s*1/);
    } finally {
      await context.close();
    }
  });

  // Форма клієнта L4 -1/-2 (`{cells: [...]}`) серверу чужа. Після виправлення клієнта цей тест лишається
  // негативним контрактом сервера: чужа форма не повинна мовчки «прийматися» як порожній запит.
  test('cellColor: контракт сервера - форма {cells} відхиляється 422', async ({ browser }) => {
    await ensureOperatorLevel(browser, 'Write');
    const { page, context } = await openAs(browser, Operator, 'light');

    try {
      const slice = await openDocument(page);
      const response = await patchRaw(page, slice, {
        cells: [{ rowKey: slice.rowKey, columnCode: slice.columnCode, backgroundHex: Yellow }],
      });
      expect(response.status, `PATCH {cells}: ${response.text}`).toBe(422);
      // ⚠ Код залежить від того, ЯКИЙ рубіж спрацював першим. До CELL-STYLE scope-2 `items` був обов'язковим, і
      // чужа форма падала на валідації моделі (`ECR-REQ-0422`). Тепер `items` необов'язкове, а після фіксу
      // emptyChange (546775656) `EnsureValid` запиту (без items і без scope) спрацьовує РАНІШЕ за перевірку періоду
      // в handler: `ECR-CELL-4224` / `err.ECR-CELL-4224.emptyChange`. Лишаємо й старі коди як допустимі:
      // усі - 422 ДО будь-якого запису, і саме це контракт.
      expect(response.text).toMatch(/ECR-(REQ|PRD)-0422|ECR-CELL-4224/);

      // З ПРАВИЛЬНИМ periodKey чужа форма не має проходити як «порожній успіх» (регрес scope-2: 200 appliedCells 0):
      // запит без items і без scope - 422 `ECR-CELL-4224` / `err.ECR-CELL-4224.emptyChange`.
      const withPeriod = await patchRaw(page, slice, {
        periodKey: slice.periodKey,
        cells: [{ rowKey: slice.rowKey, columnCode: slice.columnCode, backgroundHex: Yellow }],
      });
      expect(withPeriod.status, `PATCH {periodKey, cells}: ${withPeriod.text}`).toBe(422);
      expect(withPeriod.text).toContain('ECR-CELL-4224');
      expect(withPeriod.text).toContain('err.ECR-CELL-4224.emptyChange');
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
    // Тіло читається ОДРАЗУ: після довгого очікування `response.json()` падає «No resource with given identifier found».
    .then(
      async (response) =>
        (await response.json()) as {
          tableInstanceId?: number;
          periodKey?: number;
          columns?: { code: string }[];
          rows?: { rowKey: string }[];
        },
    )
    .catch(() => null);

  return async () => {
    const body = await pending;
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

/** Сире тіло PATCH `…/cell-styles` під сесією сторінки. */
async function patchRaw(page: Page, slice: SliceRef, data: unknown): Promise<{ status: number; text: string }> {
  const response = await page.request.patch(
    `/api/v1/documents/${DocumentId}/tables/${String(slice.tableInstanceId)}/cell-styles`,
    { data },
  );

  return { status: response.status(), text: await response.text() };
}

/** Computed `background-color` комірки (сирий рядок браузера). */
async function backgroundOf(cell: Locator): Promise<string> {
  return cell.evaluate((node) => getComputedStyle(node).backgroundColor);
}

/** `rgb(255, 255, 0)` / `rgba(255,255,0,1)` -> `255,255,0`; повністю прозорий -> `transparent`. */
function normalizeRgb(value: string): string {
  const parts = (value.match(/[\d.]+/g) ?? []).map(Number);
  if (parts.length < 3) return value.trim().toLowerCase();
  if (parts.length >= 4 && parts[3] === 0) return 'transparent';

  return parts.slice(0, 3).join(',');
}

/** Чекає (poll, без sleep), поки computed-фон комірки стане `expected`. */
async function expectBackground(cell: Locator, expected: string, message: string): Promise<void> {
  await expect
    .poll(async () => normalizeRgb(await backgroundOf(cell)), { message, timeout: 15_000 })
    .toBe(normalizeRgb(expected));
}

/** Вимкнена кнопка: нативний `disabled` АБО `aria-disabled="true"` - що саме поставить L4, байдуже. */
async function expectMenuDisabled(button: Locator, message: string): Promise<void> {
  await expect(async () => {
    const native = await button.isDisabled();
    const aria = await button.getAttribute('aria-disabled');
    expect(native || aria === 'true', message).toBe(true);
  }, message).toPass({ timeout: 10_000 });
}

/** Тост відмови: `data-testid="cell-color-toast"` (якщо з'явиться) або `role=alert`, з текстом коду/причини. */
function errorToast(page: Page): Locator {
  return page
    .getByTestId('cell-color-toast')
    .or(page.getByRole('alert'))
    .filter({
      hasText:
        /ECR-CELL-4224|ECR-CELL-0409|ECR-ACCS-0403|may not edit|не можна змінювати|нельзя изменять|PeriodClosed|DocumentSubmitted|period is closed|document is submitted/i,
    });
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
