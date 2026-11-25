import { expect, test, type Page } from '@playwright/test';
import { axeViolations } from './axeRule';

/**
 * Відмова лінивого чанка після оновлення — не розробницький екран React Router (✎ 25.11, людина: «виправь це»).
 *
 * Live 25.11: вкладка, відкрита до вікна, після переходу показала «Unexpected Application Error! Failed to fetch
 * dynamically imported module: …/assets/IdleSignOut-….js … Hey developer 👋». Тут відсутній файл імітується
 * `page.route(...).abort()` — браузер дає ту саму відмову `import()`, що й зниклий після оновлення чанк.
 *
 * Що доводиться тут, а не у vitest: справжнє перезавантаження сторінки (рівно одне — без петлі), справжня відмова
 * `import()` у Chromium, контраст екрана в обох темах (axe у браузері).
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test chunkReload`.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };

const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';

/** Модуль годинника бездіяльності — dev-сервер (`/src/shared/session/IdleSignOut.tsx`) і збірка (`IdleSignOut-<хеш>.js`). */
const IdleChunk = /\/(src\/shared\/session\/IdleSignOut\.tsx|assets\/IdleSignOut-[\w-]+\.js)(\?.*)?$/;

/** Сторінка «Мої групи» — лінивий чанк сторінки (`routePrefetch.ts`). */
const MyGroupsChunk = /\/(src\/pages\/MyGroupsPage\.tsx|assets\/MyGroupsPage-[\w-]+\.js)(\?.*)?$/;

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });
  await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Operator.password);
  await page.keyboard.press('Enter');
}

/** Лічильник повних завантажень документа (перезавантаження — теж). */
function countLoads(page: Page): { readonly value: number } {
  const counter = { value: 0 };
  page.on('load', () => {
    counter.value += 1;
  });
  return counter;
}

async function expectNoDeveloperScreen(page: Page): Promise<void> {
  const body = (await page.locator('body').textContent()) ?? '';
  expect(body, 'розробницький екран React Router').not.toContain('Unexpected Application Error');
  expect(body, 'розробницький екран React Router').not.toContain('Hey developer');
}

test.describe('Відмова чанка після оновлення: одне перезавантаження, далі — зрозумілий екран', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  for (const scheme of ['light', 'dark'] as const) {
    test(`шматок каркаса (IdleSignOut) зник · ${scheme}`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: scheme });
      await page.setViewportSize({ width: 1280, height: 800 });

      let blocked = 0;
      await page.route(IdleChunk, async (route) => {
        blocked += 1;
        await route.abort('failed');
      });

      const loads = countLoads(page);
      await signIn(page);

      // Після входу каркас бере свій лінивий шматок → відмова → ОДНЕ автоматичне перезавантаження → та сама
      // відмова → межа кореня показує екран «нова версія», а не розробницький.
      const panel = page.getByTestId('root-error-page');
      await expect(panel).toBeVisible({ timeout: 45_000 });
      await expect(panel.getByRole('alert')).toContainText('ECR-WEB-CHUNK-STALE');
      await expect(panel.getByRole('button', { name: /reload page|перезагрузить|қайта жүктеу/i })).toBeVisible();
      await expectNoDeveloperScreen(page);

      // goto('/login') = 1 завантаження, автоматичне перезавантаження = 2-ге; третього (петлі) немає.
      await page.waitForTimeout(1_500);
      expect(loads.value, 'рівно одне автоматичне перезавантаження').toBe(2);
      expect(blocked, 'чанк просили двічі: до і після перезавантаження').toBeGreaterThanOrEqual(2);

      expect(await axeViolations(page, 'color-contrast')).toEqual([]);
      await page.screenshot({ path: `test-results/chunk-reload-root-${scheme}.png` });

      // Файл знову є (нова збірка) → кнопка повертає робочий застосунок.
      await page.unroute(IdleChunk);
      await panel.getByRole('button', { name: /reload page|перезагрузить|қайта жүктеу/i }).click();
      await expect(page.getByRole('navigation').first()).toBeVisible({ timeout: 30_000 });
      await expect(page.getByTestId('root-error-page')).toHaveCount(0);
    });
  }

  test('сторінка (MyGroupsPage) зникла: каркас живий, одне перезавантаження, екран у вмісті', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await signIn(page);
    await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
    await expect(page.getByRole('navigation').first()).toBeVisible({ timeout: 30_000 });

    await page.route(MyGroupsChunk, (route) => route.abort('failed'));
    const loads = countLoads(page);

    await page.goto('/my-groups');

    const screen = page.getByTestId('render-error-screen');
    await expect(screen).toBeVisible({ timeout: 45_000 });
    await expect(screen.getByRole('alert')).toContainText('ECR-WEB-CHUNK-STALE');
    await expectNoDeveloperScreen(page);
    // ⚠ Межа сторінки — під каркасом: навігація лишається.
    await expect(page.getByRole('navigation').first()).toBeVisible();

    await page.waitForTimeout(1_500);
    expect(loads.value, 'goto + одне автоматичне перезавантаження').toBe(2);
    await page.screenshot({ path: 'test-results/chunk-reload-page-light.png' });
  });
});
