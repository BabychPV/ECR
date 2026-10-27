import { expect, test, type APIResponse, type Locator, type Page } from '@playwright/test';

/**
 * ui-sheet-focus на живому стенді: згортання панелі таблиць (A), таблиця на весь екран (B),
 * стрілки/список аркушів (C), згортання «Calculation results» (D).
 *
 * Кожен сценарій повторюється N/5 усередині спека (гонки фокуса й розкладки).
 * Знімки (необов'язково): `ECR_SHEETFOCUS_SHOTS_DIR` (тег — `ECR_SHEETFOCUS_SHOT_TAG`, before/after).
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const ShotsDir = process.env['ECR_SHEETFOCUS_SHOTS_DIR'] ?? '';
const ShotTag = process.env['ECR_SHEETFOCUS_SHOT_TAG'] ?? 'after';
const Repeats = 5;
const KeyRepeats = 10;
const SheetCount = 6;

test.describe('Фокус на таблиці аркуша (ui-sheet-focus)', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('панель таблиць: «Сховати» → смужка → розгорнути, вибір переживає перезавантаження (N/5)', async ({ page }) => {
    test.slow();
    await signIn(page);
    await open(page);

    for (let i = 0; i < Repeats; i += 1) {
      const nav = page.getByTestId('table-navigator');
      if (!(await nav.isVisible())) await page.getByTestId('table-navigator-rail-btn').click();
      await expect(nav).toBeVisible();

      await page.getByTestId('table-navigator-hide').click();
      await expect(nav).toBeHidden();
      const rail = page.getByTestId('table-navigator-rail-btn');
      await expect(rail).toBeFocused();
      const box = await page.getByTestId('table-navigator-rail').boundingBox();
      expect(box?.width ?? 999, 'смужка вузька').toBeLessThanOrEqual(40);

      if (i === 0) await shot(page, 'rail');

      await rail.click();
      await expect(nav).toBeVisible();
      await expect(page.getByTestId('table-navigator-hide')).toBeFocused();
    }

    await page.getByTestId('table-navigator-hide').click();
    await page.reload();
    await expect(page.getByTestId('table-navigator-rail')).toBeVisible({ timeout: 60_000 });
    await page.getByTestId('table-navigator-rail-btn').click();
  });

  test('на весь екран: правка комірки, збереження, вихід, фокус на кнопці (N/5)', async ({ page }) => {
    test.slow();
    await signIn(page);
    await open(page);

    const toggle = page.getByTestId('sheet-full-toggle');
    await shot(page, 'normal');

    for (let i = 0; i < Repeats; i += 1) {
      // Після PATCH документ може перечитатися й перемонтувати сторінку: клік повторюємо, доки адреса не набуде full=1.
      await expect(async () => {
        if (!/[?&]full=1/.test(page.url())) await toggle.click();
        await expect(page, `прогін ${String(i + 1)}/${String(Repeats)}`).toHaveURL(/[?&]full=1/, { timeout: 2_000 });
      }).toPass({ timeout: 20_000 });
      await expect(page.getByRole('region')).toBeVisible();
      await expect(page.getByTestId('table-navigator')).toBeHidden();
      if (i === 0) await shot(page, 'full');

      // Сітка займає майже все вікно.
      const grid = page.locator('revo-grid:visible');
      const height = (await grid.boundingBox())?.height ?? 0;
      const viewport = page.viewportSize()?.height ?? 0;
      expect(height, 'сітка на всю висоту вікна').toBeGreaterThan(viewport * 0.6);

      // Правка комірки всередині.
      const cell = grid.locator('revogr-data .rgCell').first();
      await editCell(page, cell);

      // Esc у редакторі скасовує правку, але НЕ виходить з режиму.
      // (Редактор міг лишитися відкритим після Enter, або його відкриває F2.)
      if ((await page.locator('revogr-edit').count()) === 0) {
        await cell.click();
        await page.keyboard.press('Enter');
      }
      await expect(page.locator('revogr-edit')).toHaveCount(1);
      await page.keyboard.press('Escape');
      await expect(page.locator('revogr-edit')).toHaveCount(0);
      await expect(page).toHaveURL(/[?&]full=1/);

      // Esc поза редактором виходить; фокус — на кнопці.
      await page.locator('body').press('Escape');
      await expect(page).not.toHaveURL(/[?&]full=1/);
      await expect(toggle).toBeFocused();
    }

    // Клавіша та посилання.
    // Ctrl+Shift+Enter ×10 (вхід і вихід), щоразу ПІСЛЯ правки й збереження: колись флейкало саме після PATCH.
    const cell = page.locator('revo-grid:visible revogr-data .rgCell').first();
    for (let i = 0; i < KeyRepeats; i += 1) {
      await editCell(page, cell);
      await expect(page.locator('revogr-edit')).toHaveCount(0);

      await page.keyboard.press('Control+Shift+Enter');
      await expect(page, `Ctrl+Shift+Enter (вхід) ${String(i + 1)}/${String(KeyRepeats)}`).toHaveURL(/[?&]full=1/);
      await expect(page.getByRole('region')).toBeVisible();
      // Тулбар сітки (Undo/Redo, статус) видимий у повному екрані.
      await expect(page.getByRole('button', { name: /^Undo$/ })).toBeVisible();
      await expect(page.getByRole('button', { name: /^Redo$/ })).toBeVisible();

      await page.keyboard.press('Control+Shift+Enter');
      await expect(page, `Ctrl+Shift+Enter (вихід) ${String(i + 1)}/${String(KeyRepeats)}`).not.toHaveURL(/[?&]full=1/);
      await expect(toggle).toBeFocused();
    }

    await toggle.click();
    await page.reload();
    await expect(page.getByRole('region')).toBeVisible({ timeout: 60_000 });
    await toggle.click();
    await expect(page).not.toHaveURL(/[?&]full=1/);
  });

  test('аркуші: «›» до останнього, активна вкладка видима, «…» веде на вибраний (N/5)', async ({ page }) => {
    test.slow();
    test.setTimeout(300_000);
    await page.setViewportSize({ width: 900, height: 800 });
    await signIn(page);

    // Документ із шістьма аркушами з довгими назвами — вкладки не вміщуються у смугу.
    const { documentId, periodKey } = await createMultiSheetDocument(page);
    await page.goto(`/documents/${String(documentId)}?periodKey=${String(periodKey)}`);
    const tabs = page.getByRole('tablist').getByRole('tab');
    await expect(tabs).toHaveCount(SheetCount, { timeout: 60_000 });
    await shot(page, 'sheets-start');

    const strip = page.getByRole('tablist');
    const overflows = await strip.evaluate((element) => element.scrollWidth > element.clientWidth);
    expect(overflows, 'вкладки мали б не вміщуватись у смугу').toBe(true);

    const prev = page.getByTestId('sheet-prev');
    const next = page.getByTestId('sheet-next');

    for (let i = 0; i < Repeats; i += 1) {
      // «‹» на першому аркуші вимкнена; «›» веде до останнього.
      await page.getByTestId('sheet-all').click();
      await page.getByRole('menuitem').first().click();
      await expect(prev, `прогін ${String(i + 1)}/${String(Repeats)}`).toBeDisabled();
      await expect(next).toBeEnabled();

      for (let step = 0; step < SheetCount - 1; step += 1) {
        await next.click();
        // Чекаємо, поки аркуш справді змінився (клік рахує сусіда від поточного рендера), і що вкладка видима.
        await expect(page.getByRole('tab', { selected: true })).toContainText(`Sheet ${String(step + 2)} `);
        await expect(page.getByRole('tab', { selected: true })).toBeInViewport({ ratio: 0.9 });
      }
      await expect(next).toBeDisabled();
      await expect(prev).toBeEnabled();
      await expect(page.getByRole('tab', { selected: true })).toContainText(`Sheet ${String(SheetCount)}`);
      if (i === 0) await shot(page, 'sheets-end');

      // «…» → вибір аркуша: другий.
      await page.getByTestId('sheet-all').click();
      await page.getByRole('menuitem', { name: /Sheet 2 / }).click();
      await expect(page.getByRole('tab', { selected: true })).toContainText('Sheet 2 ');
      await expect(prev).toBeEnabled();
      await expect(next).toBeEnabled();
    }
  });

  test('«Calculation results»: розкривачка, вибір переживає перезавантаження (N/5)', async ({ page }) => {
    test.slow();
    await signIn(page);
    await open(page);

    const toggle = page.getByTestId('calc-results-toggle');
    await expect(toggle).toBeVisible({ timeout: 60_000 });

    for (let i = 0; i < Repeats; i += 1) {
      const before = await toggle.getAttribute('aria-expanded');
      await toggle.click();
      await expect(toggle).toHaveAttribute('aria-expanded', before === 'true' ? 'false' : 'true');
    }

    const chosen = await toggle.getAttribute('aria-expanded');
    await page.reload();
    await expect(page.getByTestId('calc-results-toggle')).toHaveAttribute('aria-expanded', chosen ?? 'false', {
      timeout: 60_000,
    });
    await shot(page, 'calc');
  });
});

/** Правка комірки: редактор відкрито ДОСТЕМЕННО (повтор dblclick), нове унікальне значення, PATCH прийнято. */
let editSeq = Date.now() % 100_000;
async function editCell(page: Page, cell: Locator): Promise<void> {
  const editor = page.locator('revogr-edit');
  if ((await editor.count()) > 0) {
    await page.keyboard.press('Escape');
    await expect(editor).toHaveCount(0);
  }
  await expect(async () => {
    if ((await editor.count()) === 0) await cell.dblclick();
    await expect(editor).toHaveCount(1, { timeout: 2_000 });
  }).toPass({ timeout: 20_000 });

  editSeq += 1;
  await page.keyboard.type(String(editSeq));
  // Автозбереження: PATCH пішов і прийнятий (індикатор «saved» мигає коротко — не опора).
  const saved = page.waitForResponse((r) => r.request().method() === 'PATCH' && r.ok(), { timeout: 30_000 });
  await page.keyboard.press('Enter');
  await saved;
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

/** Шаблон із `SheetCount` аркушами (по таблиці з одним стовпцем), опублікований; проєкт і документ на ньому. */
async function createMultiSheetDocument(page: Page): Promise<{ documentId: number; periodKey: number }> {
  const stamp = String(Date.now());
  const template = await ok<{ templateId: number }>(
    await page.request.post('/api/v1/templates', { data: { code: `E2ESheets${stamp}`, nameL10n: { en: `E2E sheets ${stamp}` } } }),
    'заведення шаблону',
  );
  const version = await ok<{ versionId: number }>(
    await page.request.post(`/api/v1/templates/${String(template.templateId)}/versions`, {
      data: { versionNumber: '1.0.0.0', cloneFromVersionId: null },
    }),
    'заведення версії шаблону',
  );
  const base = `/api/v1/template-versions/${String(version.versionId)}`;

  for (let n = 1; n <= SheetCount; n += 1) {
    const code = `S${String(n)}`;
    await ok(
      await page.request.put(`${base}/sheets/${code}`, {
        data: { nameL10n: { en: `Sheet ${String(n)} with a long descriptive name` }, ordinal: n, isMandatory: true, isVisible: true, sheetGroup: null },
      }),
      `аркуш ${code}`,
    );
    const table = await ok<{ id: number }>(
      await page.request.put(`${base}/sheets/${code}/tables/T${String(n)}`, {
        data: { nameL10n: { en: `Table ${String(n)}` }, ordinal: 0, layoutKind: 'Static', rowMode: 'Dynamic', maxDynamicRows: 50 },
      }),
      `таблиця аркуша ${code}`,
    );
    await ok(
      await page.request.put(`${base}/tables/${String(table.id)}/columns/C1`, {
        data: {
          headerL10n: { en: 'C1' },
          ordinal: 0,
          dataType: 'String',
          isRequired: false,
          isReadOnly: false,
          isHidden: false,
          precision: null,
          scale: null,
          defaultValue: null,
          displayFormat: null,
          lookupFilter: null,
          lookupRegistryDefId: null,
          styleId: null,
          unitId: null,
        },
      }),
      `стовпець ${code}`,
    );
  }

  await ok(await page.request.post(`${base}/publish`, { data: { reason: 'e2e: документ із кількома аркушами' } }), 'публікація версії шаблону');
  const policies = await ok<{ id: number; code: string }[]>(await page.request.get('/api/v1/projects/period-policies'), 'політики періодів');
  const policy = policies.find((item) => item.code === 'ECR-Standard') ?? policies[0];
  expect(policy, 'у стенда немає жодної політики періодів').toBeDefined();
  const project = await ok<{ projectId: number }>(
    await page.request.post('/api/v1/projects', {
      data: {
        code: `E2ESheetsPrj${stamp}`,
        nameL10n: { en: `E2E sheets project ${stamp}` },
        periodKind: 'Monthly',
        timeZoneId: 'UTC',
        templateVersionId: version.versionId,
        periodPolicyId: policy?.id,
        year: new Date().getUTCFullYear(),
      },
    }),
    'проєкт',
  );
  await ok(await page.request.post(`/api/v1/projects/${String(project.projectId)}/activate`), 'активація проєкту');
  const structure = await ok<{ sheets: { id: number }[] }>(await page.request.get(`${base}/structure`), 'структура версії');
  const document = await ok<{ documentId: number }>(
    await page.request.post('/api/v1/documents', {
      data: { projectId: project.projectId, sheetDefIds: structure.sheets.map((sheet) => sheet.id), templateVersionId: version.versionId },
    }),
    'документ',
  );
  const calendar = await ok<{ periods: { periodKey: number; state: string }[] }>(
    await page.request.get(`/api/v1/projects/${String(project.projectId)}/periods`),
    'календар проєкту',
  );
  const period = calendar.periods.find((item) => item.state === 'Open') ?? calendar.periods[0];
  expect(period, 'у проєкту немає періодів').toBeDefined();

  return { documentId: document.documentId, periodKey: period?.periodKey ?? 0 };
}

async function open(page: Page): Promise<void> {
  await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
  await expect(page.locator('revo-grid:visible').first()).toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('sheet-full-toggle')).toBeVisible({ timeout: 60_000 });
}

async function shot(page: Page, name: string): Promise<void> {
  const size = page.viewportSize()?.width ?? 0;
  const file = `${ShotTag}-${name}-${String(size)}.png`;
  // Без каталогу знімків — у вкладення прогону (test-results, потрапляє в артефакт CI).
  await page.screenshot({ path: ShotsDir === '' ? test.info().outputPath(file) : `${ShotsDir}\\${file}` });
}

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });
  await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
  // ⚠ Роль, а не підпис: підпис «Password» має й кнопка-тумблер видимості (`LoginPage.tsx`).
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Operator.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
