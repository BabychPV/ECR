import { expect, test, type Page } from '@playwright/test';

/**
 * Шапка колонок: світла заливка автора в Dark мапиться на `--ecr-sunken` (макет D15-01),
 * у Light лишається заливкою автора; насичена `#FFC000` і темна `#1F3864` не змінюються.
 *
 * Стенд не потрібен: `ECR_E2E_OPTIONAL=1 npx playwright test e2e/headerFillDark.spec.ts`.
 * Кольори P1-V1-0003 (шапка C6D9F1) і P1-V1-0010 (9a, CCFFCC) — з Land-шаблонів.
 */
const DocStand = '/_key-commit-gate/document';

const header = (page: Page) => page.locator('revo-grid revogr-header .rgHeaderCell.ecr-header-styled').first();

const luminanceOf = (css: string): number => {
  const [r = 0, g = 0, b = 0] = (css.match(/\d+/g) ?? []).map(Number).map((v) => v / 255);
  const lin = (c: number) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);

  return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
};

async function backgroundOf(page: Page, scheme: 'light' | 'dark', fill: string): Promise<string> {
  await page.emulateMedia({ colorScheme: scheme });
  await page.goto(`${DocStand}?headerFill=${fill}`);
  await expect(header(page)).toBeVisible({ timeout: 30_000 });

  return header(page).evaluate((node) => getComputedStyle(node).backgroundColor);
}

for (const [name, fill] of [['P1-V1-0003', 'c6d9f1'], ['P1-V1-0010 (9a)', 'ccffcc'], ['сіра', 'd9d9d9']] as const) {
  test(`Dark: світла заливка шапки ${name} — фон не світлий`, async ({ page }) => {
    expect(luminanceOf(await backgroundOf(page, 'dark', fill))).toBeLessThan(0.2);
  });

  test(`Light: заливка шапки ${name} лишається авторською`, async ({ page }) => {
    const [r, g, b] = fill.match(/../g)!.map((h) => parseInt(h, 16));
    expect(await backgroundOf(page, 'light', fill)).toBe(`rgb(${String(r)}, ${String(g)}, ${String(b)})`);
  });
}

for (const fill of ['ffc000', '1f3864']) {
  test(`Dark: заливка шапки #${fill} не змінюється`, async ({ page }) => {
    const [r, g, b] = fill.match(/../g)!.map((h) => parseInt(h, 16));
    expect(await backgroundOf(page, 'dark', fill)).toBe(`rgb(${String(r)}, ${String(g)}, ${String(b)})`);
  });
}
