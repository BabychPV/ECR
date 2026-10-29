import { expect, test } from '@playwright/test';
import { createGroupedDocument } from './colGroups';
import { createMultiSheetDocument, signIn } from './multiSheet';

/**
 * Знімки «до/після» для ui-colgroups: «до» — документ без груп (одна шапка), «після» — з 3 рівнями груп;
 * 1440 і 500 px, світла й темна теми, плюс конструктор (діалог груп, перегляд). Знімки — у test-results → артефакт CI.
 */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

test.describe('uiColgroups-shots', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('знімки 1440 і 500, light і dark', async ({ page }) => {
    test.setTimeout(420_000);
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page);
    const flat = await createMultiSheetDocument(page, 1, 12);
    const grouped = await createGroupedDocument(page);

    for (const scheme of ['light', 'dark'] as const) {
      await page.emulateMedia({ colorScheme: scheme });
      for (const [width, height] of [[1440, 700], [500, 800]] as const) {
        await page.setViewportSize({ width, height });
        for (const [tag, id, period] of [
          ['before', flat.documentId, flat.periodKey],
          ['after', grouped.documentId, grouped.periodKey],
        ] as const) {
          await page.goto(`/documents/${String(id)}?periodKey=${String(period)}`);
          await expect(page.locator('revo-grid:visible').first()).toBeVisible({ timeout: 60_000 });
          await page.waitForTimeout(2000);
          await page.screenshot({ path: test.info().outputPath(`${tag}-doc-${String(width)}-${scheme}.png`) });
        }
      }
    }

    await page.emulateMedia({ colorScheme: 'light' });
    await page.setViewportSize({ width: 1440, height: 900 });
    const base = `/admin/templates/${String(grouped.templateId)}/versions/${String(grouped.versionId)}?node=t:${String(grouped.tableId)}`;
    await page.goto(`${base}&tab=columns`);
    await page.getByTestId('manage-column-groups').click();
    await expect(page.getByTestId('column-groups-dialog')).toBeVisible({ timeout: 45_000 });
    await page.waitForTimeout(800);
    await page.screenshot({ path: test.info().outputPath('after-builder-dialog-1440.png') });
    await page.keyboard.press('Escape');
    await page.goto(`${base}&tab=preview`);
    await expect(page.locator('[data-preview-group-row]').first()).toBeVisible({ timeout: 45_000 });
    await page.screenshot({ path: test.info().outputPath('after-builder-preview-1440.png') });
  });
});
