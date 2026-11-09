import { expect, test, type Locator, type Page } from '@playwright/test';

/**
 * Перша комірка ДАНИХ сітки: прокручуваний в'юпорт (`rgCol`), не закріплена зона. Закріплена зліва (`colPinStart`) колонка
 * підпису рядка не має коду в зрізі, рядок формули на ній лишається `empty`, а чи закріплена вона - залежить від
 * налаштувань, які лишили попередні сценарії шарда (05.11: нестабільне падіння `[data-formula-bar="cell"]`).
 */
function firstDataCell(grid: Locator): Locator {
  return grid.locator('revogr-viewport-scroll.rgCol revogr-data[type="rgRow"] .rgCell').first();
}
/**
 * Інспектор документа (`UI-25`) на живому стенді: закритий за замовчуванням,
 * кнопка зауважень відкриває його, `Esc` закриває й повертає фокус, History
 * показує вкладку для вибраної комірки. Запуск «Аудитом»:
 * `tools/e2e-stand.ps1 … -Grep "інспектор"`.
 */

const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('інспектор документа', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('інспектор: закритий за замовчуванням, відкривається кнопкою, Esc повертає фокус', async ({ page }) => {
    test.slow();
    await signIn(page);
    await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
    await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });

    // L2: на відкритті інспектора немає.
    const triggers = page.locator('[data-inspector-triggers]');
    await expect(triggers).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('complementary')).toHaveCount(0);

    const issues = triggers.getByRole('button').first();
    await issues.focus();
    await issues.press('Enter');

    const aside = page.getByRole('complementary');
    await expect(aside).toBeVisible();
    await expect(page).toHaveURL(/[?&]panel=issues/);
    await expect(aside.getByRole('tab', { selected: true })).toBeFocused();

    await page.keyboard.press('Escape');
    await expect(page.getByRole('complementary')).toHaveCount(0);
    await expect(issues).toBeFocused();
    await expect(page).not.toHaveURL(/[?&]panel=/);
  });

  test('інспектор: History і Info показують вибрану комірку', async ({ page }) => {
    test.slow();
    await signIn(page);
    await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);

    const grid = page.locator('revo-grid').first();
    await expect(grid).toBeVisible({ timeout: 60_000 });
    await firstDataCell(grid).click();
    await expect(page.locator('[data-formula-bar="cell"]').first()).toBeVisible();

    // ✎ TC: between Issues and History now sits the "Discussion" button (when the chat exists), so not by index.
    await page.locator('[data-inspector-triggers]').getByRole('button', { name: /History/i }).click();
    const aside = page.getByRole('complementary');
    await expect(aside).toBeVisible();
    await expect(page).toHaveURL(/[?&]panel=history/);
    await expect(aside.locator('[data-inspector-address]')).toBeVisible();

    // Вибір комірки не підміняє вміст: History лишається журналом аркуша, «This cell» доступний і веде до історії комірки.
    await expect(aside.getByRole('radio', { name: 'Sheet log' })).toBeChecked();
    await expect(aside.getByRole('radio', { name: 'This cell' })).toBeEnabled();
    await aside.locator('label', { hasText: 'This cell' }).click();
    await expect(aside.locator('[data-inspector-scope]')).toContainText(/^Cell: /);

    await aside.getByRole('tab').nth(2).click();
    await expect(aside.locator('[data-inspector-info]')).toBeVisible();
  });
});

/**
 * ✎ UAT 08.11, Water Report (рядок «Sea Water Intake», січень 2026): червоний рядок зі знаком «!», штриховка
 * закритих місяців і «All changes saved» без слова про рядок. Людина: «штриховку прибрати залишити замок»,
 * «чому строка червона і зі знаками оклику в кутку?», «треба перевірити список issues».
 *
 * ⚠ Стан «втрачене посилання» ставить нічна перевірка посилань (`OrphanScanPlan`), а відмову `PeriodClosed` —
 * календар; API для них немає, і ламати календар спільного стенда не можна. Тому справжню відповідь зрізу першої
 * таблиці доповнено (`route.fetch` + `fulfill`, як `staleCalculations.spec.ts`): перший рядок — `isOrphaned`, усі
 * комірки двох перших рядків — `PeriodClosed`. Решта шляху (сітка, інспектор, рядок стану) — справжня.
 */
const WaterRow = 'Sea Water Intake (e2e)';
/** Row label of a flagged row in the grid (`DocumentGrid` row-label `cellTemplate`), not the save-state count. */
const RowAttentionLabel = 'revo-grid .ecr-row-label--attention[data-row-attention]';

async function openWithWaterLock(page: Page): Promise<{ key: string }> {
  let target: number | null = null;
  let firstRowKey = '';
  await page.route(new RegExp(`/api/v1/documents/${DocumentId}/tables/(\\d+)$`), async (route) => {
    if (route.request().method() !== 'GET') return route.fallback();
    const response = await route.fetch();
    const body = (await response.json()) as {
      tableInstanceId: number;
      columns: { code: string }[];
      rows: { rowKey: string; label: string | null; isOrphaned: boolean }[];
      cellPermissions: Record<string, string>;
    };
    if (target === null && body.rows.length > 0 && body.columns.length > 0) target = body.tableInstanceId;
    if (body.tableInstanceId !== target) return route.fulfill({ response, json: body });

    const [first, second] = body.rows;
    if (first !== undefined) {
      firstRowKey = first.rowKey;
      first.isOrphaned = true;
      first.label = WaterRow;
    }
    for (const row of [first, second]) {
      if (row === undefined) continue;
      for (const column of body.columns) body.cellPermissions[`${row.rowKey}:${column.code}`] = 'PeriodClosed';
    }

    return route.fulfill({ response, json: body });
  });

  await signIn(page);
  await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
  // ⚠ Not bare `[data-row-attention]`: the save-state line carries the same attribute (a count, "0") and is always
  // there, so the wait passed before the slice was routed (CI 42498099369: key "null:").
  const label = page.locator(RowAttentionLabel).first();
  await expect(label).toBeVisible({ timeout: 60_000 });
  expect(target, 'the slice of the first table was not routed').not.toBeNull();

  return { key: `${String(target)}:${firstRowKey}` };
}

