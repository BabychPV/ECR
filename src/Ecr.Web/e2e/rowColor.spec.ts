import { expect, test, type Page } from '@playwright/test';
import { createRowColorDocument, openAs, type RowColorDocument } from './rowColor';

/**
 * Колір тексту на живому стенді (P1 від людини, Land «3.00 Stationary Equipment»): колір тексту, обраний у тулбарі, має
 * ВІДОБРАЖАТИСЬ (computed `color` комірки) — на залитому й на незалитому рядку, у колонці, у діапазоні, у світлій і темній
 * темі, після reload. Комірка із заливкою БЕЗ явного кольору тексту зберігає автоконтраст.
 */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const Repeats = 5;
const Rows = 40;
const Orange = '#ffc000';
const Red = '#c00000';
const Green = '#00b050';

let doc: RowColorDocument | undefined;

const cell = (page: Page, row: number, col: number) =>
  page.locator(`revo-grid:visible revogr-data[type="rgRow"] .rgCell[data-rgrow="${String(row)}"][data-rgcol="${String(col)}"]`);

async function openDocument(page: Page): Promise<void> {
  const current = doc as RowColorDocument;
  await page.goto(`/documents/${String(current.documentId)}?periodKey=${String(current.periodKey)}`);
  await expect(page.locator('revo-grid:visible').first()).toBeVisible({ timeout: 60_000 });
  await expect(cell(page, 0, 2)).toBeVisible({ timeout: 60_000 });
}

async function choose(page: Page, target: 'fill' | 'text', hex: string): Promise<void> {
  await page.getByTestId(`cell-color-${target}`).first().click();
  await page.getByRole('button', { name: hex, exact: true }).first().click();
}

/** Контраст WCAG між computed `color` і computed `background-color` комірки (0, якщо фон прозорий). */
async function contrastOf(page: Page, row: number, col: number): Promise<{ ratio: number; fg: string; bg: string }> {
  return cell(page, row, col).evaluate((element) => {
    const parse = (value: string): number[] => (/\d+(\.\d+)?/g.exec(value) === null ? [0, 0, 0] : (value.match(/\d+(\.\d+)?/g) ?? []).slice(0, 3).map(Number));
    const lum = (rgb: number[]): number => {
      const [r, g, b] = rgb.map((v) => {
        const c = (v ?? 0) / 255;

        return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4);
      });

      return 0.2126 * (r ?? 0) + 0.7152 * (g ?? 0) + 0.0722 * (b ?? 0);
    };
    const style = getComputedStyle(element);
    const l1 = lum(parse(style.color));
    const l2 = lum(parse(style.backgroundColor));

    return { ratio: (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05), fg: style.color, bg: style.backgroundColor };
  });
}

