import { expect, test } from '@playwright/test';
import { createMultiSheetDocument, signIn } from './multiSheet';

/**
 * Знімки «до/після» для ui-sheet-bottom: документ із 8 аркушами й широкою таблицею, 1440 і 500 px.
 * Той самий файл ганяється і на main (знімки «до»), і на lane (знімки «після»); тег — `ECR_SHEETBOTTOM_SHOT_TAG`.
 * Знімки потрапляють у вкладення прогону (test-results → артефакт CI).
 */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const Tag = process.env['ECR_SHEETBOTTOM_SHOT_TAG'] ?? 'after';

test.describe('uiSheetBottom-shots', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('знімки 1440 і 500: аркуші, прокрутка, повний екран', async ({ page }) => {
    test.setTimeout(300_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);
    const doc = await createMultiSheetDocument(page, 8, 30);

    for (const [width, height] of [[1440, 900], [500, 900]] as const) {
      await page.setViewportSize({ width, height });
      await page.goto(`/documents/${String(doc.documentId)}?periodKey=${String(doc.periodKey)}`);
      await expect(page.locator('revo-grid:visible').first()).toBeVisible({ timeout: 60_000 });
      await expect(page.getByRole('tablist').getByRole('tab')).toHaveCount(8, { timeout: 60_000 });
      // Дати сторінці й сітці устатися (лінива сітка, вкладки, смуга).
      await page.waitForTimeout(1500);
      await page.screenshot({ path: test.info().outputPath(`${Tag}-doc-${String(width)}.png`) });
      await page.screenshot({ path: test.info().outputPath(`${Tag}-doc-full-${String(width)}.png`), fullPage: true });
    }

    await page.setViewportSize({ width: 1440, height: 900 });
    await page.goto(`/documents/${String(doc.documentId)}?periodKey=${String(doc.periodKey)}&full=1`);
    await page.waitForTimeout(2500);
    await page.screenshot({ path: test.info().outputPath(`${Tag}-full-1440.png`) });
  });
});
