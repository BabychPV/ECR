import { expect, test, type Page } from '@playwright/test';

/**
 * Закріплені ключові колонки сітки документа у СПРАВЖНЬОМУ `DocumentGrid` і справжньому RevoGrid:
 * типове закріплення видно після горизонтальної прокрутки; вибір користувача переживає перезавантаження;
 * стрілки через межу закріплення; копія з закріпленої колонки -> вставка в прокручувану ляже в ту колонку;
 * «None»/«Default»; межа читається в обох темах.
 *
 * ⚠ Стенд БД не потрібен (як `cellColor.spec.ts`): сторінка `/_key-commit-gate/document` (DEV-маршрут) показує
 * справжній `DocumentGrid`. Її власний «сервер» (2 колонки) тут замінено широким зрізом: `addInitScript` ставить
 * прапорець `__standFetchInstalled` ДО скриптів застосунку, і сторінка свій `fetch` не підміняє. Налаштування
 * користувача (`/me/preferences`) тримаються в `sessionStorage` — вони переживають `page.reload()`, як на сервері.
 * Запуск без стенда: `ECR_E2E_OPTIONAL=1 npx playwright test e2e/gridFrozenColumns.spec.ts`.
 *
 * ⚠ Фікстура: підпис рядка + CODE, NAME (рядкові, вузькі) + M1..M8 (десяткові). Типово закріплюються підпис, CODE,
 * NAME (3 колонки); M1 - перша прокручувана. Ширина підпису стиснута налаштуванням користувача (90 px), щоб у стенді
 * шириною 480 px лишалася прокручувана частина.
 */
const DocStand = '/_key-commit-gate/document';
const TableDefId = 9_001;
/** Три закріплені колонки по 90 px = 270 px; з запасом на межу й рамки. */
const PinnedWidthCeiling = 300;
const Rows = 6;

interface StandProbe {
  __patches: { rowKey: string; cells: { columnCode: string; value: unknown }[] }[];
  __prefWrites: { method: string; key: string; value: unknown }[];
}

