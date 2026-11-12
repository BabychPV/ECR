import { expect, test, type Page } from '@playwright/test';
import { createMultiSheetDocument, signIn } from './multiSheet';

/**
 * «Calculation results — усі місяці року» (CALC-RESULTS.md) на живому стенді (`tools/e2e-stand.ps1 -Grep "calc results year"`).
 *
 * (1) Справжній сервер: документ → «All months» у смузі таблиці → `/documents/:id/results`; новий документ не має
 *     прогонів — сторінка показує стан «нічого не пораховано» з дією «Open the document» (тобто `GET …/year` = 200).
 * (2) Огляд із числами: відповіді `GET …/calculation-results/year/summary` і `…/year/rows` підмінено (`page.route`)
 *     зведенням і сторінкою з кодами СПРАВЖНЬОГО аркуша й таблиці документа — бо сід стенда не має методологій із
 *     прогонами; пошук «сервер» фікстури виконує сам (`q`). Перевіряється клієнт: зведення першим, рядки першої таблиці
 *     сторінкою, місяці й значення, пошук із `q` у запиті, шторка значення й перехід «Open source cell» на документ
 *     (аркуш, таблиця, місяць). Зведення й сторінки на сервері доводять `CalculationYearViewTests` (Api, справжній SQL).
 *
 * ⛔ Без стенда файл ПАДАЄ (вхід/запити до API), а не пропускається (honesty-guard).
 */
test.describe.configure({ timeout: 180_000 });

interface DocumentTable {
  readonly sheetCode: string;
  readonly tableCode: string;
  readonly sheetDefId: number;
  readonly tableDefId: number;
}

async function firstTable(page: Page, documentId: number, periodKey: number): Promise<DocumentTable> {
  const response = await page.request.get(`/api/v1/documents/${String(documentId)}/tables?periodKey=${String(periodKey)}`);
  expect(response.ok(), `tables: ${String(response.status())}`).toBe(true);
  const tables = (await response.json()) as DocumentTable[];
  expect(tables.length).toBeGreaterThan(0);
  return tables[0]!;
}

const none = (n: number): null[] => Array.from({ length: n }, () => null);
const en = (text: string) => ({ values: { en: text } });

/** Рядки таблиці (сторінка `…/year/rows`); пошук `q` — як на сервері: назва або IDCode. */
function yearRows(table: DocumentTable, q: string): unknown {
  const equipment = [
    { table: 0, rowKey: 'R1', labelL10n: null, name: 'Boiler B-1', idCode: 'ES-001' },
    { table: 0, rowKey: 'R2', labelL10n: null, name: 'Flare FS-101', idCode: 'ES-002' },
  ];
  const rows = [
    { id: 'r-boiler', equipment: 0, methodology: 0, outputCode: 'E_CO2', column: null, substance: 0, unit: 0, values: ['1.5', '2.5', ...none(10)], states: 'vvnnnnnnnnnn', yearTotal: '4', monthsIncluded: 2 },
    { id: 'r-flare', equipment: 1, methodology: 0, outputCode: 'E_CO2', column: null, substance: 0, unit: 0, values: ['7.25', null, ...none(10)], states: 'vannnnnnnnnn', yearTotal: '7.25', monthsIncluded: 1 },
  ].filter((row) => q === '' || [equipment[row.equipment]!.name, equipment[row.equipment]!.idCode].some((t) => t.toLowerCase().includes(q.toLowerCase())));
  return {
    table: String(table.tableDefId),
    total: rows.length,
    nextCursor: null,
    equipment,
    methodologies: [{ methodologyId: 1, code: 'M-01', nameL10n: en('Stationary combustion'), version: '2.0' }],
    substances: [{ id: 3, code: 'CO2', nameL10n: en('Carbon dioxide') }],
    units: [{ id: 1, code: 't', symbolL10n: en('t'), dimension: 'Mass', additive: true }],
    columns: [],
    rows,
  };
}

