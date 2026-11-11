import { expect, test, type Page } from '@playwright/test';
import { createMultiSheetDocument, signIn } from './multiSheet';

/**
 * «Calculation results — усі місяці року» (CALC-RESULTS.md) на живому стенді (`tools/e2e-stand.ps1 -Grep "calc results year"`).
 *
 * (1) Справжній сервер: документ → «All months» у смузі таблиці → `/documents/:id/results`; новий документ не має
 *     прогонів — сторінка показує стан «нічого не пораховано» з дією «Open the document» (тобто `GET …/year` = 200).
 * (2) Огляд із числами: відповідь `GET …/calculation-results/year` підмінено (`page.route`) оглядом із кодами
 *     СПРАВЖНЬОГО аркуша й таблиці документа — бо сід стенда не має методологій із прогонами. Перевіряється клієнт:
 *     місяці й значення, фільтр, шторка значення й перехід «Open source cell» на документ (аркуш, таблиця, місяць).
 *     Зведення на сервері доводять `CalculationYearViewTests` (Api, справжній SQL).
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

function yearView(documentId: number, year: number, table: DocumentTable): unknown {
  const none = (n: number): null[] => Array.from({ length: n }, () => null);
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
      { sheetDefId: table.sheetDefId, sheetCode: table.sheetCode, sheetNameL10n: { values: { en: 'Air emissions' } }, sheetCopyId: null, tableDefId: table.tableDefId, tableCode: table.tableCode, tableNameL10n: { values: { en: 'Combustion sources' } } },
    ],
    equipment: [
      { table: 0, rowKey: 'R1', labelL10n: null, name: 'Boiler B-1', idCode: 'ES-001' },
      { table: 0, rowKey: 'R2', labelL10n: null, name: 'Flare FS-101', idCode: 'ES-002' },
    ],
    methodologies: [{ methodologyId: 1, code: 'M-01', nameL10n: { values: { en: 'Stationary combustion' } }, version: '2.0' }],
    substances: [{ id: 3, code: 'CO2', nameL10n: { values: { en: 'Carbon dioxide' } } }],
    units: [{ id: 1, code: 't', symbolL10n: { values: { en: 't' } }, dimension: 'Mass', additive: true }],
    columns: [],
    rows: [
      { id: 'r-boiler', equipment: 0, methodology: 0, outputCode: 'E_CO2', column: null, substance: 0, unit: 0, values: ['1.5', '2.5', ...none(10)], states: 'vvnnnnnnnnnn', yearTotal: '4', monthsIncluded: 2 },
      { id: 'r-flare', equipment: 1, methodology: 0, outputCode: 'E_CO2', column: null, substance: 0, unit: 0, values: ['7.25', null, ...none(10)], states: 'vannnnnnnnnn', yearTotal: '7.25', monthsIncluded: 1 },
    ],
    freshnessHidden: false,
  };
}

test('calc results year: All months from the document — real endpoint, empty year', async ({ page }) => {
  await signIn(page);
  const doc = await createMultiSheetDocument(page, 1, 3);

  await page.goto(`/documents/${String(doc.documentId)}?periodKey=${String(doc.periodKey)}`);
  const allMonths = page.getByTestId('doc-cr-all');
  await expect(allMonths).toBeVisible({ timeout: 60_000 });
  const year = page.waitForResponse((r) => r.url().includes('/calculation-results/year'));
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
  await page.route('**/calculation-results/year?**', (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(yearView(doc.documentId, year, table)) }),
  );

  await page.goto(`/documents/${String(doc.documentId)}/results?year=${String(year)}`);
  const grid = page.getByTestId('calc-results-grid');
  await expect(grid).toBeVisible({ timeout: 60_000 });
  await expect(grid.getByRole('columnheader')).toHaveCount(15);
  await expect(grid.locator('[data-cr-row="r-boiler"]')).toContainText('2/12');
  await expect(grid.locator('[data-cr-cell="r-flare#1"]')).toHaveText('7.25');

  await page.getByLabel('Search').fill('ES-002');
  await expect(grid.locator('[data-cr-row="r-boiler"]')).toHaveCount(0);

  await grid.locator('[data-cr-cell="r-flare#1"]').click();
  const open = page.getByTestId('cr-open-source');
  await expect(open).toBeVisible();
  await open.click();

  await expect(page).toHaveURL(
    new RegExp(`/documents/${String(doc.documentId)}\\?periodKey=${String(year)}01&sheet=${table.sheetCode}&table=${table.tableCode}`),
  );
});
