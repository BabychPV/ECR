import { expect, test, type Page } from '@playwright/test';

/**
 * Бічне меню згортається до іконок (запит людини 06.10) — на живому стенді.
 *
 * Що доводиться тут, а не у vitest: справжня ширина меню й зсув вмісту
 * (jsdom не верстає), збереження після F5 і на ІНШОМУ «пристрої» (новий
 * контекст браузера без `localStorage` бачить вибір із сервера), підказка
 * на фокусі з клавіатури.
 *
 * ⚠ Налаштування `navbarCollapsed` серверне й переживає тест: наприкінці меню
 * розгортається назад (`finally`), інакше наступні набори (знімки екрана)
 * побачили б вузьке меню.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test -g "згортання"`.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });
  await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Operator.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

async function navWidth(page: Page): Promise<number> {
  const box = await page.locator('nav').first().boundingBox();
  if (box === null) throw new Error('меню не видно');
  return box.width;
}

async function mainLeft(page: Page): Promise<number> {
  const box = await page.locator('main#main-content').boundingBox();
  if (box === null) throw new Error('вмісту не видно');
  return box.x;
}

const CollapseName = /Collapse menu|Свернуть меню|Мәзірді жию/;
const ExpandName = /Expand menu|Развернуть меню|Мәзірді жаю/;

test.describe('Бічне меню: згортання до іконок', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  test('згортається, тримає стан після F5 і на іншому пристрої, розгортається', async ({ page, browser }) => {
    test.setTimeout(120_000);
    await page.setViewportSize({ width: 1280, height: 800 });
    await signIn(page);

    const collapse = page.getByRole('button', { name: CollapseName });
    await expect(collapse).toBeVisible({ timeout: 30_000 });
    await expect(collapse).toHaveAttribute('aria-expanded', 'true');

    const wideNav = await navWidth(page);
    const wideMain = await mainLeft(page);

    try {
      await collapse.click();

      const expand = page.getByRole('button', { name: ExpandName });
      await expect(expand).toHaveAttribute('aria-expanded', 'false');

      // Меню вузьке, вміст сторінки зайняв звільнену ширину.
      await expect.poll(() => navWidth(page)).toBeLessThan(80);
      await expect.poll(() => mainLeft(page)).toBeLessThan(wideMain - 150);
      expect(wideNav).toBeGreaterThan(200);

      // Назва пункту — на фокусі з клавіатури, без миші.
      const firstItem = page.locator('nav a').first();
      const name = await firstItem.getAttribute('aria-label');
      expect(name, 'згорнутий пункт без доступного імені').toBeTruthy();
      await firstItem.focus();
      await expect(page.getByRole('tooltip')).toHaveText(name ?? '');

      // F5: вузьке з першого кадру.
      await page.reload();
      await expect(page.getByRole('button', { name: ExpandName })).toBeVisible({ timeout: 30_000 });
      expect(await navWidth(page)).toBeLessThan(80);

      // Клавіатура (рев'ю 06.10, P2-1): Enter перемикає меню, а фокус лишається
      // на кнопці в обидва боки — не падає на початок сторінки (WCAG 2.4.3).
      await page.getByRole('button', { name: ExpandName }).focus();
      await page.keyboard.press('Enter');
      await expect(page.getByRole('button', { name: CollapseName })).toBeFocused();
      await expect.poll(() => navWidth(page)).toBeGreaterThan(200);
      await page.keyboard.press('Enter');
      await expect(page.getByRole('button', { name: ExpandName })).toBeFocused();
      await expect.poll(() => navWidth(page)).toBeLessThan(80);

      // Інший пристрій: свіжий контекст без localStorage бере вибір із сервера.
      const other = await browser.newContext({ viewport: { width: 1280, height: 800 } });
      try {
        const otherPage = await other.newPage();
        await signIn(otherPage);
        await expect(otherPage.getByRole('button', { name: ExpandName })).toBeVisible({ timeout: 30_000 });
      } finally {
        await other.close();
      }

      // Мобільна ширина: шухляда за бургером, «лише іконки» не діє.
      await page.setViewportSize({ width: 390, height: 800 });
      await page.getByRole('button', { name: /Menu|Меню|Мәзір/ }).first().click();
      await expect(page.locator('nav a').first()).toContainText(/\S/);
      await page.setViewportSize({ width: 1280, height: 800 });
    } finally {
      // ⚠ Спершу ширина: на мобільній кнопки згортання не видно (`visibleFrom`),
      // і без цього падіння мобільного кроку лишало б `e2e-admin` зі згорнутим
      // меню на сервері для наступних наборів (рев'ю 06.10, P3-3).
      await page.setViewportSize({ width: 1280, height: 800 });
      const expand = page.getByRole('button', { name: ExpandName });
      if (await expand.isVisible().catch(() => false)) await expand.click();
      await expect(page.getByRole('button', { name: CollapseName })).toBeVisible();
    }

    await expect.poll(() => navWidth(page)).toBeGreaterThan(200);
  });
});
