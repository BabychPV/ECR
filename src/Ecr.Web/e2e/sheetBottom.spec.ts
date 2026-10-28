import { expect, test, type Page } from '@playwright/test';
import { createMultiSheetDocument, signIn, type MultiSheetDocument } from './multiSheet';

/**
 * ui-sheet-bottom на живому стенді (макет A «вкладки і скрол в одному рядку під таблицею»): нижній рядок у
 * рамці панелі, ПІД сіткою — «‹ › …», вкладки аркушів, горизонтальна прокрутка таблиці; Calculation results
 * завжди згорнуто. Документ із 8 аркушами й широкою таблицею (30 стовпців) заводиться через API в самому spec.
 * Кожен сценарій повторюється N/5 усередині спека.
 */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const Repeats = 5;
const SheetCount = 8;
const WideColumns = 30;

let doc: MultiSheetDocument | undefined;

async function open(page: Page): Promise<void> {
  await signIn(page);
  doc ??= await createMultiSheetDocument(page, SheetCount, WideColumns);
  await page.goto(`/documents/${String(doc.documentId)}?periodKey=${String(doc.periodKey)}`);
  await expect(page.locator('revo-grid:visible').first()).toBeVisible({ timeout: 60_000 });
  await expect(page.getByRole('tablist').getByRole('tab')).toHaveCount(SheetCount, { timeout: 60_000 });
  await expect(page.getByTestId('grid-hscroll')).toBeVisible();
}

