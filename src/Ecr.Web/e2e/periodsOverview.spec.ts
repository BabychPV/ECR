import { expect, test, type Page } from '@playwright/test';

/**
 * UI-33: Periods за макетом (`docs/design/hybrid/screens-ops.js`, `/admin/periods`) — огляд
 * «All projects · current period» і плитки періодів року обраного проєкту.
 *
 * ⚠ Стенд може мати один проєкт (тоді він обирається сам, `U-10`) або кілька (тоді спершу
 * видно лише огляд). Тест проходить обидві гілки: через огляд обирає перший проєкт, якщо
 * проєкт ще не обрано.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';

test.describe('Periods: огляд проєктів і плитки року (UI-33)', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('огляд видно без вибору; плитка з клавіатури виділяє рядок календаря', async ({ page }) => {
    await signIn(page, Operator.user, Operator.password);
    await page.goto('/admin/periods');

    const overview = page.getByTestId('periods-overview');
    await expect(overview, 'немає огляду «All projects»').toBeVisible({ timeout: 30_000 });
    await expect(overview.getByRole('table')).toBeVisible({ timeout: 30_000 });

    const year = page.getByTestId('periods-year');
    if (!(await year.isVisible())) {
      await overview.getByRole('table').getByRole('button').first().click();
    }
    await expect(year, 'вибір проєкту не показав плиток року').toBeVisible({ timeout: 30_000 });

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