/** Виконується У БРАУЗЕРІ до скриптів застосунку: широкий зріз, PATCH, довідники, налаштування користувача. */
function installWideStand(config: { documentId: number; tableInstanceId: number; tableDefId: number; rows: number }): void {
  const w = window as unknown as StandProbe & { __standFetchInstalled?: boolean };
  w.__standFetchInstalled = true;
  w.__patches = [];
  w.__prefWrites = [];

  const prefsKey = 'e2e.frozen.prefs';
  const readPrefs = (): { key: string; value: unknown; updatedAt: string }[] => {
    const raw = sessionStorage.getItem(prefsKey);
    if (raw !== null) return JSON.parse(raw) as { key: string; value: unknown; updatedAt: string }[];

    // Підпис рядка стиснуто до 90 px (налаштування користувача).
    return [{ key: `grid.columnWidths.${String(config.tableDefId)}`, value: { __rowLabel: 90 }, updatedAt: '2026-11-05T00:00:00Z' }];
  };
  const writePrefs = (items: unknown[]): void => sessionStorage.setItem(prefsKey, JSON.stringify(items));

  const column = (code: string, ordinal: number, dataType: string, widthPx: number | null) => ({
    code,
    dataType,
    defaultValue: null,
    displayFormat: null,
    header: code,
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

  const columns = [
    column('CODE', 0, 'String', 90),
    column('NAME', 1, 'String', 90),
    ...Array.from({ length: 8 }, (_, i) => column(`M${String(i + 1)}`, i + 2, 'Decimal', null)),
  ];
  const stored = new Map<string, Record<string, unknown>>();

  const slice = () => ({
    cellConfirmations: {},
    cellPermissions: {},
    cellStyles: {},
    periodKey: 202_609,
    tableInstanceId: config.tableInstanceId,
    columns,
    rows: Array.from({ length: config.rows }, (_, index) => {
      const rowKey = `R${String(index + 1)}`;
      const cells: Record<string, unknown> = { CODE: `C${String(index + 1)}`, NAME: String(101 + index) };
      for (let m = 1; m <= 8; m += 1) cells[`M${String(m)}`] = String(m);

      return {
        cells: { ...cells, ...stored.get(rowKey) },
        isOrphaned: false,
        label: `Row ${String(index + 1)}`,
        ordinal: index,
        rowKey,
        rowVersion: `v${String(index)}`,
        rowKind: 'Item',
      };
    }),
  });

  const json = (body: unknown, status = 200): Response =>
    new Response(status === 204 ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

  const original = window.fetch.bind(window);
  window.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    const path = new URL(url, window.location.origin).pathname;
    const method = init?.method ?? 'GET';

    if (new RegExp(`/documents/${String(config.documentId)}/cells$`).test(path) && method === 'PATCH') {
      const rows = (JSON.parse(String(init?.body)) as { rows: StandProbe['__patches'] }).rows;
      w.__patches.push(...rows);
      for (const row of rows) {
        const cells = { ...stored.get(row.rowKey) };
        for (const cell of row.cells) cells[cell.columnCode] = cell.value;
        stored.set(row.rowKey, cells);
      }

      return json({ appliedCells: rows.length, rowVersions: {}, validation: [] });
    }
    if (new RegExp(`/documents/${String(config.documentId)}/tables/${String(config.tableInstanceId)}$`).test(path)) {
      return json(slice());
    }
    if (/\/(registries|units)$/.test(path)) return json([]);
    if (/\/me\/preferences$/.test(path)) return json(readPrefs());

    const single = /\/me\/preferences\/([^/]+)$/.exec(path);
    if (single !== null) {
      const key = decodeURIComponent(single[1] ?? '');
      const rest = readPrefs().filter((item) => item.key !== key);
      if (method === 'PUT') {
        const value = JSON.parse(String(init?.body)) as unknown;
        w.__prefWrites.push({ method, key, value });
        writePrefs([...rest, { key, value, updatedAt: new Date().toISOString() }]);

        return json({ key, value, updatedAt: new Date().toISOString() });
      }
      if (method === 'DELETE') {
        w.__prefWrites.push({ method, key, value: null });
        writePrefs(rest);

        return json(null, 204);
      }
    }

    return original(input, init);
  };
}

async function open(page: Page, scheme: 'light' | 'dark' = 'light'): Promise<void> {
  await page.emulateMedia({ colorScheme: scheme });
  await page.addInitScript(installWideStand, {
    documentId: 9_001,
    tableInstanceId: 9_001,
    tableDefId: TableDefId,
    rows: Rows,
  });
  await page.goto(DocStand);
  await expect(pinnedCell(page, 0, 2)).toBeVisible({ timeout: 30_000 });
}

/**
 * Закріплений в'юпорт: `col` - 0 підпис, 1 CODE, 2 NAME.
 * ⚠ Рядок підсумків (`pinnedBottomSource`) має ті самі `data-rgrow`/`data-rgcol` у власному ряду - виключено явно.
 */
const pinnedCell = (page: Page, row: number, col: number) =>
  page.locator(
    `revo-grid revogr-viewport-scroll.colPinStart .rgCell:not([data-grid-totals])[data-rgrow="${String(row)}"][data-rgcol="${String(col)}"]`,
  );

/** Прокручуваний в'юпорт: `col` - 0 M1, 1 M2, ... (без рядка підсумків). */
const scrolledCell = (page: Page, row: number, col: number) =>
  page.locator(
    `revo-grid revogr-viewport-scroll.rgCol .rgCell:not([data-grid-totals])[data-rgrow="${String(row)}"][data-rgcol="${String(col)}"]`,
  );

const header = (page: Page, name: string) =>
  page.locator('revo-grid revogr-header .rgHeaderCell').filter({ hasText: new RegExp(`^${name}$`) });

const freezeSelect = (page: Page) => page.locator('[data-frozen-columns] select');

const formulaBar = (page: Page) => page.locator('[data-formula-bar]');

const patches = (page: Page) => page.evaluate(() => (window as unknown as StandProbe).__patches);

const prefWrites = (page: Page) => page.evaluate(() => (window as unknown as StandProbe).__prefWrites);

test('типове закріплення: підпис, CODE і NAME лишаються на місці після горизонтальної прокрутки', async ({ page }) => {
  await open(page);

  const grid = page.locator('revo-grid');
  const before = await grid.boundingBox();
  expect(before).not.toBeNull();

  await expect(pinnedCell(page, 0, 0)).toHaveText('Row 1');
  await expect(freezeSelect(page)).toHaveValue('default');

  // Прокручуємо до кінця прокручуваної частини: M1 виходить, M8 з'являється.
  await grid.evaluate(async (element) => {
    await (element as unknown as { scrollToColumnIndex: (index: number) => Promise<void> }).scrollToColumnIndex(7);
  });
  await expect(header(page, 'M8')).toBeVisible();

  const left = before?.x ?? 0;
  const right = left + (before?.width ?? 0);

  // Закріплені заголовки лишилися в межах сітки зліва (не поїхали разом з прокруткою), а M8 - в межах справа.
  for (const name of ['CODE', 'NAME']) {
    await expect(header(page, name)).toBeVisible();
    const box = await header(page, name).boundingBox();
    expect(box?.x ?? -1).toBeGreaterThanOrEqual(left - 1);
    expect((box?.x ?? 0) + (box?.width ?? 0)).toBeLessThanOrEqual(left + PinnedWidthCeiling);
  }
  const last = await header(page, 'M8').boundingBox();
  expect((last?.x ?? 0) + (last?.width ?? 0)).toBeLessThanOrEqual(right + 1);
  await expect(pinnedCell(page, 0, 2)).toHaveText('101');
});

test('вибір «None» переживає перезавантаження, «Default» його скидає', async ({ page }) => {
  await open(page);

  await freezeSelect(page).selectOption('0');
  await expect(page.locator('revo-grid revogr-viewport-scroll.colPinStart .rgCell')).toHaveCount(0);
  await expect.poll(async () => (await prefWrites(page)).map((w) => `${w.method}:${w.key}:${JSON.stringify(w.value)}`)).toEqual([
    `PUT:grid.frozenColumns.${String(TableDefId)}:0`,
  ]);

  await page.reload();
  await expect(scrolledCell(page, 0, 0)).toBeVisible({ timeout: 30_000 });
  await expect(freezeSelect(page)).toHaveValue('0');
  await expect(page.locator('revo-grid revogr-viewport-scroll.colPinStart .rgCell')).toHaveCount(0);

  await freezeSelect(page).selectOption('default');
  await expect(pinnedCell(page, 0, 2)).toBeVisible();
  await expect.poll(async () => (await prefWrites(page)).map((w) => w.method)).toEqual(['DELETE']);

  await page.reload();
  await expect(pinnedCell(page, 0, 2)).toBeVisible({ timeout: 30_000 });
  await expect(freezeSelect(page)).toHaveValue('default');
});

test('вибір «до колонки NAME» закріплює рівно підпис, CODE і NAME і переживає перезавантаження', async ({ page }) => {
  await open(page);

  // Значення = кількість закріплених колонок з підписом: 1 підпис, 2 CODE, 3 NAME, 4 M1.
  await freezeSelect(page).selectOption('4');
  await expect(pinnedCell(page, 0, 3)).toHaveText('1');

  await page.reload();
  await expect(pinnedCell(page, 0, 3)).toBeVisible({ timeout: 30_000 });
  await expect(freezeSelect(page)).toHaveValue('4');
});

test('стрілки через межу закріплення: фокус іде в сусідню колонку, а не в «ту саму» за x', async ({ page }) => {
  await open(page);

  await scrolledCell(page, 0, 0).click();
  await expect(formulaBar(page)).toContainText('M1');

  await page.keyboard.press('ArrowLeft');
  await expect(formulaBar(page)).toContainText('NAME');

  await page.keyboard.press('ArrowLeft');
  await expect(formulaBar(page)).toContainText('CODE');

  await page.keyboard.press('ArrowRight');
  await page.keyboard.press('ArrowRight');
  await expect(formulaBar(page)).toContainText('M1');

  await page.keyboard.press('ArrowRight');
  await expect(formulaBar(page)).toContainText('M2');
});

test('копія з закріпленої колонки, вставка в прокручувану: значення лягає в обрану колонку', async ({ page }) => {
  await open(page);

  // NAME рядка Row 2 = «102». Копіюємо з закріпленого в'юпорту...
  await pinnedCell(page, 1, 2).click();
  await expect(formulaBar(page)).toContainText('NAME');
  await page.keyboard.press('Control+c');

  // ...вставляємо в M3 того самого рядка (прокручуваний в'юпорт, x=2 -> глобальна колонка 5).
  await scrolledCell(page, 1, 2).click();
  await expect(formulaBar(page)).toContainText('M3');
  await page.keyboard.press('Control+v');

  await expect
    .poll(async () => (await patches(page)).flatMap((row) => row.cells.map((cell) => `${row.rowKey}:${cell.columnCode}:${String(cell.value)}`)))
    .toEqual(['R2:M3:102']);
  await expect(scrolledCell(page, 1, 2)).toContainText('102');
});

test('вставка з початку закріпленої колонки розкладається через межу: NAME -> M1', async ({ page }) => {
  await open(page);

  await pinnedCell(page, 0, 2).click();
  await expect(formulaBar(page)).toContainText('NAME');
  await page.evaluate(() => navigator.clipboard.writeText('555\t777'));
  await page.keyboard.press('Control+v');

  await expect
    .poll(async () =>
      (await patches(page))
        .flatMap((row) => row.cells.map((cell) => `${row.rowKey}:${cell.columnCode}:${String(cell.value)}`))
        .sort(),
    )
    .toEqual(['R1:M1:777', 'R1:NAME:555']);
});

for (const scheme of ['light', 'dark'] as const) {
  test(`межа закріплених колонок видна в темі ${scheme}`, async ({ page }) => {
    await open(page, scheme);

    const edge = await page.locator('revo-grid revogr-viewport-scroll.colPinStart').evaluate((node) => {
      const style = getComputedStyle(node, '::after');

      return { width: style.width, background: style.backgroundColor, content: style.content };
    });

    expect(edge.width).toBe('1px');
    expect(edge.content).not.toBe('none');
    // Колір береться зі змінної теми, а не літерал: він не прозорий.
    expect(edge.background).not.toBe('rgba(0, 0, 0, 0)');
  });
}

test('межа має різний колір у світлій і темній темі (змінна теми, а не літерал)', async ({ page }) => {
  const edgeColor = () =>
    page.locator('revo-grid revogr-viewport-scroll.colPinStart').evaluate((node) => getComputedStyle(node, '::after').backgroundColor);

  await open(page, 'light');
  const light = await edgeColor();

  await page.emulateMedia({ colorScheme: 'dark' });
  await page.reload();
  await expect(pinnedCell(page, 0, 2)).toBeVisible({ timeout: 30_000 });
  const dark = await edgeColor();

  expect(dark).not.toBe(light);
});
