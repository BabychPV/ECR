import { expect, test, type Page } from '@playwright/test';

/**
 * Заголовок сторінок помилок `/403` і `/404`: без рамки й в один рядок.
 *
 * ✎ 2026-10-06, вимога людини: «треба прибрати рамку, у текст в одну строку».
 * Було: тихе кільце 1px навколо програмно сфокусованого заголовка читалося як
 * прямокутна рамка, а 26px заголовок у контейнері 420px ламався на два рядки.
 * Макет — `docs/design/hybrid` (`h1:focus{outline:none}`, `.work-state` 560px,
 * `.work-state-h` 15px/600).
 *
 * Що доводиться тут, а не у vitest: обчислений `outline` після програмного
 * фокуса (Chromium малює `:focus-visible` і для нього, `X-37`) і справжня
 * кількість рядків — jsdom не верстає. Фокус на заголовку лишається (читалка
 * оголошує екран), а Tab звідти веде до посилання з помітним кільцем.
 *
 * Запуск (стенд — `tools/e2e-stand.ps1`): `npx playwright test -g "сторінки помилок"`.
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

interface HeadingState {
  focused: boolean;
  outline: string;
  lines: number;
}

async function headingState(page: Page): Promise<HeadingState> {
  const heading = page.getByRole('main').getByRole('alert').getByRole('heading');
  await expect(heading).toBeVisible({ timeout: 30_000 });

  return heading.evaluate((el) => {
    const cs = getComputedStyle(el);
    const lineHeight = parseFloat(cs.lineHeight) || parseFloat(cs.fontSize) * 1.3;
    return {
      focused: document.activeElement === el,
      outline: cs.outlineStyle,
      lines: Math.round(el.getBoundingClientRect().height / lineHeight),
    };
  });
}

test.describe('Заголовок сторінки помилок: без рамки, в один рядок', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає. Стенд: tools/e2e-stand.ps1.');

  for (const scheme of ['light', 'dark'] as const) {
    test(`сторінки помилок /403 і /404 · ${scheme}`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: scheme });
      await page.setViewportSize({ width: 1280, height: 800 });
      await signIn(page);

      for (const path of ['/403', '/no-such-page-e2e']) {
        await page.goto(path);
        const state = await headingState(page);

        expect(state.focused, `${path}: фокус має бути на заголовку (читалка оголошує екран)`).toBe(true);
        expect(state.outline, `${path}: навколо заголовка рамка`).toBe('none');
        expect(state.lines, `${path}: заголовок переноситься`).toBe(1);

        // Tab із заголовка — до посилання «Documents», і там кільце є.
        await page.keyboard.press('Tab');
        const next = await page.evaluate(() => {
          const a = document.activeElement;
          return a === null ? null : { tag: a.tagName, outline: getComputedStyle(a).outlineStyle };
        });
        expect(next, `${path}: Tab із заголовка`).toEqual({ tag: 'A', outline: 'solid' });
      }
    });
  }
});
