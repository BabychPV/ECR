import { test, expect } from '@playwright/test';
import { structuralDifference } from './greyscale';

/**
 * `ФВ-14.18` у пікселях (`D-140`, гейт 4).
 *
 * ⛔ Це та половина вимоги, якої не видно в jsdom: там немає розкладки, отже
 * немає ані меж, ані маркерів, ані площі, яку вони займають. Гейти 1–3 живуть
 * у `cellStateMeasured.test.ts` над токенами; цей — над тим, що справді
 * намальовано.
 *
 * ⚠ Директива №04 §7.2 називає орієнтир: кутовий трикутник 8×8 у комірці
 * 100×24 — близько 2.7 % площі, тобто ледь проходить поріг 2 %. Якщо носій
 * опинився нижче — це не привід опускати поріг: **носій, що займає менше 2 %
 * комірки, оператор і не помітить.** Треба збільшувати носій.
 */
const STATES = ['dirty', 'readOnly', 'calculated', 'orphaned', 'rounded', 'normal'] as const;

/** Поріг із директиви; калібрований на чинній палітрі (числа — у звіті нижче). */
const MinimumDifference = 0.02;

for (const scheme of ['light', 'dark'] as const) {
  test(`ФВ-14.18: стани комірки різняться без кольору — тема ${scheme}`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme });
    await page.goto('/_kitchen-sink');

    // ⚠ Явний вибір теми, а не лише `emulateMedia`: перемикач зберігає вибір
    // у `localStorage`, і попередній прогін лишив би там інше значення.
    await page.getByText(scheme === 'light' ? 'Світла' : 'Темна', { exact: true }).click();

    const stand = page.locator('[data-measure="cell-states"]');
    await expect(stand).toBeVisible();

    const shots: Record<string, Buffer> = {};
    for (const state of STATES) {
      shots[state] = await stand.locator(`[data-measure-cell="${state}"]`).screenshot();
    }

    const report: string[] = [];
    let worst = 1;

    for (let i = 0; i < STATES.length; i++) {
      for (let j = i + 1; j < STATES.length; j++) {
        const a = STATES[i]!;
        const b = STATES[j]!;
        const difference = structuralDifference(shots[a]!, shots[b]!);

        report.push(`${a}/${b}: ${(difference * 100).toFixed(1)} %`);
        worst = Math.min(worst, difference);

        expect(
          difference,
          `${a}/${b} у темі ${scheme}: різниця ${(difference * 100).toFixed(1)} % ` +
            'після знеколірення. Носій, що займає менше 2 % комірки, оператор не помітить — ' +
            'збільшуй носій, а не опускай поріг.',
        ).toBeGreaterThanOrEqual(MinimumDifference);
      }
    }

    // ⚠ Числа друкуються завжди: поріг калібрується один раз, а бачити запас
    // корисно на кожному прогоні — різке падіння між прогонами помітне
    // раніше, ніж воно дійде до порога.
    console.log(`[${scheme}] найгірша пара ${(worst * 100).toFixed(1)} %\n  ${report.join('\n  ')}`);
  });
}

test('ФВ-14.18: знеколірення справді прибирає колір', async ({ page }) => {
  // ⛔ Калібрування самої перевірки. Дві комірки, що різняться ЛИШЕ фоном і
  // не мають форми, мусять дати різницю НИЖЧЕ порога — інакше гейт міряє
  // колір, який щойно оголосили несуттєвим, і був би зеленим на палітрі
  // взагалі без другого носія.
  await page.goto('/_kitchen-sink');

  await page.evaluate(() => {
    const host = document.createElement('div');
    host.setAttribute('data-calibration', '');
    host.innerHTML = `
      <div data-flat="a" style="width:100px;height:24px;background:#fff8e1">1 234,56</div>
      <div data-flat="b" style="width:100px;height:24px;background:#eef6ff">1 234,56</div>`;
    document.body.append(host);
  });

  const host = page.locator('[data-calibration]');
  const a = await host.locator('[data-flat="a"]').screenshot();
  const b = await host.locator('[data-flat="b"]').screenshot();

  const difference = structuralDifference(a, b);

  console.log(`калібрування: два фони без форми дають ${(difference * 100).toFixed(1)} %`);
  expect(difference).toBeLessThan(MinimumDifference);
});
