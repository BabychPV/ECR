import { expect, test, type Page } from '@playwright/test';

/**
 * L4: колір комірки «як в Excel» у СПРАВЖНЬОМУ `DocumentGrid`: виділення → «Заливка» /
 * «Колір тексту» → палітра → комірка пофарбована; «Без заливки» скидає.
 *
 * ⚠ Стенд не потрібен (сервер у браузері, `DocumentGridGateStandPage`):
 * `ECR_E2E_OPTIONAL=1 npx playwright test e2e/cellColor.spec.ts`.
 */
const DocStand = '/_key-commit-gate/document';

const cellAt = (page: Page, col: number, row: number) =>
  page.locator(`revo-grid revogr-data[type="rgRow"] .rgCell[data-rgcol="${String(col)}"][data-rgrow="${String(row)}"]`);

/**
 * Відкрита палітра. Mantine `Popover` лишає закритий дропдаун у DOM на час переходу, тож
 * `page.getByRole('button', { name: '#c00000' })` бачив ДВА зразки (закриваюча «Заливка» і
 * відкрита «Колір тексту») → strict-mode violation. Скоуп — лише видимий дропдаун.
 */
const dropdown = (page: Page) => page.locator('.ecr-cell-color-dropdown:visible');

async function open(page: Page): Promise<void> {
  await page.goto(DocStand);
  await expect(cellAt(page, 1, 0)).toBeVisible({ timeout: 30_000 });
  await cellAt(page, 1, 0).click();
  await expect(page.getByTestId('cell-color-fill')).not.toHaveAttribute('aria-disabled', 'true');
}

for (const scheme of ['light', 'dark'] as const) {
  test(`заливка і колір тексту виділеної комірки, скидання — тема ${scheme}`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme });
    await open(page);

    await page.getByTestId('cell-color-fill').click();
    await dropdown(page).getByRole('button', { name: '#ffff00', exact: true }).click();
    await expect(dropdown(page)).toHaveCount(0);
    await expect(cellAt(page, 1, 0)).toHaveAttribute('data-user-style', 'true');
    await expect(cellAt(page, 1, 0)).toHaveCSS('background-color', 'rgb(255, 255, 0)');
    // Текст на жовтій заливці — темний у обох темах (≥ 4.5:1).
    const text = await cellAt(page, 1, 0).evaluate((node) => getComputedStyle(node).color);
    const [r, g, b] = (text.match(/\d+/g) ?? ['255', '255', '255']).map(Number);
    expect(((r ?? 255) * 299 + (g ?? 255) * 587 + (b ?? 255) * 114) / 1000).toBeLessThan(110);

    await page.getByTestId('cell-color-text').click();
    await dropdown(page).getByRole('button', { name: '#c00000', exact: true }).click();
    await expect(dropdown(page)).toHaveCount(0);
    await expect(cellAt(page, 1, 0)).toHaveAttribute('data-user-style', 'true');

    await page.getByTestId('cell-color-fill').click();
    await page.getByTestId('cell-color-fill-reset').click();
    await expect(dropdown(page)).toHaveCount(0);
    await expect(cellAt(page, 1, 0)).not.toHaveCSS('background-color', 'rgb(255, 255, 0)');
  });
}

test('без виділення кнопки кольору вимкнені', async ({ page }) => {
  await page.goto(DocStand);
  await expect(cellAt(page, 1, 0)).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId('cell-color-fill')).toBeDisabled();
});
