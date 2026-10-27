import { expect, test, type Page } from '@playwright/test';

/**
 * ui-sheet-focus на живому стенді: згортання панелі таблиць (A), таблиця на весь екран (B),
 * стрілки/список аркушів (C), згортання «Calculation results» (D).
 *
 * Кожен сценарій повторюється N/5 усередині спека (гонки фокуса й розкладки).
 * Знімки (необов'язково): `ECR_SHEETFOCUS_SHOTS_DIR` (тег — `ECR_SHEETFOCUS_SHOT_TAG`, before/after).
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const ShotsDir = process.env['ECR_SHEETFOCUS_SHOTS_DIR'] ?? '';
const ShotTag = process.env['ECR_SHEETFOCUS_SHOT_TAG'] ?? 'after';
const Repeats = 5;

test.describe('Фокус на таблиці аркуша (ui-sheet-focus)', () => {
  test.skip(
    PeriodKey === '' || DocumentId === '',
    'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.',
  );

  test('панель таблиць: «Сховати» → смужка → розгорнути, вибір переживає перезавантаження (N/5)', async ({ page }) => {
    test.slow();
    await signIn(page);
    await open(page);

    for (let i = 0; i < Repeats; i += 1) {
      const nav = page.getByTestId('table-navigator');
      if (!(await nav.isVisible())) await page.getByTestId('table-navigator-rail-btn').click();
      await expect(nav).toBeVisible();

      await page.getByTestId('table-navigator-hide').click();
      await expect(nav).toBeHidden();
      const rail = page.getByTestId('table-navigator-rail-btn');
      await expect(rail).toBeFocused();
      const box = await page.getByTestId('table-navigator-rail').boundingBox();
      expect(box?.width ?? 999, 'смужка вузька').toBeLessThanOrEqual(40);

      if (i === 0) await shot(page, 'rail');

      await rail.click();
      await expect(nav).toBeVisible();
      await expect(page.getByTestId('table-navigator-hide')).toBeFocused();
    }

    await page.getByTestId('table-navigator-hide').click();
    await page.reload();
    await expect(page.getByTestId('table-navigator-rail')).toBeVisible({ timeout: 60_000 });
    await page.getByTestId('table-navigator-rail-btn').click();
  });

  test('на весь екран: правка комірки, збереження, вихід, фокус на кнопці (N/5)', async ({ page }) => {
    test.slow();
    await signIn(page);
    await open(page);

    const toggle = page.getByTestId('sheet-full-toggle');
    await shot(page, 'normal');

    for (let i = 0; i < Repeats; i += 1) {
      // Після PATCH документ може перечитатися й перемонтувати сторінку: клік повторюємо, доки адреса не набуде full=1.
      await expect(async () => {
        if (!/[?&]full=1/.test(page.url())) await toggle.click();
        await expect(page, `прогін ${String(i + 1)}/${String(Repeats)}`).toHaveURL(/[?&]full=1/, { timeout: 2_000 });
      }).toPass({ timeout: 20_000 });
      await expect(page.getByRole('region')).toBeVisible();
      await expect(page.getByTestId('table-navigator')).toBeHidden();
      if (i === 0) await shot(page, 'full');

      // Сітка займає майже все вікно.
      const grid = page.locator('revo-grid:visible');
      const height = (await grid.boundingBox())?.height ?? 0;
      const viewport = page.viewportSize()?.height ?? 0;
      expect(height, 'сітка на всю висоту вікна').toBeGreaterThan(viewport * 0.6);

      // Правка комірки всередині.
      const cell = grid.locator('revogr-data .rgCell').first();
      await cell.dblclick();
      await page.keyboard.type(String(10 + i));
      // Автозбереження: PATCH пішов і прийнятий (індикатор «saved» мигає коротко — не опора).
      const saved = page.waitForResponse((r) => r.request().method() === 'PATCH' && r.ok(), { timeout: 30_000 });
      await page.keyboard.press('Enter');
      await saved;

      // Esc у редакторі скасовує правку, але НЕ виходить з режиму.
      // (Редактор міг лишитися відкритим після Enter, або його відкриває F2.)
      if ((await page.locator('revogr-edit').count()) === 0) {
        await cell.click();
        await page.keyboard.press('Enter');
      }
      await expect(page.locator('revogr-edit')).toHaveCount(1);
      await page.keyboard.press('Escape');
      await expect(page.locator('revogr-edit')).toHaveCount(0);
      await expect(page).toHaveURL(/[?&]full=1/);

      // Esc поза редактором виходить; фокус — на кнопці.
      await page.locator('body').press('Escape');
      await expect(page).not.toHaveURL(/[?&]full=1/);
      await expect(toggle).toBeFocused();
    }

    // Клавіша та посилання.
    await toggle.focus();
    await page.evaluate(() => {
      (window as unknown as { __keys: string[] }).__keys = [];
      document.addEventListener('keydown', (e) => {
        (window as unknown as { __keys: string[] }).__keys.push(`|c|s|`);
      });
    });
    await page.keyboard.press('Control+Shift+Enter');
    const seen = await page.evaluate(() => (window as unknown as { __keys: string[] }).__keys);
    await expect(page, `клавіші, які бачив документ: ${seen.join(' ; ')}`).toHaveURL(/[?&]full=1/);
    await page.reload();
    await expect(page.getByRole('region')).toBeVisible({ timeout: 60_000 });
    await toggle.click();
    await expect(page).not.toHaveURL(/[?&]full=1/);
  });

  test('аркуші: «›» до останнього, активна вкладка видима, «…» веде на вибраний (N/5)', async ({ page }) => {
    test.slow();
    await signIn(page);
    await open(page);

    const tabs = page.getByRole('tablist').getByRole('tab');
    const count = await tabs.count();
    test.skip(count < 2, 'на стенді один аркуш');

    for (let i = 0; i < Repeats; i += 1) {
      const next = page.getByTestId('sheet-next');
      while (await next.isEnabled()) await next.click();
      await expect(next).toBeDisabled();

      const active = page.getByRole('tab', { selected: true });
      await expect(active).toBeInViewport();
      const strip = page.getByRole('tablist');
      const [a, s] = [await active.boundingBox(), await strip.boundingBox()];
      expect((a?.x ?? 0) + (a?.width ?? 0), 'вкладка не вилазить правіше смуги').toBeLessThanOrEqual((s?.x ?? 0) + (s?.width ?? 0) + 1);
      if (i === 0) await shot(page, 'sheets');

      await page.getByTestId('sheet-all').click();
      await page.getByRole('menuitem').first().click();
      await expect(page.getByTestId('sheet-prev')).toBeDisabled();
    }
  });

  test('«Calculation results»: розкривачка, вибір переживає перезавантаження (N/5)', async ({ page }) => {
    test.slow();
    await signIn(page);
    await open(page);

    const toggle = page.getByTestId('calc-results-toggle');
    await expect(toggle).toBeVisible({ timeout: 60_000 });

    for (let i = 0; i < Repeats; i += 1) {
      const before = await toggle.getAttribute('aria-expanded');
      await toggle.click();
      await expect(toggle).toHaveAttribute('aria-expanded', before === 'true' ? 'false' : 'true');
    }

    const chosen = await toggle.getAttribute('aria-expanded');
    await page.reload();
    await expect(page.getByTestId('calc-results-toggle')).toHaveAttribute('aria-expanded', chosen ?? 'false', {
      timeout: 60_000,
    });
    await shot(page, 'calc');
  });
});

async function open(page: Page): Promise<void> {
  await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
  await expect(page.locator('revo-grid:visible').first()).toBeVisible({ timeout: 60_000 });
  await expect(page.getByTestId('sheet-full-toggle')).toBeVisible({ timeout: 60_000 });
}

async function shot(page: Page, name: string): Promise<void> {
  const size = page.viewportSize()?.width ?? 0;
  const file = `${ShotTag}-${name}-${String(size)}.png`;
  // Без каталогу знімків — у вкладення прогону (test-results, потрапляє в артефакт CI).
  await page.screenshot({ path: ShotsDir === '' ? test.info().outputPath(file) : `${ShotsDir}\\${file}` });
}

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({
    timeout: 30_000,
  });
  await page.getByLabel(/User name|Ім'я/i).fill(Operator.user);
  // ⚠ Роль, а не підпис: підпис «Password» має й кнопка-тумблер видимості (`LoginPage.tsx`).
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Operator.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
