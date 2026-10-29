import { expect, test, type CDPSession, type Page } from '@playwright/test';
import { createRowColorDocument, openAs, type RowColorDocument } from './rowColor';

/**
 * ui-rowcolor на живому стенді (P1 від людини, Land «3.00 Stationary Equipment»): (1) колір тексту, обраний у тулбарі, має
 * ВІДОБРАЖАТИСЬ (computed `color` комірки) — на залитому й на незалитому рядку, у світлій і темній темі, після reload;
 * (2) заливка цілого рядка не гальмує: застосування — один кадр, прокрутка з 50 залитими рядками — без довгих кадрів.
 * Заміри — справжній Chromium (CDP `Performance` + довгі завдання + інтервали rAF), числа лишаються в `test-results`.
 */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const Repeats = 5;
const Rows = 120;
const Orange = '#ffc000';
const Red = '#c00000';
const Green = '#00b050';

let doc: RowColorDocument | undefined;
let tableInstanceId = 0;

const cell = (page: Page, row: number, col: number) =>
  page.locator(`revo-grid:visible revogr-data[type="rgRow"] .rgCell[data-rgrow="${String(row)}"][data-rgcol="${String(col)}"]`);

async function openDocument(page: Page): Promise<void> {
  page.on('request', (request) => {
    const match = /\/documents\/\d+\/tables\/(\d+)\//.exec(request.url());
    if (match?.[1] !== undefined) tableInstanceId = Number(match[1]);
  });
  const current = doc as RowColorDocument;
  await page.goto(`/documents/${String(current.documentId)}?periodKey=${String(current.periodKey)}`);
  await expect(page.locator('revo-grid:visible').first()).toBeVisible({ timeout: 60_000 });
  await expect(cell(page, 0, 2)).toBeVisible({ timeout: 60_000 });
}

async function choose(page: Page, target: 'fill' | 'text', hex: string): Promise<void> {
  await page.getByTestId(`cell-color-${target}`).first().click();
  await page.getByRole('button', { name: hex, exact: true }).first().click();
}

async function metrics(cdp: CDPSession): Promise<Record<string, number>> {
  const { metrics: list } = (await cdp.send('Performance.getMetrics')) as { metrics: { name: string; value: number }[] };

  return Object.fromEntries(list.map((item) => [item.name, item.value]));
}

function delta(before: Record<string, number>, after: Record<string, number>): Record<string, number> {
  const keys = ['LayoutCount', 'RecalcStyleCount', 'LayoutDuration', 'RecalcStyleDuration', 'ScriptDuration', 'TaskDuration'];

  return Object.fromEntries(keys.map((key) => [key, Math.round(((after[key] ?? 0) - (before[key] ?? 0)) * (key.endsWith('Duration') ? 1000 : 1))]));
}

