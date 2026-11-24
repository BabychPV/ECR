import { expect, test, type Page } from '@playwright/test';

/**
 * ✎ 24.11 (аудит «Consistency issues»): після прогону перевірки екран називає КОЖНЕ правило проходу з кількістю
 * знахідок, клік по правилу фільтрує журнал, порожній журнал каже, скільки перевірок виконано.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

/** Скільки правил пише прохід (`ConsistencyCheckJob.KnownRules`). */
const KnownRules = 14;

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

test.describe('Consistency issues: перевірки останнього прогону', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('прогін на вимогу → блок правил, фільтр за правилом, посилання знахідок', async ({ page }) => {
    test.setTimeout(240_000);
    await signIn(page, Admin.user, Admin.password);
    await page.goto('/admin/consistency');

    await page.getByRole('button', { name: /Run check now/i }).first().click();
    await page.getByRole('dialog').getByRole('textbox').fill('e2e: consistency checks block');
    await page.getByRole('dialog').getByRole('button', { name: /Run check now/i }).click();
    await expect(page.locator('[role="status"][data-outcome="succeeded"]'), 'прогін не завершився успіхом').toBeVisible({
      timeout: 180_000,
    });

    const block = page.locator('[data-checks-performed]');
    await expect(block, 'немає блоку «перевірки останнього прогону»').toBeVisible({ timeout: 30_000 });
    await expect(block.getByRole('listitem')).toHaveCount(KnownRules);
    await expect(block.locator('[data-rule="CALC_RESULT_VERSION_NOT_EFFECTIVE"]')).toBeVisible();

    // Клік по правилу — фільтр журналу за ним (у адресі й у кнопці).
    await block.locator('[data-rule="EFFECTIVE_VERSION_FORMULAS"]').getByRole('button').click();
    await expect(page).toHaveURL(/ruleCode=EFFECTIVE_VERSION_FORMULAS/);
    await expect(block.locator('[data-rule="EFFECTIVE_VERSION_FORMULAS"]').getByRole('button')).toHaveAttribute(
      'aria-pressed',
      'true',
    );

    // Повторний клік знімає фільтр; знахідка з документом (якщо є) веде на документ за період.
    await block.locator('[data-rule="EFFECTIVE_VERSION_FORMULAS"]').getByRole('button').click();
    await expect(page).not.toHaveURL(/ruleCode=/);
    const links = page.locator('[data-finding-links] a[href^="/documents/"]');
    if ((await links.count()) > 0) {
      await expect(links.first()).toHaveAttribute('href', /^\/documents\/\d+\?periodKey=\d{6}$/);
    }
  });
});

async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user);
  // ⚠ Роль, а не підпис: `getByLabel(/Password|Пароль/i)` резолвиться і в кнопку-тумблер видимості пароля (Q-260).
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
