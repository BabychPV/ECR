import { expect, test, type APIResponse, type Locator } from '@playwright/test';
import { createMultiSheetDocument, signIn } from './multiSheet';

/**
 * ✎ 10.11, дефект людини (P2): смуга 12 місяців у переліку документів «не гарно виглядає і не рівно» — «Late (flagged)»
 * (рамка 2px) і поточний період (кільце-тінь назовні) були більшими або зсунутими відносно замкнених клітинок, а порожні
 * клітинки сідали на базову лінію тексту інакше, ніж клітинки зі знаком. Правило — KIT.md «MonthStrip».
 *
 * Проти РЕАЛЬНОГО стенда (`tools/e2e-stand.ps1`, workflow `e2e-stand`, `-Grep "Month strip 10.11"`). Документ і проєкт —
 * власні (`createMultiSheetDocument`). Стани місяців стенд сам не дає (Grace, Closed, поточний разом), тож відповіді
 * `GET /documents` і `GET /projects/{id}/periods` — справжні, лише з підставленими станами місяців: перевіряється
 * верстка справжньої сторінки, а не дані (як у `projectsDocuments.spec.ts`, fg5).
 *
 * ⛔ Без стенда файл ПАДАЄ (`globalSetup.ts`), а не пропускається (honesty-guard).
 */

interface DocumentCard {
  readonly projectId: number;
  readonly businessKey: string;
}

interface Period {
  periodKey: number;
  state: string;
  isCurrent: boolean;
  [key: string]: unknown;
}

/** Розмір, позиція і ВИДИМІ межі (з outline і зовнішньою тінню) клітинки. */
interface Measured {
  readonly cell: string;
  readonly width: number;
  readonly height: number;
  readonly top: number;
  readonly visualWidth: number;
  readonly visualHeight: number;
  readonly visualTop: number;
}

async function ok<T>(response: APIResponse, what: string): Promise<T> {
  const body = await response.text();
  expect(response.status(), `${what}: ${String(response.status())} ${body.slice(0, 1500)}`).toBeLessThan(300);

  return (body.length > 0 ? JSON.parse(body) : {}) as T;
}

/** Стан ПЕРІОДУ на місяць (1–12): Closed ×5, Grace (late), Open+поточний, Scheduled ×2, без запису (unknown). */
const PeriodStateOf: Readonly<Record<number, string | null>> = {
  1: 'Closed', 2: 'Closed', 3: 'Closed', 4: 'Closed', 5: 'Closed', 6: 'Closed', 7: 'Closed', 8: 'Closed',
  9: 'Grace', 10: 'Open', 11: 'Scheduled', 12: null,
};
/** Стан ПОГОДЖЕННЯ документа на місяць: Approved, Submitted, Rejected (повернуто) — решта чернетки. */
const DocumentStateOf: Readonly<Record<number, string>> = { 1: 'Approved', 2: 'Submitted', 3: 'Rejected' };
const CurrentMonth = 10;

async function measure(boxes: Locator): Promise<Measured[]> {
  return boxes.evaluateAll((nodes) =>
    nodes.map((node) => {
      const rect = node.getBoundingClientRect();
      const style = getComputedStyle(node);
      // Що виходить за рамку: outline (товщина + offset) і зовнішні (не inset) тіні.
      let out = 0;
      if (style.outlineStyle !== 'none') out = Math.max(out, parseFloat(style.outlineWidth) + parseFloat(style.outlineOffset));
      if (style.boxShadow !== 'none') {
        for (const layer of style.boxShadow.split(/,(?![^(]*\))/)) {
          if (/\binset\b/.test(layer)) continue;
          const px = (layer.replace(/rgba?\([^)]*\)/g, '').match(/-?\d+(\.\d+)?px/g) ?? []).map((v) => parseFloat(v));
          const [x = 0, y = 0, blur = 0, spread = 0] = px;
          out = Math.max(out, spread + blur + Math.max(Math.abs(x), Math.abs(y)));
        }
      }
      out = Math.max(0, out);

      return {
        cell: `${node.getAttribute('data-cell') ?? '?'}${node.getAttribute('data-current') === 'true' ? '+current' : ''}`,
        width: rect.width,
        height: rect.height,
        top: rect.top,
        visualWidth: rect.width + 2 * out,
        visualHeight: rect.height + 2 * out,
        visualTop: rect.top - out,
      };
    }),
  );
}