test.describe('rowColor: колір тексту й заливка рядка', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');
  test.describe.configure({ mode: 'serial', timeout: 600_000 });

  for (const scheme of ['light', 'dark'] as const) {
    test(`колір тексту: заливка рядка, без заливки, діапазон, колонка, reload — тема ${scheme} (N/5)`, async ({ browser }) => {
      const page = await openAs(browser, scheme);

      try {
        doc ??= await createRowColorDocument(page, Rows);
        await openDocument(page);
        const picker = page.getByTestId('cell-color-scope');

        for (let i = 0; i < Repeats; i += 1) {
          const label = `${scheme} ${String(i + 1)}/${String(Repeats)}`;
          const colour = i % 2 === 0 ? Red : Green;
          const rgb = i % 2 === 0 ? 'rgb(192, 0, 0)' : 'rgb(0, 176, 80)';
          const filledRow = 1 + i;
          const plainRow = 8 + i;

          // «Весь рядок» із заливкою, потім колір тексту на тому ж рядку.
          await cell(page, filledRow, 2).click();
          await picker.selectOption('row');
          await choose(page, 'fill', Orange);
          await expect(cell(page, filledRow, 2), `${label}: заливка рядка`).toHaveAttribute('data-user-fill', 'true', { timeout: 15_000 });
          await choose(page, 'text', colour);
          await expect(cell(page, filledRow, 2), `${label}: дані користувацького тексту`).toHaveAttribute('data-user-text', 'true', { timeout: 15_000 });
          if (i === 0) await page.screenshot({ path: test.info().outputPath(`rowcolor-${scheme}.png`) });
          await expect(cell(page, filledRow, 2), `${label}: computed color на ЗАЛИТОМУ рядку`).toHaveCSS('color', rgb);
          await expect(cell(page, filledRow, 6), `${label}: computed color, інша колонка рядка`).toHaveCSS('color', rgb);

          // Той самий колір на незалитому рядку (лише комірка).
          await cell(page, plainRow, 2).click();
          await picker.selectOption('selection');
          await choose(page, 'text', colour);
          await expect(cell(page, plainRow, 2), `${label}: computed color на НЕЗАЛИТОМУ рядку`).toHaveCSS('color', rgb);

          // Колонка і діапазон.
          await cell(page, 14, 4).click();
          await picker.selectOption('column');
          await choose(page, 'text', colour);
          await expect(cell(page, 15, 4), `${label}: колонка`).toHaveCSS('color', rgb);
          await picker.selectOption('selection');
          await cell(page, 18, 8).click();
          await page.keyboard.down('Shift');
          await cell(page, 20, 10).click();
          await page.keyboard.up('Shift');
          await choose(page, 'text', colour);
          await expect(cell(page, 19, 9), `${label}: діапазон`).toHaveCSS('color', rgb);

          // Reload: колір приходить із сервера й відображається.
          await page.reload();
          await expect(cell(page, filledRow, 2)).toBeVisible({ timeout: 60_000 });
          await expect(cell(page, filledRow, 2), `${label}: після reload, залитий рядок`).toHaveCSS('color', rgb);
          await expect(cell(page, plainRow, 2), `${label}: після reload, незалитий рядок`).toHaveCSS('color', rgb);
        }
      } finally {
        await page.context().close();
      }
    });
  }

  test('швидкодія: заливка рядка — один кадр; прокрутка з 50 залитими рядками — без довгих кадрів (N/5)', async ({ browser }, testInfo) => {
    const page = await openAs(browser, 'light');

    try {
      doc ??= await createRowColorDocument(page, Number(process.env['ROWCOLOR_ROWS'] ?? Rows));
      await openDocument(page);
      const cdp = await page.context().newCDPSession(page);
      await cdp.send('Performance.enable');
      await page.evaluate(() => {
        const w = window as unknown as { __long: number[]; __loaf: unknown[] };
        w.__long = [];
        w.__loaf = [];
        new PerformanceObserver((list) => {
          for (const entry of list.getEntries()) w.__long.push(entry.duration);
        }).observe({ type: 'longtask', buffered: false });
        // Довгі кадри анімації з атрибуцією скриптів (хто саме тримає потік): джерело, функція, тривалість.
        try {
          new PerformanceObserver((list) => {
            for (const entry of list.getEntries()) {
              const loaf = entry as unknown as { duration: number; blockingDuration: number; renderStart: number; startTime: number; styleAndLayoutStart: number; scripts: { sourceURL: string; sourceFunctionName: string; invoker: string; duration: number; forcedStyleAndLayoutDuration: number }[] };
              w.__loaf.push({
                d: Math.round(loaf.duration),
                render: Math.round(loaf.startTime + loaf.duration - loaf.renderStart),
                scripts: loaf.scripts.map((script) => `${script.sourceURL.split('/').slice(-2).join('/').replace(/\?.*$/, '')}#${script.sourceFunctionName || script.invoker}:${String(Math.round(script.duration))}ms(forced ${String(Math.round(script.forcedStyleAndLayoutDuration))})`),
              });
            }
          }).observe({ type: 'long-animation-frame', buffered: false });
        } catch {
          // Chromium без LoAF — лишаються longtask.
        }
      });

      const fills: { ms: number; domMs: number; counters: Record<string, number>; long: number[]; loaf: unknown[] }[] = [];

      for (let i = 0; i < Repeats; i += 1) {
        const row = 22 + i;
        await cell(page, row, 2).click();
        await page.getByTestId('cell-color-scope').selectOption('row');
        await page.getByTestId('cell-color-fill').first().click();
        await page.evaluate((r) => {
          const w = window as unknown as { __t0: number; __tdom: number; __long: number[] };
          w.__long = [];
          (window as unknown as { __loaf: unknown[] }).__loaf = [];
          w.__tdom = 0;
          document.addEventListener('click', () => (w.__t0 = performance.now()), { capture: true, once: true });
          // Момент, коли заливка з'явилась у DOM (відділяє роботу React/RevoGrid від очікування кадру).
          const observer = new MutationObserver(() => {
            if (w.__tdom === 0 && document.querySelector(`revo-grid revogr-data[type="rgRow"] .rgCell[data-rgrow="${String(r)}"][data-user-fill="true"]`) !== null) {
              w.__tdom = performance.now();
              observer.disconnect();
            }
          });
          observer.observe(document.body, { subtree: true, attributes: true, attributeFilter: ['data-user-fill'], childList: true });
        }, row);
        const before = await metrics(cdp);
        await page.getByRole('button', { name: i % 2 === 0 ? Orange : '#ffff00', exact: true }).first().click();
        await page.waitForFunction(
          (r) => document.querySelectorAll(`revo-grid revogr-data[type="rgRow"] .rgCell[data-rgrow="${String(r)}"][data-user-fill="true"]`).length >= 8,
          row,
          { timeout: 15_000 },
        );
        const { ms, domMs } = await page.evaluate(
          () =>
            new Promise<{ ms: number; domMs: number }>((resolve) => {
              const w = window as unknown as { __t0: number; __tdom: number };
              requestAnimationFrame(() => requestAnimationFrame(() => resolve({ ms: performance.now() - w.__t0, domMs: w.__tdom === 0 ? -1 : w.__tdom - w.__t0 })));
            }),
        );
        const after = await metrics(cdp);
        const long = await page.evaluate(() => (window as unknown as { __long: number[] }).__long.slice());
        const loaf = await page.evaluate(() => (window as unknown as { __loaf: unknown[] }).__loaf.slice());
        fills.push({ ms: Math.round(ms), domMs: Math.round(domMs), counters: delta(before, after), long, loaf });
      }

      // 50 залитих рядків через API (один PATCH зі scope), reload, прокрутка колесом.
      const current = doc as RowColorDocument;
      const rowKeys = current.rowKeys.slice(0, 50);
      const response = await page.request.patch(`/api/v1/documents/${String(current.documentId)}/tables/${String(tableInstanceId)}/cell-styles`, {
        data: { periodKey: current.periodKey, reset: false, scope: { rowKeys, allColumns: true, allRows: false, reset: false, backgroundHex: Orange, foregroundHex: null, isBold: null } },
      });
      expect(response.status(), `PATCH 50 рядків: ${await response.text()}`).toBe(200);
      await page.reload();
      await expect(cell(page, 0, 2)).toBeVisible({ timeout: 60_000 });

      const box = await page.locator('revo-grid:visible').first().boundingBox();
      expect(box).not.toBeNull();
      const scrolls: { maxFrame: number; over50: number; frames: number }[] = [];

      for (let i = 0; i < Repeats; i += 1) {
        await page.mouse.move((box?.x ?? 0) + 400, (box?.y ?? 0) + 200);
        await page.evaluate(() => {
          const w = window as unknown as { __frames: number[]; __stop: boolean };
          w.__frames = [];
          w.__stop = false;
          let last = performance.now();
          const tick = (now: number): void => {
            w.__frames.push(now - last);
            last = now;
            if (!w.__stop) requestAnimationFrame(tick);
          };
          requestAnimationFrame(tick);
        });
        for (let step = 0; step < 30; step += 1) {
          await page.mouse.wheel(0, step < 15 ? 160 : -160);
          await page.waitForTimeout(16);
        }
        const frames = await page.evaluate(() => {
          const w = window as unknown as { __frames: number[]; __stop: boolean };
          w.__stop = true;

          return w.__frames.slice(1);
        });
        scrolls.push({ maxFrame: Math.round(Math.max(...frames)), over50: frames.filter((f) => f > 50).length, frames: frames.length });
      }

      const report = { fills, scrolls };
      await testInfo.attach('rowcolor-perf.json', { body: JSON.stringify(report, null, 2), contentType: 'application/json' });
      console.log(`ROWCOLOR-PERF ${JSON.stringify(report)}`);

      const worstFill = Math.max(...fills.map((f) => f.ms));
      const worstFrame = Math.max(...scrolls.map((s) => s.maxFrame));
      expect(worstFill, `заливка рядка: до наступного кадру ${JSON.stringify(fills.map((f) => f.ms))} мс (бюджет 100 мс у production-збірці, у dev-стенді ≤ 400)`).toBeLessThanOrEqual(400);
      expect(worstFrame, `прокрутка: найдовший кадр ${JSON.stringify(scrolls.map((s) => s.maxFrame))} мс (бюджет 50)`).toBeLessThanOrEqual(100);
    } finally {
      await page.context().close();
    }
  });
});
