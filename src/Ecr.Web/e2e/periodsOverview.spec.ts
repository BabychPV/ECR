import { expect, test, type Page } from '@playwright/test';

/**
 * UI-33 → ✎ Projects (макет 08.11, рядки f3/f4): огляд усіх проєктів — перелік `/projects`; клац по проєкту
 * відкриває вкладку Periods з плитками року, плитка з клавіатури виділяє рядок календаря.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('Projects: перелік і плитки року на вкладці Periods (UI-33, f3/f4)', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('перелік видно одразу; проєкт відкривається на Periods; плитка з клавіатури виділяє рядок календаря', async ({ page }) => {
    await signIn(page, Operator.user, Operator.password);
    await page.goto('/projects');

    // Not a bare `.first()`: other specs create `E2E <name> project` fixtures on the shared stand.
    const link = page
      .locator('[data-project-open]')
      .filter({ hasNotText: /E2E[ -]?(groups|bottom)|E2E(Groups|Bottom)Prj|E2EPass2|E2EPD/i })
      .first();
    await expect(link, 'немає переліку проєктів').toBeVisible({ timeout: 30_000 });
    await link.click();
    await expect(page).toHaveURL(/\/projects\/\d+\/periods$/);

    const year = page.getByTestId('periods-year');
    await expect(year, 'вкладка Periods не показала плиток року').toBeVisible({ timeout: 30_000 });

    const tiles = year.locator('[data-period-tile]');
    expect(await tiles.count(), 'жодної плитки періоду').toBeGreaterThan(0);

    // ⛔ Рішення 1: плитки не несуть дій «Open …»/«Close now».
    await expect(year.getByRole('button', { name: /^(Open|Close now)/ })).toHaveCount(0);

    const tile = tiles.first();
    const key = await tile.getAttribute('data-period-tile');
    await tile.focus();
    await page.keyboard.press('Enter');

    await expect(tile).toHaveAttribute('aria-pressed', 'true');
    await expect(page.locator(`[data-period-row="${key ?? ''}"]`)).toHaveAttribute('data-selected', 'true');
  });
});
async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });

  await page.getByLabel(/User name|Ім'я/i).fill(user);
  // ⚠ Роль, а не підпис: `getByLabel(/Password|Пароль/i)` резолвиться і в кнопку-тумблер
  // видимості пароля (`LoginPage.tsx`, Q-260).
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
