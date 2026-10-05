import { expect, test, type CDPSession, type Page } from '@playwright/test';

/**
 * Гейт для класу дефектів T3-01 → T4-01 (швидкий ввід склеює значення або
 * пише їх у чужу комірку) — у СПРАВЖНЬОМУ RevoGrid, без стенда.
 *
 * ⛔ Чому дефект виправлявся двічі. Перше виправлення T3-01 (563fa6c0)
 * доводилося vitest-МОДЕЛЛЮ RevoGrid (`keyCommitGate.test.ts`), у якій старий
 * редактор зникав із DOM одразу після Enter. Живий прогін був, але окремим
 * харнесом поза репозиторієм і з паузами 0/3/10/30/80 мс. Справжній RevoGrid
 * шле `focuscell` РАНІШЕ, ніж прибирає старий `<input>` (T4-01, 7983649b), і
 * відтворений Enter потрапляв у нього: при паузах 50–80 мс значення
 * лягали через рядок. Модель була хибна, а прогону, що міг це показати, не
 * було ні в CI, ні в репозиторії.
 *
 * ⚠ Тому тут — сітка інтервалів, що щільно покриває вікно переходу фокуса
 * RevoGrid (~70 мс, `timeout(RESIZE_INTERVAL + 30)`), і кожен інтервал
 * повторюється: дефект T4-01 відтворювався не на кожному прогоні.
 *
 * ⚠ Стенд не потрібен (як `cellStates.spec.ts`): запуск
 * `ECR_E2E_OPTIONAL=1 npx playwright test e2e/keyCommitGateLive.spec.ts`.
 */
const Stand = '/_key-commit-gate';
const IntervalsMs = [0, 10, 20, 30, 40, 50, 55, 60, 65, 70, 75, 80, 90, 100, 120];
const Repeats = 2;
const Values = ['1', '2', '3'];

async function openStand(page: Page, gate: boolean): Promise<void> {
  await page.goto(gate ? Stand : `${Stand}?gate=0`);
  await expect(page.locator('[data-stand="key-commit-gate"][data-ready="true"]')).toBeAttached({ timeout: 30_000 });
  const firstQty = page.locator('revo-grid revogr-data[type="rgRow"] .rgCell[data-rgcol="1"][data-rgrow="0"]');
  await expect(firstQty).toBeVisible({ timeout: 30_000 });
  await firstQty.click();
}

/** Послідовність сканера/макросу: `Enter, 1, Enter, Enter, 2, Enter, ...` з паузою між клавішами. */
async function typeFast(page: Page, intervalMs: number): Promise<void> {
  const keys = Values.flatMap((value) => ['Enter', value, 'Enter']);
  for (const key of keys) {
    await page.keyboard.press(key);
    if (intervalMs > 0) await page.waitForTimeout(intervalMs);
  }
}

/** Значення колонки QTY у рядках R1..R8 — із джерела сітки, а не з DOM (віртуалізація). */
async function quantities(page: Page): Promise<string[]> {
  // ⚠ Після останнього Enter черга ще може тримати хвіст до запасного терміну (250 мс).
  await page.waitForTimeout(400);

  return page.evaluate(async () => {
    const grid = document.querySelector('revo-grid') as unknown as {
      getSource: () => Promise<{ qty: unknown }[]>;
    };
    const source = await grid.getSource();

    return source.map((row) => String(row.qty ?? ''));
  });
}

const Expected = [...Values, '', '', '', '', ''];

test.describe('Швидкий ввід у справжньому RevoGrid (T3-01, T4-01)', () => {
  for (const intervalMs of IntervalsMs) {
    test(`інтервал ${intervalMs} мс: кожне значення у своєму рядку`, async ({ page }) => {
      for (let attempt = 1; attempt <= Repeats; attempt += 1) {
        await openStand(page, true);
        await typeFast(page, intervalMs);

        expect(await quantities(page), `прогін ${attempt}/${Repeats}`).toEqual(Expected);
      }
    });
  }

  /*
   * ⛔ Контроль самого приладу: без черги той самий ввід мусить псуватися хоча
   * б на одному інтервалі вікна переходу. Якщо колись і без черги все зелене —
   * стенд більше не відтворює умов дефекту (інша версія RevoGrid, інші
   * пропси), і зелені тести вище нічого не доводять.
   */
  test('контроль: без черги швидкий ввід псується', async ({ page }) => {
    const broken: number[] = [];
    for (const intervalMs of [0, 10, 30, 50, 60, 70]) {
      await openStand(page, false);
      await typeFast(page, intervalMs);
      if (JSON.stringify(await quantities(page)) !== JSON.stringify(Expected)) broken.push(intervalMs);
    }

    expect(broken.length, 'без черги дефект не відтворюється — стенд не відтворює умов RevoGrid').toBeGreaterThan(0);
  });
});