test.describe('rowColor: колір тексту', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');
  test.describe.configure({ mode: 'serial', timeout: 600_000 });

  for (const scheme of ['light', 'dark'] as const) {
    test(`колір тексту: заливка рядка, без заливки, діапазон, колонка, reload — тема ${scheme} (N/5)`, async ({ browser }) => {
      const page = await openAs(browser, scheme);

      try {
        doc ??= await createRowColorDocument(page, Rows);
        await openDocument(page);
        const picker = page.getByTestId('cell-color-scope');

        for (let i = 0; i < Repeats; i += 1) {
          const label = `${scheme} ${String(i + 1)}/${String(Repeats)}`;
          const colour = i % 2 === 0 ? Red : Green;
          const rgb = i % 2 === 0 ? 'rgb(192, 0, 0)' : 'rgb(0, 176, 80)';
          const filledRow = 1 + i;
          const plainRow = 8 + i;

          // «Весь рядок» із заливкою, потім колір тексту на тому ж рядку.
          await cell(page, filledRow, 2).click();
          await picker.selectOption('row');
          await choose(page, 'fill', Orange);
          await expect(cell(page, filledRow, 2), `${label}: заливка рядка`).toHaveAttribute('data-user-fill', 'true', { timeout: 15_000 });
          await choose(page, 'text', colour);
          await expect(cell(page, filledRow, 2), `${label}: дані користувацького тексту`).toHaveAttribute('data-user-text', 'true', { timeout: 15_000 });
          if (i === 0) await page.screenshot({ path: test.info().outputPath(`rowcolor-${scheme}.png`) });
          await expect(cell(page, filledRow, 2), `${label}: computed color на ЗАЛИТОМУ рядку`).toHaveCSS('color', rgb);
          await expect(cell(page, filledRow, 6), `${label}: computed color, інша колонка рядка`).toHaveCSS('color', rgb);

          // Той самий колір на незалитому рядку (лише комірка).
          await cell(page, plainRow, 2).click();
          await picker.selectOption('selection');
          await choose(page, 'text', colour);
          await expect(cell(page, plainRow, 2), `${label}: computed color на НЕЗАЛИТОМУ рядку`).toHaveCSS('color', rgb);

          // Колонка і діапазон.
          await cell(page, 14, 4).click();
          await picker.selectOption('column');
          await choose(page, 'text', colour);
          await expect(cell(page, 15, 4), `${label}: колонка`).toHaveCSS('color', rgb);
          await picker.selectOption('selection');
          await cell(page, 18, 8).click();
          await page.keyboard.down('Shift');
          await cell(page, 20, 10).click();
          await page.keyboard.up('Shift');
          await choose(page, 'text', colour);
          await expect(cell(page, 19, 9), `${label}: діапазон`).toHaveCSS('color', rgb);

          // Reload: колір приходить із сервера й відображається.
          await page.reload();
          await expect(cell(page, filledRow, 2)).toBeVisible({ timeout: 60_000 });
          await expect(cell(page, filledRow, 2), `${label}: після reload, залитий рядок`).toHaveCSS('color', rgb);
          await expect(cell(page, plainRow, 2), `${label}: після reload, незалитий рядок`).toHaveCSS('color', rgb);
        }
      } finally {
        await page.context().close();
      }
    });
  }

  for (const scheme of ['light', 'dark'] as const) {
    test(`автоконтраст: заливка без явного кольору тексту — тема ${scheme}`, async ({ browser }, testInfo) => {
      const page = await openAs(browser, scheme);

      try {
        doc ??= await createRowColorDocument(page, Rows);
        await openDocument(page);
        const fills = [Orange, '#ed7d31', '#a5a5a5', '#5b9bd5', '#70ad47', '#4472c4', '#ffff00', '#92d050', '#00b050', '#00b0f0', '#0070c0', '#ff0000', '#7030a0', '#44546a', '#e7e6e6'];
        const results: { fill: string; ratio: number; fg: string; bg: string }[] = [];

        await page.getByTestId('cell-color-scope').selectOption('selection');

        for (const [index, fill] of fills.entries()) {
          const row = 20 + index;

          await cell(page, row, 2).click();
          await choose(page, 'fill', fill);
          await expect(cell(page, row, 2), `${fill}: заливка`).toHaveAttribute('data-user-fill', 'true', { timeout: 15_000 });
          await expect(cell(page, row, 2), `${fill}: явного кольору тексту немає`).not.toHaveAttribute('data-user-text', 'true');
          results.push({ fill, ...(await contrastOf(page, row, 2)) });
        }

        await testInfo.attach(`contrast-${scheme}.json`, { body: JSON.stringify(results), contentType: 'application/json' });
        console.log(`AUTOCONTRAST ${scheme} ${JSON.stringify(results.map((r) => `${r.fill}=${r.ratio.toFixed(2)}`))}`);
        const worst = results.reduce((a, b) => (a.ratio <= b.ratio ? a : b));
        expect(worst.ratio, `${scheme}: найгірший автоконтраст на заливці без явного кольору ${JSON.stringify(worst)}`).toBeGreaterThanOrEqual(4.5);
      } finally {
        await page.context().close();
      }
    });
  }
});
