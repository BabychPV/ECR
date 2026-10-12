import { expect, test, type Page, type Response } from '@playwright/test';

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
  const box = await page.locator('nav:not(.ecr-crumbs)').boundingBox();
  if (box === null) throw new Error('меню не видно');
  return box.width;
}

// ⚠ Міряємо ПЕРШУ ДИТИНУ main, а не сам `main#main-content`: AppShell зсуває вміст
// через padding-inline-start головного елемента, тож сам main завжди на x=0.
async function contentBox(page: Page): Promise<{ x: number; width: number }> {
  const box = await page.locator('main#main-content > *').first().boundingBox();
  if (box === null) throw new Error('вмісту не видно');
  return box;
}

async function contentLeft(page: Page): Promise<number> {
  return (await contentBox(page)).x;
}

const CollapseName = /Collapse menu|Свернуть меню|Мәзірді жию/;
const ExpandName = /Expand menu|Развернуть меню|Мәзірді жаю/;

/** Кеш вибору в `localStorage` (`shared/theme/navbarCollapse.ts`). */
const StorageKey = 'ecr.navbarCollapsed';

/** `PUT` збереження вибору на сервері (`usePreferenceSync` → `putPreference`). */
const SavePreference = /\/api\/v1\/me\/preferences\/navbarCollapsed$/;

/**
 * Чекає відповідь на збереження вибору меню на сервері (`PUT`). Створюється ДО дії, що його викликає.
 *
 * ⚠ Без цього `reload`/закриття контексту могли б обірвати запит, а значення сервера перемагає кеш
 * (`usePreferenceSync` → `reconcile`) — і меню «розгорталося б назад» саме в тесті «стан після F5».
 */
function saveResponse(page: Page): Promise<Response> {
  const response = page.waitForResponse(
    (r) => r.request().method() === 'PUT' && SavePreference.test(r.url()),
    { timeout: 10_000 },
  );
  // Не «unhandled rejection», якщо тест упаде раніше за `await`.
  response.catch(() => undefined);

  return response;
}

/** Натискає Enter на кнопці меню й чекає, поки вибір дійшов до сервера (порядок записів тримається). */
async function pressEnterAndWaitSaved(page: Page): Promise<void> {
  const saved = saveResponse(page);
  await page.keyboard.press('Enter');
  expect((await saved).ok(), 'вибір меню не збережено на сервері').toBe(true);
}

/**
 * Повертає меню в розгорнутий стан на десктопі й ЧЕКАЄ, поки воно стало таким.
 *
 * ⚠ Флейк CI 11.10 (`navbarCollapse.spec.ts`, «не знайдено кнопку згортання за
 * 5 с»): раніше тут було `setViewportSize` → одразу `expand.isVisible()` (без
 * очікування) → «немає кнопки розгортання — нічого клікати» →
 * `expect(collapse).toBeVisible()`. Після мобільного кроку ширина 390→1280
 * застосовується не миттєво: CSS (`visibleFrom`) показує кнопку відразу, а її
 * НАЗВУ (`iconsOnly = стан && desktop`) React ставить пізніше, за подією
 * `matchMedia`. У цьому вікні кнопка ще мобільна/зі старою назвою, `isVisible()`
 * дає `false`, клік пропускається, і «Collapse menu» не з'являється, бо меню
 * лишилось згорнутим. Тож чекаємо детермінований сигнал готовності: ім'я кнопки
 * відповідає збереженому вибору (`true` → «Expand», інакше → «Collapse»).
 */
async function restoreExpandedMenu(page: Page): Promise<void> {
  await page.setViewportSize({ width: 1280, height: 800 });

  const expand = page.getByRole('button', { name: ExpandName });
  const collapse = page.getByRole('button', { name: CollapseName });

  await expect
    .poll(
      async () => {
        const stored = await page.evaluate((key) => window.localStorage.getItem(key), StorageKey);
        return (stored === 'true' ? expand : collapse).isVisible();
      },
      { message: 'кнопка меню не відповідає збереженому вибору після повернення на десктоп', timeout: 15_000 },
    )
    .toBe(true);

  if (await expand.isVisible()) {
    // Розгортання теж пишеться на сервер: закриття сторінки обірвало б `PUT`, і наступні набори
    // (знімки екрана) побачили б вузьке меню з сервера.
    const restored = saveResponse(page);
    await expand.click();
    await restored;
  }
  await expect(collapse).toBeVisible();
}

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
    const wideMain = await contentLeft(page);
    const wideWidth = (await contentBox(page)).width;

    // ⚠ Збереження на сервері чекаємо ДО F5 (див. `saveResponse`).
    const saved = saveResponse(page);

    try {
      await collapse.click();

      const expand = page.getByRole('button', { name: ExpandName });
      await expect(expand).toHaveAttribute('aria-expanded', 'false');

      // Меню вузьке, вміст сторінки зайняв звільнену ширину.
      await expect.poll(() => navWidth(page)).toBeLessThan(80);
      await expect.poll(() => contentLeft(page)).toBeLessThan(wideMain - 150);
      await expect.poll(async () => (await contentBox(page)).width).toBeGreaterThan(wideWidth + 150);
      expect(wideNav).toBeGreaterThan(200);

      // Назва пункту — на фокусі з клавіатури, без миші.
      const firstItem = page.locator('nav:not(.ecr-crumbs) a').first();
      const name = await firstItem.getAttribute('aria-label');
      expect(name, 'згорнутий пункт без доступного імені').toBeTruthy();
      await firstItem.focus();
      // ⚠ Підказка кнопки згортання (мишу після кліку лишено над нею) теж у DOM —
      // тому шукаємо саме підказку пункту, а не «єдину».
      await expect(page.getByRole('tooltip').filter({ hasText: name ?? '' })).toHaveText(name ?? '');

      // F5: вузьке з першого кадру.
      expect((await saved).ok(), 'вибір меню не збережено на сервері до F5').toBe(true);
      await page.reload();
      await expect(page.getByRole('button', { name: ExpandName })).toBeVisible({ timeout: 30_000 });
      expect(await navWidth(page)).toBeLessThan(80);

      // Клавіатура (рев'ю 06.10, P2-1): Enter перемикає меню, а фокус лишається
      // на кнопці в обидва боки — не падає на початок сторінки (WCAG 2.4.3).
      await page.getByRole('button', { name: ExpandName }).focus();
      await pressEnterAndWaitSaved(page);
      await expect(page.getByRole('button', { name: CollapseName })).toBeFocused();
      await expect.poll(() => navWidth(page)).toBeGreaterThan(200);
      await pressEnterAndWaitSaved(page);
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
      await expect(page.locator('nav:not(.ecr-crumbs) a').first()).toContainText(/\S/);
      await page.setViewportSize({ width: 1280, height: 800 });
    } catch (error) {
      // ⚠ Спершу ширина: на мобільній кнопки згортання не видно (`visibleFrom`),
      // і без цього падіння мобільного кроку лишало б `e2e-admin` зі згорнутим
      // меню на сервері для наступних наборів (рев'ю 06.10, P3-3).
      // ⛔ Не `finally`: помилка відновлення в `finally` ПЕРЕКРИВАЛА першопричину
      // (11.10 CI показав лише «не знайдено кнопку згортання»). Тут відновлення
      // best-effort, а звітується перша помилка.
      await restoreExpandedMenu(page).catch(() => undefined);
      throw error;
    }

    // Успішний шлях: відновлення — частина тесту, і його збій має бути видно.
    await restoreExpandedMenu(page);

    await expect.poll(() => navWidth(page)).toBeGreaterThan(200);
  });
});
