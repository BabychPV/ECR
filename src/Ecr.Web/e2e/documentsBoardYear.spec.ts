import { mkdirSync } from 'node:fs';
import { expect, test, type Page } from '@playwright/test';

/**
 * ✎ b14 documents-board-yearly (людина 25.11, два знімки: «це не зрозумів чому»):
 * 1) Documents → рік → Board: картки — у тих самих стовпцях, що й лічильники над дошкою (одне правило року), стовпця
 *    «No state for this period» немає (доти всі документи падали туди: дошка брала стан за місяць, якого в переліку року немає);
 * 2) документ: біля «Sheet:» — чий це стан (місяць або «Year N»), а не голе «Draft».
 *
 * Справжній стенд (`tools/e2e-stand.ps1`), без підміни відповідей: числа дає сервер.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const ReportDir = process.env['ECR_E2E_REPORT_DIR'] ?? '';
const Year = PeriodKey === '' ? '' : PeriodKey.slice(0, 4);

/** Лічильник смуги → стовпець дошки. */
const CounterToColumn: Readonly<Record<string, string>> = {
  draft: 'Draft',
  partlySubmitted: 'PartlySubmitted',
  submitted: 'Submitted',
  rejected: 'Rework',
  approved: 'Approved',
  noApproval: 'NoApproval',
};

test.describe('documentsBoardYear: дошка року і стан аркуша', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('documentsBoardYear: стовпці дошки = лічильники року; «без стану» немає', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, Operator.user, Operator.password);
    await page.goto(`/?year=${Year}&view=board`);

    const board = page.locator('[data-documents-board]');
    await expect(board, 'дошка не відрендерилася').toBeVisible({ timeout: 30_000 });
    await expect(page.locator('[data-summary-strip]'), 'смуги лічильників немає').toBeVisible({ timeout: 30_000 });
    await expect(board.locator('[data-board-card]').first(), 'на дошці немає карток').toBeVisible({ timeout: 30_000 });

    // Перелік стенда вміщається в першу сторінку — тоді дошка показує всі документи року, і числа мають збігатися.
    await expect(page.locator('[data-board-column="NoState"]'), 'стовпець «No state for this period» є').toHaveCount(0);

    for (const [counter, column] of Object.entries(CounterToColumn)) {
      const tile = page.locator(`[data-summary-counter="${counter}"]`);
      if ((await tile.count()) === 0) continue;
      const shown = Number(((await tile.innerText()).match(/\d+/) ?? ['0'])[0]);
      const col = board.locator(`[data-board-column="${column}"] [data-board-count]`);
      const onBoard = (await col.count()) === 0 ? 0 : Number(await col.getAttribute('data-board-count'));
      expect(onBoard, `стовпець ${column} ≠ лічильник ${counter}`).toBe(shown);
    }

    if (ReportDir !== '') {
      mkdirSync(ReportDir, { recursive: true });
      await page.screenshot({ path: `${ReportDir}\\documentsBoardYear-board-1440.png` });
    }
  });

  test('documentsBoardYear: «Sheet:» каже, чий це стан (місяць або рік)', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await signIn(page, Operator.user, Operator.password);
    await page.goto(`/?year=${Year}`);

    const link = page.locator('[data-documents-table] [data-document-row] a').first();
    await expect(link, 'у переліку немає документа').toBeVisible({ timeout: 30_000 });
    await link.click();

    const scope = page.getByTestId('document-sheet-state-scope');
    await expect(scope, 'біля «Sheet:» немає періоду').toBeVisible({ timeout: 30_000 });
    expect((await scope.innerText()).trim(), 'підпис періоду порожній').toMatch(/^·\s*\S/);
    if (ReportDir !== '') await page.screenshot({ path: `${ReportDir}\\documentsBoardYear-sheet-1440.png` });
  });
});

async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