/** Заголовок крайньої лівої видимої (не обрізаної ліворуч) комірки сітки — «перший видимий стовпець». */
async function firstHeader(page: Page): Promise<string> {
  const grid = await page.locator('revo-grid:visible').first().boundingBox();
  const cells = page.locator('revo-grid:visible revogr-header .rgHeaderCell');
  const count = await cells.count();
  let best = { x: Number.POSITIVE_INFINITY, text: '' };
  for (let i = 0; i < count; i += 1) {
    const box = await cells.nth(i).boundingBox();
    const text = (await cells.nth(i).innerText()).trim();
    // Комірка, що ще частково видна (права межа правіше лівого краю сітки), і непорожній заголовок.
    if (box !== null && grid !== null && box.x + box.width > grid.x + 4 && text !== '' && box.x < best.x) best = { x: box.x, text };
  }
  return best.text;
}
/** Смугу — на початок і чекаємо, поки сітка покаже перший стовпець: повертає його заголовок (опорна точка). */
async function resetToStart(page: Page): Promise<string> {
  await page.getByTestId('grid-hscroll').evaluate((el) => {
    el.scrollLeft = 0;
  });
  await expect.poll(() => firstHeader(page), { timeout: 15_000, message: 'сітка не стала на перший стовпець' }).toBe('C1');
  return 'C1';
}
test.describe('Нижній рядок під таблицею (ui-sheet-bottom)', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('макет: рядок під сіткою в рамці панелі — «‹ › …», вкладки, прокрутка (N/5)', async ({ page }) => {
    test.setTimeout(300_000);
    // Високе вікно: сторінка вміщується, тож липка позиція рядка не накладається на сітку.
    await page.setViewportSize({ width: 1280, height: 1500 });
    await open(page);

    for (let i = 0; i < Repeats; i += 1) {
      if (i > 0) await page.reload();
      await expect(page.getByTestId('grid-hscroll')).toBeVisible({ timeout: 60_000 });

      const panel = page.locator('.ecr-sheet-panel').first();
      const bar = panel.locator('.doc-sheet-bar');
      await expect(bar, 'нижній рядок у рамці панелі').toBeVisible();

      const [grid, barBox, prev, tabs, scroll] = await Promise.all([
        page.locator('revo-grid:visible').first().boundingBox(),
        bar.boundingBox(),
        page.getByTestId('sheet-prev').boundingBox(),
        page.getByRole('tablist').boundingBox(),
        page.getByTestId('grid-hscroll').boundingBox(),
      ]);
      expect(grid && barBox && prev && tabs && scroll, 'усі частини рядка мають розмір').toBeTruthy();
      if (!grid || !barBox || !prev || !tabs || !scroll) return;

      // Рядок ПІД сіткою.
      expect(barBox.y, `прогін ${String(i + 1)}/${String(Repeats)}: рядок не під сіткою`).toBeGreaterThanOrEqual(grid.y + grid.height - 2);
      // Висота ~36 px.
      expect(barBox.height).toBeGreaterThanOrEqual(34);
      expect(barBox.height).toBeLessThanOrEqual(44);
      // Один ряд: ліворуч групи → вкладки → прокрутка (зліва направо), прокрутка ≥ 160 px.
      expect(prev.x).toBeLessThan(tabs.x);
      expect(tabs.x + tabs.width).toBeLessThanOrEqual(scroll.x + 2);
      expect(scroll.width).toBeGreaterThanOrEqual(160);
      expect(Math.abs(prev.y + prev.height / 2 - (scroll.y + scroll.height / 2))).toBeLessThan(barBox.height);

      // Штатна прокрутка RevoGrid не видна, а скролбар один.
      const native = await page.locator('revo-grid:visible revogr-scroll-virtual.horizontal').first().evaluate((el) => getComputedStyle(el).visibility);
      expect(native).toBe('hidden');

      // Прокрутка ВИДНА: таблиця ширша за вікно, доріжка намальована, повзунок має ненульову ширину в межах рядка.
      // Сітка домальовує ширину вмісту після першого малювання (особливо після reload): чекаємо, а не міряємо миттю.
      await expect
        .poll(() => page.getByTestId('grid-hscroll').evaluate((el) => el.scrollWidth - el.clientWidth), {
          timeout: 15_000,
          message: `прогін ${String(i + 1)}: прокручувати нічого, хоч стовпців ${String(WideColumns)}`,
        })
        .toBeGreaterThan(0);
      const metrics = await page.getByTestId('grid-hscroll').evaluate((el) => ({ scroll: el.scrollWidth, client: el.clientWidth }));
      expect(metrics.scroll, `прогін ${String(i + 1)}: прокручувати нічого, хоч стовпців ${String(WideColumns)}`).toBeGreaterThan(metrics.client);
      const rail = await page.getByTestId('grid-hscroll-rail').boundingBox();
      const thumb = await page.getByTestId('grid-hscroll-thumb').boundingBox();
      expect(rail, 'доріжка намальована').toBeTruthy();
      expect(thumb, 'повзунок намальований').toBeTruthy();
      if (!rail || !thumb) return;
      expect(rail.height).toBeGreaterThanOrEqual(9);
      expect(thumb.width, 'повзунок має ненульову ширину').toBeGreaterThan(8);
      expect(thumb.width).toBeLessThan(rail.width);
      expect(thumb.x).toBeGreaterThanOrEqual(rail.x - 1);
      expect(thumb.x + thumb.width).toBeLessThanOrEqual(rail.x + rail.width + 1);
      expect(rail.y).toBeGreaterThanOrEqual(barBox.y);
      expect(rail.y + rail.height).toBeLessThanOrEqual(barBox.y + barBox.height + 1);
      // Колір повзунка відрізняється від доріжки (видно на знімку).
      const colours = await page.evaluate(() => {
        const r = document.querySelector('[data-testid="grid-hscroll-rail"]') as HTMLElement;
        const t = document.querySelector('[data-testid="grid-hscroll-thumb"]') as HTMLElement;
        return { rail: getComputedStyle(r).backgroundColor, thumb: getComputedStyle(t).backgroundColor };
      });
      expect(colours.thumb).not.toBe(colours.rail);

      if (i === 0) {
        await page.screenshot({ path: test.info().outputPath('after-bar-1280.png') });
        await page.screenshot({ path: test.info().outputPath('after-bar-1280-zoom.png'), clip: { x: barBox.x, y: barBox.y - 4, width: barBox.width, height: barBox.height + 8 } });
      }
    }
  });

  test('низьке вікно: липкий рядок не накриває сітку, коли сторінку докручено; повзунок тягнеться мишею (N/5)', async ({ page }) => {
    test.setTimeout(300_000);
    await page.setViewportSize({ width: 1280, height: 600 });
    await open(page);

    for (let i = 0; i < Repeats; i += 1) {
      // Докручуємо сторінку до кінця колесом над шапкою документа (не над сіткою).
      const title = page.locator('h1, h2').first();
      await title.hover();
      for (let k = 0; k < 6; k += 1) await page.mouse.wheel(0, 800);
      const bar = page.locator('.ecr-sheet-panel .doc-sheet-bar');
      await expect(bar).toBeVisible();
      await expect.poll(async () => {
        const [grid, b] = await Promise.all([page.locator('revo-grid:visible').first().boundingBox(), bar.boundingBox()]);
        return grid !== null && b !== null ? b.y - (grid.y + grid.height) : -9999;
      }, { timeout: 10_000, message: `прогін ${String(i + 1)}: рядок накриває нижній край сітки` }).toBeGreaterThanOrEqual(-1);

      // Повзунок тягнеться мишею: сітка зсувається, назад — теж.
      const start = await resetToStart(page);
      const thumb = await page.getByTestId('grid-hscroll-thumb').boundingBox();
      if (thumb === null) throw new Error('повзунка немає');
      await page.mouse.move(thumb.x + thumb.width / 2, thumb.y + thumb.height / 2);
      await page.mouse.down();
      await page.mouse.move(thumb.x + thumb.width / 2 + 600, thumb.y + thumb.height / 2, { steps: 8 });
      await page.mouse.up();
      await expect.poll(() => firstHeader(page), { timeout: 15_000, message: 'повзунок не зрушив сітку' }).not.toBe(start);
      await page.getByTestId('grid-hscroll').evaluate((el) => {
        el.scrollLeft = 0;
      });
      await expect.poll(() => firstHeader(page), { timeout: 15_000 }).toBe(start);
    }
  });

  test('смуга прокрутки рухає сітку, а коліщатко по сітці рухає смугу (N/5)', async ({ page }) => {
    test.setTimeout(300_000);
    await page.setViewportSize({ width: 1280, height: 800 });
    await open(page);

    const scroll = page.getByTestId('grid-hscroll');
    await expect(scroll).toHaveAttribute('data-scrollable', 'true', { timeout: 30_000 });
    const start = await resetToStart(page);

    for (let i = 0; i < Repeats; i += 1) {
      // Смугу — у кінець: перший видимий стовпець змінюється.
      await scroll.evaluate((el) => {
        el.scrollLeft = el.scrollWidth;
      });
      try {
        await expect.poll(() => firstHeader(page), { timeout: 15_000 }).not.toBe(start);
      } catch (error) {
        const state = await page.evaluate(() => {
          const t = document.querySelector('[data-testid="grid-hscroll"]') as HTMLElement;
          const g = document.querySelector('revo-grid revogr-scroll-virtual.horizontal') as HTMLElement;
          const grid = document.querySelector('revo-grid') as HTMLElement;
          const headers = [...document.querySelectorAll('revo-grid revogr-header .rgHeaderCell')].slice(0, 40).map((h) => {
            const b = h.getBoundingClientRect();
            return `${((h as HTMLElement).innerText ?? '').trim()}@${String(Math.round(b.x))}`;
          });
          const viewports = [...document.querySelectorAll('revo-grid revogr-viewport-scroll')].map(
            (v) => `${v.className}:${String((v.querySelector('.horizontal-wrapper, .inner-content-table') as HTMLElement | null)?.scrollLeft ?? v.scrollLeft)}`,
          );
          const scrollers = [document.scrollingElement, document.querySelector('main')].map((m) => m?.scrollLeft ?? -1);
          return JSON.stringify({
            scrollers,
            gridRect: grid.getBoundingClientRect().toJSON(),
            trackLeft: t.scrollLeft,
            trackRange: t.scrollWidth - t.clientWidth,
            gridLeft: g.scrollLeft,
            gridRange: g.scrollWidth - g.clientWidth,
            headers,
            viewports,
          });
        });
        throw new Error(`прогін ${String(i + 1)}: сітка не рушила за смугою: ${state}`, { cause: error });
      }

      // Смугу — на початок: сітка вертається.
      await scroll.evaluate((el) => {
        el.scrollLeft = 0;
      });
      await expect.poll(() => firstHeader(page), { timeout: 15_000 }).toBe(start);

      // Shift+колесо / горизонтальне колесо по сітці — смуга слідує.
      const grid = page.locator('revo-grid:visible').first();
      const box = await grid.boundingBox();
      if (box === null) return;
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.mouse.wheel(900, 0);
      await expect.poll(() => scroll.evaluate((el) => el.scrollLeft), { timeout: 15_000 }).toBeGreaterThan(0);

      await page.mouse.wheel(-5000, 0);
      await expect.poll(() => scroll.evaluate((el) => el.scrollLeft), { timeout: 15_000 }).toBe(0);
    }
  });

  test('вкладки: «›» до останнього, «‹» назад, «…» веде на аркуш (N/5)', async ({ page }) => {
    test.setTimeout(300_000);
    await page.setViewportSize({ width: 1280, height: 800 });
    await open(page);

    const prev = page.getByTestId('sheet-prev');
    const next = page.getByTestId('sheet-next');

    for (let i = 0; i < Repeats; i += 1) {
      await page.getByTestId('sheet-all').click();
      await page.getByRole('menuitem').first().click();
      await expect(prev, `прогін ${String(i + 1)}/${String(Repeats)}`).toBeDisabled();

      for (let step = 0; step < SheetCount - 1; step += 1) {
        await next.click();
        await expect(page.getByRole('tab', { selected: true })).toContainText(`Sheet ${String(step + 2)} `);
        await expect(page.getByRole('tab', { selected: true })).toBeInViewport({ ratio: 0.9 });
        // Нижній рядок лишається ПІД сіткою після зміни аркуша.
        await expect(page.getByTestId('grid-hscroll')).toBeVisible();
      }
      await expect(next).toBeDisabled();

      await page.getByTestId('sheet-all').click();
      await page.getByRole('menuitem', { name: /Sheet 1 / }).click();
      await expect(page.getByRole('tab', { selected: true })).toContainText('Sheet 1 ');
    }
  });

  test('повний екран: той самий рядок унизу, прокрутка рухає сітку (N/5)', async ({ page }) => {
    test.setTimeout(300_000);
    await page.setViewportSize({ width: 1280, height: 800 });
    await open(page);

    for (let i = 0; i < Repeats; i += 1) {
      // Після перечитування документа сторінка може перемонтуватися: клік повторюємо, доки адреса не набуде full=1.
      await expect(async () => {
        if (!/[?&]full=1/.test(page.url())) await page.getByTestId('sheet-full-toggle').click();
        await expect(page).toHaveURL(/[?&]full=1/, { timeout: 2_000 });
      }).toPass({ timeout: 20_000 });
      const bar = page.locator('.ecr-sheet-panel .doc-sheet-bar');
      await expect(bar).toBeVisible();

      const box = await bar.boundingBox();
      const viewport = page.viewportSize()?.height ?? 0;
      expect(box, 'нижній рядок має розмір').toBeTruthy();
      expect((box?.y ?? 0) + (box?.height ?? 0), `прогін ${String(i + 1)}/${String(Repeats)}: рядок не внизу вікна`).toBeGreaterThanOrEqual(viewport - 24);

      const scroll = page.getByTestId('grid-hscroll');
      const start = await resetToStart(page);
      await scroll.evaluate((el) => {
        el.scrollLeft = el.scrollWidth;
      });
      await expect.poll(() => firstHeader(page), { timeout: 15_000 }).not.toBe(start);
      await scroll.evaluate((el) => {
        el.scrollLeft = 0;
      });
      await expect.poll(() => firstHeader(page), { timeout: 15_000 }).toBe(start);
      if (i === 0) await page.screenshot({ path: test.info().outputPath('after-full-1280.png') });

      await page.getByTestId('sheet-full-toggle').click();
      await expect(page).not.toHaveURL(/[?&]full=1/);
    }
  });

  test('Calculation results завжди згорнуто за замовчуванням; вибір пам’ятається (N/5)', async ({ page }) => {
    test.setTimeout(300_000);
    await page.setViewportSize({ width: 1280, height: 800 });
    await open(page);
    await page.evaluate(() => {
      window.localStorage.removeItem('ecr.calcResults.collapsed');
    });
    await page.reload();

    const toggle = page.getByTestId('calc-results-toggle');
    await expect(toggle).toBeVisible({ timeout: 60_000 });
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await expect(page.getByTestId('calc-results-count')).toBeVisible();

    for (let i = 0; i < Repeats; i += 1) {
      await toggle.click();
      await expect(toggle).toHaveAttribute('aria-expanded', 'true');
      await toggle.click();
      await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    }

    await toggle.click();
    await page.reload();
    await expect(page.getByTestId('calc-results-toggle')).toHaveAttribute('aria-expanded', 'true', { timeout: 60_000 });
  });
});