function expectAligned(boxes: readonly Measured[], what: string): void {
  const first = boxes[0];
  expect(first, `${what}: немає клітинок`).toBeDefined();
  if (first === undefined) return;
  const dump = boxes.map((b) => `${b.cell} ${b.visualWidth.toFixed(1)}×${b.visualHeight.toFixed(1)}@${b.visualTop.toFixed(1)}`).join(' | ');
  for (const key of ['width', 'height', 'top', 'visualWidth', 'visualHeight', 'visualTop'] as const) {
    const spread = Math.max(...boxes.map((b) => b[key])) - Math.min(...boxes.map((b) => b[key]));
    // soft: смуга й легенда звітують обидві, а не лише перша, що впала.
    expect.soft(spread, `${what}: ${key} різниться на ${spread.toFixed(2)} px — ${dump}`).toBeLessThanOrEqual(0.5);
  }
}

test.describe('Month strip 10.11', () => {
  // Не `serial`: світла й темна схеми незалежні — падіння однієї не має ховати іншу.
  test.describe.configure({ timeout: 300_000 });

  for (const scheme of ['light', 'dark'] as const) {
    test(`all 12 month boxes and the legend swatches have one size on one line (${scheme})`, async ({ page }) => {
      await page.emulateMedia({ colorScheme: scheme });
      await signIn(page);
      const { documentId, periodKey } = await createMultiSheetDocument(page, 1, 1);
      const card = await ok<DocumentCard>(
        await page.request.get(`/api/v1/documents/${String(documentId)}?periodKey=${String(periodKey)}`),
        'GET document',
      );
      const year = Math.trunc(periodKey / 100);

      await page.route(`**/api/v1/projects/${String(card.projectId)}/periods`, async (route) => {
        const response = await route.fetch();
        const body = (await response.json()) as { periods: Period[] };
        const template = body.periods.find((p) => Math.trunc(p.periodKey / 100) === year) ?? body.periods[0];
        expect(template, 'календар проєкту порожній').toBeDefined();
        const others = body.periods.filter((p) => Math.trunc(p.periodKey / 100) !== year).map((p) => ({ ...p, isCurrent: false }));
        const months: Period[] = [];
        for (let month = 1; month <= 12; month += 1) {
          const state = PeriodStateOf[month];
          if (state === null || state === undefined || template === undefined) continue;
          months.push({ ...template, periodKey: year * 100 + month, month, state, isCurrent: month === CurrentMonth });
        }
        await route.fulfill({ response, json: { ...body, periods: [...others, ...months].sort((a, b) => a.periodKey - b.periodKey) } });
      });
      await page.route(
        (url) => url.pathname === '/api/v1/documents',
        async (route) => {
          const response = await route.fetch();
          const body = (await response.json()) as { items: { id: number; periodStates?: unknown }[] };
          const periodStates = Array.from({ length: 12 }, (_, i) => ({
            periodKey: year * 100 + i + 1,
            state: DocumentStateOf[i + 1] ?? 'Draft',
          }));
          await route.fulfill({
            response,
            json: { ...body, items: body.items.map((item) => (item.id === documentId ? { ...item, periodStates } : item)) },
          });
        },
      );

      await page.setViewportSize({ width: 1440, height: 900 });
      await page.goto(`/?projectId=${String(card.projectId)}&year=${String(year)}`);
      const row = page.locator(`[data-document-row="${card.businessKey}"]`);
      await expect(row).toBeVisible({ timeout: 60_000 });
      const cells = row.locator('[data-month]');
      await expect(cells).toHaveCount(12);
      // Підстановка дійшла: смуга справді змішана — усі стани моделі разом із поточним.
      await expect(row.locator(`[data-month="${String(year * 100 + 9)}"]`)).toHaveAttribute('data-cell', 'late', { timeout: 60_000 });
      const states = await cells.evaluateAll((nodes) => nodes.map((n) => n.getAttribute('data-cell')));
      expect(new Set(states), states.join(',')).toEqual(new Set(['approved', 'submitted', 'returned', 'locked', 'late', 'open', 'notOpen', 'unknown']));
      await expect(row.locator(`[data-month="${String(year * 100 + CurrentMonth)}"]`)).toHaveAttribute('data-current', 'true');
      await page.waitForTimeout(500);
      const legend = page.locator('[data-year-strip-legend]');
      await expect(legend).toBeVisible();
      // Знімки — ДО перевірок: на червоному прогоні вони й є доказом «до».
      await row.locator('[data-column="months"]').screenshot({ path: test.info().outputPath(`month-strip-${scheme}.png`) });
      await legend.screenshot({ path: test.info().outputPath(`month-strip-legend-${scheme}.png`) });

      expectAligned(await measure(row.locator('[data-month] .ecr-year-strip-box')), `смуга (${scheme})`);

      const swatches = legend.locator('.ecr-year-strip-box');
      await expect(swatches).toHaveCount(8);
      expectAligned(await measure(swatches), `легенда (${scheme})`);
    });
  }
});