/*
 * ⛔ T5-01 (прохід 5, main c4d64f41): на 50–80 мс значення все ще лягало в чужий
 * рядок або губилось (≈4 %), а гейт вище був зелений (16/16, 320/320). Голий
 * RevoGrid цього не відтворює: збій вимагає ПОВІЛЬНОГО переходу - перерендеру
 * `DocumentGrid` після фіксації на навантаженій машині. Звідси дві умови цього
 * блоку: справжній `DocumentGrid` (`/_key-commit-gate/document`, сервер у
 * браузері) і CPU ×4 через CDP. Без них та сама сітка інтервалів була чиста:
 * 0 з 400 без навантаження; під ×4 до фіксу - 3 з 200.
 *
 * ⚠ Збій імовірнісний (~1.5 % прогону до фіксу), тож прогонів багато: 7 інтервалів
 * × `DocRepeats` × дві послідовності. Мірило подвійне: значення в DOM і тіла PATCH.
 */
const DocStand = '/_key-commit-gate/document';
const DocIntervalsMs = [50, 55, 60, 65, 70, 75, 80];
const DocRepeats = Number(process.env['ECR_GATE_REPEATS'] ?? '6');
const CpuSlowdown = 4;

const Sequences = {
  /** Сканер: Enter відкриває, значення, Enter фіксує. */
  A: { keys: ['1', '2', '3'].flatMap((value) => ['Enter', value, 'Enter']), expected: ['1', '2', '3'] },
  /** Друк одразу в комірку (символ відкриває редактор), Enter фіксує. */
  B: { keys: ['5', '7', '9'].flatMap((value) => [value, 'Enter']), expected: ['5', '7', '9'] },
} as const;

async function openDocumentStand(page: Page): Promise<void> {
  await page.goto(DocStand);
  const firstQty = page.locator('revo-grid revogr-data[type="rgRow"] .rgCell[data-rgcol="1"][data-rgrow="0"]');
  await expect(firstQty).toBeVisible({ timeout: 30_000 });
  await firstQty.click();
  // Як у тестувальника: пауза після кліку, далі - послідовність без пауз людини.
  await page.waitForTimeout(400);
}

/** QTY у R1..R5 з DOM і рядки, що пішли в PATCH (`rowKey=значення`). */
async function documentState(page: Page): Promise<{ shown: string[]; patched: string[] }> {
  // ⚠ Автозбереження - через 500 мс тиші після останньої правки, плюс затримка стенда.
  await page.waitForTimeout(1_300);

  return page.evaluate(() => {
    const shown = Array.from({ length: 5 }, (_, row) => {
      const cell = document.querySelector(
        `revo-grid revogr-data[type="rgRow"] .rgCell[data-rgcol="1"][data-rgrow="${String(row)}"]`,
      );

      return cell?.textContent?.trim() ?? '?';
    });
    const patches = (window as unknown as { __standPatches?: { rowKey: string; cells: { value: unknown }[] }[] })
      .__standPatches;
    const patched = (patches ?? []).map((row) => `${row.rowKey}=${row.cells.map((cell) => String(cell.value)).join(',')}`);

    return { shown, patched };
  });
}

test.describe('Швидкий ввід у справжньому DocumentGrid під навантаженням CPU (T5-01)', () => {
  // ⚠ 7 × DocRepeats × ~5 с на CPU ×4 - бюджет тесту з запасом.
  test.setTimeout(DocRepeats * 60_000);

  let cdp: CDPSession | null = null;

  test.beforeEach(async ({ page }) => {
    cdp = await page.context().newCDPSession(page);
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: CpuSlowdown });
  });

  test.afterEach(async () => {
    await cdp?.send('Emulation.setCPUThrottlingRate', { rate: 1 });
    cdp = null;
  });

  for (const [name, sequence] of Object.entries(Sequences)) {
    test(`послідовність ${name}, 50–80 мс: кожне значення у своєму рядку і в PATCH`, async ({ page }) => {
      const failures: string[] = [];
      const expectedPatched = sequence.expected.map((value, row) => `R${String(row + 1)}=${value}`);

      for (const intervalMs of DocIntervalsMs) {
        for (let attempt = 1; attempt <= DocRepeats; attempt += 1) {
          await openDocumentStand(page);
          for (const key of sequence.keys) {
            await page.keyboard.press(key);
            await page.waitForTimeout(intervalMs);
          }

          const { shown, patched } = await documentState(page);
          const shownOk = JSON.stringify(shown) === JSON.stringify([...sequence.expected, '', '']);
          const patchedOk = JSON.stringify([...patched].sort()) === JSON.stringify(expectedPatched);
          if (!shownOk || !patchedOk) {
            failures.push(`${String(intervalMs)} мс #${String(attempt)}: DOM ${shown.join('|')}, PATCH ${patched.join(' ')}`);
          }
        }
      }

      expect(failures, `збої з ${String(DocIntervalsMs.length * DocRepeats)} прогонів`).toEqual([]);
    });
  }
});
