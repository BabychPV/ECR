import { mkdirSync } from 'node:fs';
import { expect, test, type Page } from '@playwright/test';

/**
 * Дошка документів: картка з 19+ аркушами не виходить за межі колонки, а сегменти смужки
 * мають заливку за станом (дефект людини 21.10: ряд порожніх пунктирних квадратів і підпис
 * «0 of 19 approved» за правою межею картки).
 *
 * ⚠ Відповідь переліку підміняється в браузері (19 аркушів у кожному документі): стенд
 * має один-два аркуші, а дефект проявляється лише на широкій смужці. Верстку міряє справжній Chromium.
 */
const Operator = { user: 'e2e-admin', password: 'E2E-Adm1n-Work-2026!' };
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const ReportDir = process.env['ECR_E2E_REPORT_DIR'] ?? '';
const Sheets = 19;

test.describe('Дошка документів: картка з багатьма аркушами', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  // `ECR_E2E_REPEAT=5` — повтор N/5 для опису коміту (e2e-stand.ps1 не має `--repeat-each`).
  const repeats = Math.max(1, Number(process.env['ECR_E2E_REPEAT'] ?? '1') || 1);
  const runs = Array.from({ length: repeats }, (_, i) => i + 1).flatMap((run) => [1440, 500].map((width) => ({ run, width })));

  for (const { run, width } of runs) {
    test(`ширина ${String(width)}: підпис і сегменти в межах картки, сегменти залиті${repeats > 1 ? ` (прогін ${String(run)})` : ''}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await padSheets(page);
      await signIn(page, Operator.user, Operator.password);
      await page.goto(`/?periodKey=${PeriodKey}&view=board`);

      const bar = page.locator('[data-board-card] [data-segment-bar]').first();
      await expect(bar, 'на дошці немає смужки аркушів').toBeVisible({ timeout: 30_000 });
      await expect(bar.locator('[data-segment]')).toHaveCount(Sheets);

      const card = bar.locator('xpath=ancestor::*[@data-board-card][1]');
      if (ReportDir !== '') {
        mkdirSync(ReportDir, { recursive: true });
        await card.screenshot({ path: `${ReportDir}\\${process.env['ECR_E2E_SHOT_TAG'] ?? 'shot'}-${String(width)}.png` });
      }

      const cardBox = await card.boundingBox();
      expect(cardBox, 'картку не виміряно').not.toBeNull();
      const inside = (box: { x: number; y: number; width: number; height: number } | null, what: string): void => {
        expect(box, `${what}: не виміряно`).not.toBeNull();
        if (box === null || cardBox === null) return;
        expect(box.x, `${what}: виходить за ліву межу картки`).toBeGreaterThanOrEqual(cardBox.x - 1);
        expect(box.x + box.width, `${what}: виходить за праву межу картки`).toBeLessThanOrEqual(cardBox.x + cardBox.width + 1);
      };

      inside(await bar.locator('[data-segment-summary]').boundingBox(), 'підпис «N of M approved»');
      for (const seg of await bar.locator('[data-segment]').all()) {
        inside(await seg.boundingBox(), 'сегмент');
      }

      // Сегменти непорожні: фон непрозорий, а межа не пунктирна.
      const looks = await bar.locator('[data-segment]').evaluateAll((nodes) =>
        nodes.map((node) => {
          const style = getComputedStyle(node);
          return { background: style.backgroundColor, borderStyle: style.borderTopStyle };
        }),
      );
      for (const look of looks) {
        expect(look.background, 'сегмент без заливки').not.toMatch(/^rgba\(.*,\s*0\)$|^transparent$/);
        expect(look.borderStyle, 'сегмент з пунктирною рамкою').not.toBe('dashed');
      }
    });
  }
});

/** Кожному документу переліку — 19 аркушів: у першого всі Draft, у решти — суміш станів. */
async function padSheets(page: Page): Promise<void> {
  await page.route(/\/api\/v1\/documents\?/, async (route) => {
    const response = await route.fetch();
    const body = (await response.json()) as { items?: Array<Record<string, unknown>> };
    const states = ['Approved', 'Submitted', 'Rejected', 'Draft'];
    (body.items ?? []).forEach((item, index) => {
      const sheetStates: Record<string, string> = {};
      for (let n = 0; n < Sheets; n += 1) {
        sheetStates[`S${String(n + 1).padStart(2, '0')}`] = index === 0 ? 'Draft' : (states[n % states.length] ?? 'Draft');
      }
      item['sheetStates'] = sheetStates;
      delete item['sheets'];
    });
    await route.fulfill({ response, json: body });
  });
}

async function signIn(page: Page, user: string, password: string): Promise<void> {
  await page.goto('/login');
  await expect(page.getByRole('heading').first(), 'сторінка входу не відрендерилася').toBeVisible({ timeout: 30_000 });
  await page.getByLabel(/User name|Ім'я/i).fill(user);
  await page.getByRole('textbox', { name: /Password|Пароль/i }).fill(password);
  await page.keyboard.press('Enter');
  await page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 30_000 });
}
