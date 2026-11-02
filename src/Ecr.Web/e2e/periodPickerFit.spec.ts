import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Period picker fit — РЕАЛЬНИЙ стенд, сторінка документа: випадайка вибору періоду («Reporting period · 2026»,
 * сітка місяців) лежить ПОВНІСТЮ у вікні — і у вузькому (500 px, де живий стенд обрізав третій стовпець
 * березень/червень/вересень/грудень), і в широкому (1440 px); усі 12 місяців видимі в межах вікна; axe
 * (wcag2a/aa) по контролу з відкритою випадайкою — у світлій і темній темі.
 *
 * Тема — вподобання користувача на сервері, тож `afterAll` повертає адміністратору світлу.
 * Запуск: `tools/e2e-stand.ps1 … -Grep "Period picker fit"`; CI — workflow `e2e-stand` з `grep`.
 */
const Admin = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const DocumentId = process.env['ECR_E2E_DOCUMENT'] ?? '';
const ShotsDir = process.env['ECR_E2E_SHOTS'] ?? '';
const Widths = [500, 1440] as const;
const Schemes = ['light', 'dark'] as const;

async function signIn(page: Page): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(Admin.user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(Admin.password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}

/** Тема на `<html>`: сервер зберігає вподобання і перекриває localStorage, тож перемикаємо через меню користувача. */
async function ensureScheme(page: Page, scheme: 'light' | 'dark'): Promise<void> {
  const html = page.locator('html');
  const menu = page.getByRole('button', { name: new RegExp(Admin.user, 'i') }).first();
  await expect(menu).toBeVisible({ timeout: 30_000 });
  if ((await html.getAttribute('data-mantine-color-scheme')) !== scheme) {
    await menu.click();
    await page.getByText(scheme === 'dark' ? 'Dark' : 'Light', { exact: true }).first().click();
    await page.keyboard.press('Escape');
  }
  await expect(html, `тема ${scheme} не застосувалась`).toHaveAttribute('data-mantine-color-scheme', scheme, { timeout: 15_000 });
}

async function openAs(browser: Browser, scheme: 'light' | 'dark', width: number): Promise<{ page: Page; context: BrowserContext }> {
  // Тема перемикається в меню користувача — на широкому вікні (у вузькому меню може згортатись).
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await context.newPage();
  await page.emulateMedia({ colorScheme: scheme });
  await signIn(page);
  await ensureScheme(page, scheme);
  await page.setViewportSize({ width, height: 900 });

  return { page, context };
}

async function axeOnPicker(page: Page): Promise<string[]> {
  await page.addScriptTag({ path: 'node_modules/axe-core/axe.min.js' });

  return page.evaluate(async () => {
    const axe = (window as unknown as {
      axe: { run: (c: object, o: object) => Promise<{ violations: { id: string; nodes: { target: unknown[] }[] }[] }> };
    }).axe;
    const result = await axe.run(
      { include: [['.ecr-period-anchor:has([data-testid="period-grid"])']] },
      { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa'] } },
    );

    return result.violations.flatMap((v) => v.nodes.map((n) => `${v.id}: ${String(n.target.join(' '))}`));
  });
}

test.describe('Period picker fit', () => {
  test.describe.configure({ mode: 'serial', timeout: 180_000 });

  test.beforeAll(() => {
    if (PeriodKey === '' || DocumentId === '') {
      throw new Error('стенда немає: в оточенні немає ECR_E2E_PERIOD/ECR_E2E_DOCUMENT (tools/e2e-stand.ps1)');
    }
  });

  test.afterAll(async ({ browser }) => {
    // Вподобання теми — дані стенда: повернути адміністратору світлу.
    const { context } = await openAs(browser, 'light', 1440);
    await context.close();
  });

  for (const scheme of Schemes) {
    for (const width of Widths) {
      test(`Period picker fit: document page ${String(width)} px, ${scheme} — popover inside viewport, 12 months visible, axe clean`, async ({ browser }) => {
        const { page, context } = await openAs(browser, scheme, width);
        try {
          await page.goto(`/documents/${DocumentId}?periodKey=${PeriodKey}`);
          await expect(page.locator('html')).toHaveAttribute('data-mantine-color-scheme', scheme);
          const choose = page.getByRole('button', { name: 'Choose a period' }).first();
          await expect(choose).toBeVisible({ timeout: 60_000 });
          await choose.click();

          const grid = page.getByTestId('period-grid');
          await expect(grid).toBeVisible({ timeout: 15_000 });
          await expect(grid.locator('button[data-period-key]')).toHaveCount(12);
          // Розкладка стабілізувалась (позиціонування — у layout-ефекті, шрифти/лінивий чанк).
          await page.waitForTimeout(300);

          const viewport = await page.evaluate(() => ({ w: document.documentElement.clientWidth, h: window.innerHeight }));
          const box = await grid.boundingBox();
          expect(box, 'у випадайки немає розмірів').not.toBeNull();
          if (box !== null) {
            const where = `x=${String(box.x)} w=${String(box.width)} vw=${String(viewport.w)}`;
            expect(box.x, `лівий край за вікном: ${where}`).toBeGreaterThanOrEqual(0);
            expect(box.x + box.width, `правий край за вікном: ${where}`).toBeLessThanOrEqual(viewport.w);
            expect(box.y, 'верх за вікном').toBeGreaterThanOrEqual(0);
            expect(box.y + box.height, 'низ за вікном').toBeLessThanOrEqual(viewport.h);
          }

          const months = grid.locator('button[data-period-key]');
          for (let index = 0; index < 12; index += 1) {
            const month = months.nth(index);
            await expect(month).toBeInViewport({ ratio: 1 });
            const m = await month.boundingBox();
            expect(m !== null && m.x >= 0 && m.x + m.width <= viewport.w, `місяць ${String(index + 1)} обрізаний`).toBe(true);
            // Назва місяця не обрізана всередині кнопки.
            const clipped = await month.evaluate((b) => b.scrollWidth > b.clientWidth + 1);
            expect(clipped, `назва місяця ${String(index + 1)} обрізана`).toBe(false);
          }

          if (ShotsDir !== '') {
            mkdirSync(ShotsDir, { recursive: true });
            await page.screenshot({ path: join(ShotsDir, `period-picker-${String(width)}-${scheme}.png`) });
          }

          expect(await axeOnPicker(page), `axe wcag2a/aa ${scheme} ${String(width)}`).toEqual([]);
        } finally {
          await context.close();
        }
      });
    }
  }
});