/** Зведення (`…/year/summary`) з кодами СПРАВЖНЬОГО аркуша й таблиці документа. */
function yearSummary(documentId: number, year: number, table: DocumentTable, matched: number): unknown {
  return {
    documentId,
    year,
    months: Array.from({ length: 12 }, (_, i) => ({
      month: i + 1,
      periodKey: year * 100 + i + 1,
      periodState: i < 2 ? 'open' : 'notOpened',
      runState: i === 0 ? 'current' : i === 1 ? 'outOfDate' : 'notOpened',
      calculatedAt: i < 2 ? `${String(year)}-02-14T09:12:00Z` : null,
    })),
    tables: [
      { key: String(table.tableDefId), sheetDefId: table.sheetDefId, sheetCode: table.sheetCode, sheetNameL10n: en('Air emissions'), sheetCopyId: null, tableDefId: table.tableDefId, tableCode: table.tableCode, tableNameL10n: en('Combustion sources'), rows: 2, matched, problems: Math.min(matched, 1) },
    ],
    methodologies: [{ code: 'M-01', nameL10n: en('Stationary combustion') }],
    substances: [{ code: 'CO2', nameL10n: en('Carbon dioxide') }],
    hasFormulas: false,
    totalRows: 2,
    matchedRows: matched,
    problemRows: Math.min(matched, 1),
    freshnessHidden: false,
  };
}

test('calc results year: All months from the document — real endpoint, empty year', async ({ page }) => {
  await signIn(page);
  const doc = await createMultiSheetDocument(page, 1, 3);

  await page.goto(`/documents/${String(doc.documentId)}?periodKey=${String(doc.periodKey)}`);
  const allMonths = page.getByTestId('doc-cr-all');
  await expect(allMonths).toBeVisible({ timeout: 60_000 });
  const year = page.waitForResponse((r) => r.url().includes('/calculation-results/year/summary'));
  await allMonths.click();

  await expect(page).toHaveURL(new RegExp(`/documents/${String(doc.documentId)}/results\\?year=\\d{4}`));
  expect((await year).status()).toBe(200);
  await expect(page.getByRole('heading', { name: 'Calculation results' })).toBeVisible();
  await expect(page.getByText('Nothing has been calculated yet')).toBeVisible();
});

test('calc results year: months, filter, drawer and Open source cell', async ({ page }) => {
  await signIn(page);
  const doc = await createMultiSheetDocument(page, 1, 3);
  const table = await firstTable(page, doc.documentId, doc.periodKey);
  const year = Math.floor(doc.periodKey / 100);
  const q = (url: string): string => new URL(url).searchParams.get('q') ?? '';
  await page.route('**/calculation-results/year/rows?**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(yearRows(table, q(route.request().url()))) }),
  );
  await page.route('**/calculation-results/year/summary?**', (route) => {
    const matched = (yearRows(table, q(route.request().url())) as { total: number }).total;
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(yearSummary(doc.documentId, year, table, matched)) });
  });

  await page.goto(`/documents/${String(doc.documentId)}/results?year=${String(year)}`);
  const grid = page.getByTestId('calc-results-grid');
  await expect(grid).toBeVisible({ timeout: 60_000 });
  await expect(grid.getByRole('columnheader')).toHaveCount(15);
  await expect(grid.locator('[data-cr-row="r-boiler"]')).toContainText('2/12');
  await expect(grid.locator('[data-cr-cell="r-flare#1"]')).toHaveText('7.25');

  // ⚠ Не `getByLabel('Search')`: такий підпис має й глобальний пошук оболонки застосунку.
  const searched = page.waitForRequest((r) => r.url().includes('/year/rows') && r.url().includes('q=ES-002'));
  await page.getByTestId('cr-search').fill('ES-002');
  await searched;
  await expect(grid.locator('[data-cr-row="r-boiler"]')).toHaveCount(0);

  await grid.locator('[data-cr-cell="r-flare#1"]').click();
  const open = page.getByTestId('cr-open-source');
  await expect(open).toBeVisible();
  await open.click();

  await expect(page).toHaveURL(
    new RegExp(`/documents/${String(doc.documentId)}\\?periodKey=${String(year)}01&sheet=${table.sheetCode}&table=${table.tableCode}`),
  );
});
