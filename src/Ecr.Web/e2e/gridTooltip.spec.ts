import { expect, test, type Page } from '@playwright/test';
import { axeViolations } from './axeRule';

/**
 * Підказка повного тексту обрізаних заголовків, групових смуг і комірок у СПРАВЖНЬОМУ `DocumentGrid` і RevoGrid
 * (`src/features/grid/truncationTooltip.ts`). Слова людини 11.11: «тултипи якщо він повністю не відображається …
 * про заголовки та комірки» (документ 96, таблиця 3.00: «Stat…», «Description of equi…», «Rot…»).
 *
 * ⚠ Стенд БД не потрібен (як `gridFrozenColumns.spec.ts`): DEV-маршрут `/_key-commit-gate/document` показує справжній
 * `DocumentGrid`, а його «сервер» замінено зрізом із довгими заголовками (`addInitScript` ставить
 * `__standFetchInstalled` ДО скриптів застосунку). Запуск без стенда:
 * `ECR_E2E_OPTIONAL=1 npx playwright test e2e/gridTooltip.spec.ts`.
 *
 * Фікстура: NAME (80 px, довгий заголовок і довге значення), CODE (200 px, короткий заголовок «Rot» — уміщається),
 * STAT (70 px) у групі з довгою назвою — обрізані і назва колонки, і смуга групи.
 */
const DocStand = '/_key-commit-gate/document';

const LongHeader = 'Description of equipment and its operating location';
const LongValue = 'Gas turbine GT-101 driving the main export compressor';
const GroupedHeader = 'Status of the rotating equipment';
const GroupLabel = 'Rotating equipment inventory for the reporting period';

/** Виконується У БРАУЗЕРІ до скриптів застосунку. */
function installStand(texts: { longHeader: string; longValue: string; groupedHeader: string; groupLabel: string }): void {
  const w = window as unknown as { __standFetchInstalled?: boolean };
  w.__standFetchInstalled = true;

  const column = (code: string, ordinal: number, header: string, widthPx: number, headerGroupId: number | null) => ({
    code,
    dataType: 'String',
    defaultValue: null,
    displayFormat: null,
    header,
    headerGroupId,
    id: ordinal + 1,
    isReadOnly: false,
    isRequired: false,
    isRequiredByMethodology: false,
    lookupRegistryDefId: null,
    ordinal,
    unitId: null,
    unitSymbol: null,
    widthPx,
  });

  const slice = {
    cellConfirmations: {},
    cellPermissions: {},
    cellStyles: {},
    periodKey: 202_609,
    tableInstanceId: 9_001,
    headerGroups: [{ id: 1, parentId: null, label: texts.groupLabel }],
    columns: [
      column('NAME', 0, texts.longHeader, 80, null),
      column('CODE', 1, 'Rot', 200, null),
      column('STAT', 2, texts.groupedHeader, 70, 1),
    ],
    rows: Array.from({ length: 4 }, (_, index) => ({
      cells: { NAME: index === 0 ? texts.longValue : `N${String(index + 1)}`, CODE: `C${String(index + 1)}`, STAT: 'ok' },
      isOrphaned: false,
      label: null,
      ordinal: index,
      rowKey: `R${String(index + 1)}`,
      rowVersion: `v${String(index)}`,
      rowKind: 'Item',
    })),
  };

  const json = (body: unknown): Response => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
  const original = window.fetch.bind(window);

  window.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    const path = new URL(url, window.location.origin).pathname;

    if (/\/documents\/9001\/tables\/9001$/.test(path)) return json(slice);
    if (/\/documents\/9001\/cells$/.test(path) && init?.method === 'PATCH') return json({ appliedCells: 0, rowVersions: {}, validation: [] });
    if (/\/(registries|units|me\/preferences)$/.test(path)) return json([]);

    return original(input, init);
  };
}

async function open(page: Page, scheme: 'light' | 'dark' = 'light'): Promise<void> {
  await page.emulateMedia({ colorScheme: scheme });
  await page.addInitScript(installStand, { longHeader: LongHeader, longValue: LongValue, groupedHeader: GroupedHeader, groupLabel: GroupLabel });
  await page.goto(DocStand);
  await expect(valueCell(page)).toBeVisible({ timeout: 30_000 });
}

const tooltip = (page: Page) => page.locator('[data-truncation-tooltip]');

const header = (page: Page, text: string | RegExp) =>
  page.locator('revo-grid revogr-header .rgHeaderCell').filter({ hasText: text }).last();

const valueCell = (page: Page) => page.locator('revo-grid revogr-viewport-scroll.rgCol .rgCell').filter({ hasText: LongValue }).first();

const codeCell = (page: Page) => page.locator('revo-grid revogr-viewport-scroll.rgCol .rgCell').filter({ hasText: /^C1$/ }).first();

test('наведення на обрізаний заголовок — підказка з повним текстом; на той, що вміщається, — нічого', async ({ page }) => {
  await open(page);

  await header(page, LongHeader).hover();
  await expect(tooltip(page)).toBeVisible();
  await expect(tooltip(page)).toHaveText(LongHeader);
  await expect(tooltip(page)).toHaveAttribute('role', 'tooltip');

  // Вихід миші — ховає. «Rot» уміщається — підказки немає і після затримки.
  // ⚠ Регулярка: рядок у `hasText` — підрядок без регістру, і «Rot» знайшов би «Rotating…» групи.
  await header(page, /^Rot$/).hover();
  await expect(tooltip(page)).toBeHidden();
  await page.waitForTimeout(900);
  await expect(tooltip(page)).toBeHidden();
});

test('обрізана назва колонки в групі й сама смуга групи дають кожна свій повний текст', async ({ page }) => {
  await open(page);

  await header(page, GroupedHeader).hover();
  await expect(tooltip(page)).toHaveText(GroupedHeader);

  await page.locator('revo-grid revogr-header .rgHeaderCell .ecr-hg-label').filter({ hasText: GroupLabel }).hover();
  await expect(tooltip(page)).toHaveText(GroupLabel);
});

test('обрізана комірка: наведення і фокус із клавіатури показують повне значення; Escape ховає', async ({ page }) => {
  await open(page);

  await valueCell(page).hover();
  await expect(tooltip(page)).toHaveText(LongValue);

  // Клік по комірці, що вміщається, — фокус від миші, підказки немає.
  await codeCell(page).click();
  await expect(tooltip(page)).toBeHidden();
  // Фокус, що прийшов з клавіатури (стрілка) на обрізану комірку, — одразу, без наведення.
  await page.waitForTimeout(600);
  await page.keyboard.press('ArrowLeft');
  await expect(tooltip(page)).toBeVisible();
  await expect(tooltip(page)).toHaveText(LongValue);

  await page.keyboard.press('Escape');
  await expect(tooltip(page)).toBeHidden();
});

for (const scheme of ['light', 'dark'] as const) {
  test(`підказка проходить axe (назва, контраст) у темі ${scheme}`, async ({ page }) => {
    await open(page, scheme);

    await header(page, LongHeader).hover();
    await expect(tooltip(page)).toBeVisible();

    for (const rule of ['aria-tooltip-name', 'color-contrast', 'aria-allowed-role']) {
      expect(await axeViolations(page, rule, '[data-truncation-tooltip]'), rule).toEqual([]);
    }
  });
}