test.describe('Water lock (UAT 08.11)', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  for (const scheme of ['light', 'dark'] as const) {
    test(`Water lock: закрита комірка — замок, без штриховки (${scheme})`, async ({ page }) => {
      test.slow();
      await page.emulateMedia({ colorScheme: scheme });
      await openWithWaterLock(page);

      const locked = page.locator('revo-grid [data-cell-state="readOnly"]').first();
      await expect(locked).toBeVisible({ timeout: 30_000 });
      const look = await locked.evaluate((element) => {
        const lock = getComputedStyle(element, '::after');

        return {
          background: getComputedStyle(element).backgroundImage,
          lockContent: lock.content,
          lockMask: lock.maskImage || lock.getPropertyValue('-webkit-mask-image'),
        };
      });
      expect(look.background, 'штриховка повернулась').not.toContain('repeating-linear-gradient');
      expect(look.lockContent, 'замок у куті зник').not.toBe('none');
      expect(look.lockMask).not.toBe('none');
    });
  }

  test('Water lock: червоний рядок — причина словами біля підпису', async ({ page }) => {
    test.slow();
    await openWithWaterLock(page);

    const label = page.locator(RowAttentionLabel).first();
    await expect(label.locator('.ecr-row-label-text')).toHaveText(WaterRow);
    const reason = label.locator('.ecr-row-attention-reason');
    await expect(reason).toBeVisible();
    await expect(reason).toContainText(/reopen the month to edit/i);
    await expect(reason).not.toContainText(/separate approval/i);
  });

  test('Water lock: «!» відкриває Issues на пункті рядка', async ({ page }) => {
    test.slow();
    const { key } = await openWithWaterLock(page);
    await expect(page.getByRole('complementary')).toHaveCount(0);

    await page.locator(`[data-row-attention-open="${key}"]`).click();

    const aside = page.getByRole('complementary');
    await expect(aside).toHaveAttribute('data-inspector', 'issues');
    const item = aside.locator(`[data-inspector-attention="${key}"]`);
    await expect(item).toContainText(WaterRow);
    await expect(item).toBeFocused();
    await expect(aside.locator('[data-inspector-locked]').first()).toContainText(/locked/i);
  });

  test('Water lock: шапка інспектора — період сторінки, комірка — назвою колонки', async ({ page }) => {
    test.slow();
    await openWithWaterLock(page);
    await page.locator('[data-inspector-triggers]').getByRole('button').first().click();

    const aside = page.getByRole('complementary');
    await expect(aside.locator('[data-inspector-period]')).toHaveAttribute('data-inspector-period', PeriodKey);

    await firstDataCell(page.locator('revo-grid').first()).click();
    const address = aside.locator('[data-inspector-address]');
    await expect(address).toBeVisible();
    // ✎ d5: чип адреси каже, що це КОЛОНКА (її назва), а не період.
    await expect(address).toHaveAttribute('data-inspector-column', /.+/);
  });

  // ✎ F13-p209 (d4): сервер віддає момент перевірки (`validatedAt`) — і у відповіді прогону, і в збереженому результаті;
  // порожній Issues каже дату й час. Зауваження стенда (якщо є) прибрано в маршруті, щоб показати порожній стан.
  test('Water lock: порожній Issues каже, коли була остання перевірка (дата й час)', async ({ page }) => {
    test.slow();
    await openWithWaterLock(page);

    const validated = page.waitForResponse(
      (response) =>
        response.request().method() === 'POST' &&
        new URL(response.url()).pathname === `/api/v1/documents/${DocumentId}/validate`,
      { timeout: 90_000 },
    );
    await page.getByRole('button', { name: /Validate|Перевір/i }).first().click();
    const fresh = (await (await validated).json()) as { validatedAt?: string | null };
    expect(fresh.validatedAt, 'POST …/validate без validatedAt').toMatch(/^\d{4}-\d{2}-\d{2}T/);

    const stored: { validatedAt?: string | null }[] = [];
    await page.route(new RegExp(`/api/v1/documents/${DocumentId}/validation\\?`), async (route) => {
      const response = await route.fetch();
      const json = (await response.json()) as { validatedAt?: string | null; messages: unknown[] };
      stored.push(json);
      await route.fulfill({ response, json: { ...json, messages: [] } });
    });
    await page.reload();
    await expect(page.locator(RowAttentionLabel).first()).toBeVisible({ timeout: 60_000 });
    await page.locator('[data-inspector-triggers]').getByRole('button').first().click();

    const empty = page.getByRole('complementary').locator('.ecr-insp-empty');
    await expect(empty).toContainText(/Checked at [^.]*2026[^.]*\d{1,2}:\d{2}/);
    expect(stored.at(-1)?.validatedAt, 'GET …/validation без validatedAt').toMatch(/^\d{4}-\d{2}-\d{2}T/);
  });

  test('Water lock: рядок стану — «N rows need attention» поруч зі збереженням', async ({ page }) => {
    test.slow();
    await openWithWaterLock(page);

    const state = page.getByTestId('document-save-state');
    await expect(state).toHaveAttribute('data-row-attention', /^[1-9]\d*$/);
    await expect(state).toContainText(/\d+ rows? needs? attention/);
  });
});

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first()).toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Operator.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
