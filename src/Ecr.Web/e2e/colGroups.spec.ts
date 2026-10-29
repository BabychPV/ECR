import { expect, test, type Page } from '@playwright/test';
import { axeViolations } from './axeRule';
import { createGroupedDocument, type GroupedDocument } from './colGroups';
import { signIn } from './multiSheet';

/**
 * ui-colgroups на живому стенді: шапка з груп у 3 рівні над рядком колонок (колонки C1..C12, див.
 * `colGroups.ts`). Кожен сценарій повторюється N/5 усередині спека.
 */
const PeriodKey = process.env['ECR_E2E_PERIOD'] ?? '';
const Repeats = 5;

let doc: GroupedDocument | undefined;

async function open(page: Page): Promise<void> {
  await signIn(page);
  doc ??= await createGroupedDocument(page);
  await page.goto(`/documents/${String(doc.documentId)}?periodKey=${String(doc.periodKey)}`);
  await expect(page.locator('revo-grid:visible').first()).toBeVisible({ timeout: 60_000 });
  await expect(page.locator('revo-grid:visible revogr-header .rgHeaderCell', { hasText: 'C1' }).first()).toBeVisible({ timeout: 60_000 });
}

interface Box {
  readonly x: number;
  readonly y: number;
  readonly w: number;
  readonly h: number;
}

/** Рамка першої клітинки шапки з точним текстом (одним `evaluate` — сітка перемальовується). */
async function headerBox(page: Page, text: string): Promise<Box | null> {
  return page.evaluate((wanted) => {
    const grid = document.querySelector('revo-grid');
    if (grid === null) return null;
    for (const cell of Array.from(grid.querySelectorAll('revogr-header .rgHeaderCell'))) {
      const own = ((cell as HTMLElement).querySelector('.ecr-hg-label, .ecr-header-name')?.textContent ?? (cell as HTMLElement).textContent ?? '')
        .replace(/\s+/g, ' ')
        .trim();
      if (own === wanted || own.endsWith(`: ${wanted}`)) {
        const box = cell.getBoundingClientRect();
        return { x: box.x, y: box.y, w: box.width, h: box.height };
      }
    }
    return null;
  }, text);
}

test.describe('Групові заголовки колонок (ui-colgroups)', () => {
  test.skip(PeriodKey === '', 'ECR_E2E_OPTIONAL: стенда немає, перевіряти нічого. Стенд: tools/e2e-stand.ps1.');

  test('шапка: 3 групові рядки, групи охоплюють колонки, колонки без групи — на всю висоту (N/5)', async ({ page }) => {
    test.setTimeout(300_000);
    await page.setViewportSize({ width: 1700, height: 1000 });
    await open(page);

    for (let i = 0; i < Repeats; i += 1) {
      if (i > 0) await page.reload();
      await expect(page.locator('revo-grid:visible revogr-header .rgHeaderCell', { hasText: 'C12' }).first()).toBeVisible({ timeout: 60_000 });

      const label = `прогін ${String(i + 1)}/${String(Repeats)}`;
      const groupRows = await page.locator('revo-grid:visible revogr-header .header-rgRow.group').count();
      expect(groupRows, `${label}: групових рядків`).toBe(3);

      const [emissions, c1, c2, c8, fuel, c12] = await Promise.all(['Emissions', 'C1', 'C2', 'C8', 'Fuel', 'C12'].map((text) => headerBox(page, text)));
      expect(emissions && c1 && c2 && c8 && fuel && c12, `${label}: усі клітинки шапки знайдено`).toBeTruthy();
      if (!emissions || !c1 || !c2 || !c8 || !fuel || !c12) return;

      // Emissions охоплює C2..C8 (7 колонок) без зазорів.
      const sum = await page.evaluate(() => {
        let total = 0;
        for (const cell of Array.from(document.querySelectorAll('revo-grid revogr-header .rgHeaderCell'))) {
          const name = (cell as HTMLElement).querySelector('.ecr-header-name')?.textContent ?? '';
          const text = ((cell as HTMLElement).textContent ?? '').trim();
          if (/(^|: )C[2-8]$/.test(name.trim()) || /(^|: )C[2-8]$/.test(text)) total += cell.getBoundingClientRect().width;
        }
        return total;
      });
      expect(Math.abs(emissions.w - sum), `${label}: ширина Emissions ${String(emissions.w)} ≠ сума C2..C8 ${String(sum)}`).toBeLessThanOrEqual(3);
      expect(emissions.x).toBeLessThanOrEqual(c2.x + 1);

      // Групові рядки компактні (32 px), рядок колонок — 45 px.
      const group = 32;
      const leaf = 45;
      expect(Math.abs(emissions.h - group), `${label}: висота групового рядка ${String(emissions.h)}`).toBeLessThanOrEqual(2);
      // Колонка без групи — на всю шапку (3 групові рядки + рядок колонок), верх — із першим груповим рядком.
      expect(Math.abs(c1.h - (3 * group + leaf)), `${label}: висота C1 ${String(c1.h)}`).toBeLessThanOrEqual(3);
      expect(Math.abs(c1.y - emissions.y), `${label}: верх C1 і Emissions`).toBeLessThanOrEqual(3);
      expect(Math.abs(c12.h - (3 * group + leaf))).toBeLessThanOrEqual(3);
      // Fuel (корінь із прямими колонками) — один груповий рядок, не вся шапка.
      expect(Math.abs(fuel.h - group)).toBeLessThanOrEqual(2);
      // C8 напряму в Emissions (глибина 1): піднята на 2 групові рядки.
      expect(Math.abs(c8.h - (2 * group + leaf)), `${label}: висота C8 ${String(c8.h)}`).toBeLessThanOrEqual(3);

      // Групові клітинки: межі видимі (токени), у групах немає ручки зміни ширини.
      const borders = await page.evaluate(() => {
        const cell = document.querySelector('revo-grid revogr-header .rgHeaderCell.ecr-hg-grp');
        if (cell === null) return null;
        const style = getComputedStyle(cell);
        return {
          right: style.borderRightWidth,
          rightColor: style.borderRightColor,
          bottom: style.borderBottomWidth,
          resizer: cell.querySelector('.resizable') === null ? 'none' : getComputedStyle(cell.querySelector('.resizable') as Element).display,
        };
      });
      expect(borders, `${label}: групова клітинка`).not.toBeNull();
      expect(borders?.right).toBe('1px');
      expect(borders?.bottom).toBe('1px');
      expect(borders?.rightColor).not.toBe('rgba(0, 0, 0, 0)');
      expect(['none']).toContain(borders?.resizer);
    }
  });

  test('a11y: aria-rowindex шапки 1..4, aria-colspan групи, шлях групи для читалки (N/5)', async ({ page }) => {
    test.setTimeout(300_000);
    await page.setViewportSize({ width: 1700, height: 1000 });
    await open(page);

    for (let i = 0; i < Repeats; i += 1) {
      if (i > 0) await page.reload();
      await expect(page.locator('revo-grid:visible revogr-header .rgHeaderCell', { hasText: 'C12' }).first()).toBeVisible({ timeout: 60_000 });
      await page.waitForTimeout(300);

      const info = await page.evaluate(() => {
        const grid = document.querySelector('revo-grid');
        if (grid === null) return null;
        const rows = Array.from(grid.querySelectorAll('revogr-header .header-rgRow')).map((row) => row.getAttribute('aria-rowindex'));
        const emissions = Array.from(grid.querySelectorAll('revogr-header .rgHeaderCell.ecr-hg-grp')).find(
          (cell) => (cell.textContent ?? '').trim() === 'Emissions',
        );
        const c2 = Array.from(grid.querySelectorAll('revogr-header .rgHeaderCell')).find((cell) => /C2$/.test((cell.textContent ?? '').trim()));
        return {
          rows,
          rowcount: Number(grid.getAttribute('aria-rowcount')),
          colspan: emissions?.getAttribute('aria-colspan') ?? null,
          sr: c2?.querySelector('.ecr-header-sr')?.textContent ?? null,
        };
      });
      const label = `прогін ${String(i + 1)}/${String(Repeats)}`;
      expect(info, label).not.toBeNull();
      expect(info?.rows.slice(0, 4), `${label}: aria-rowindex рядків шапки`).toEqual(['1', '2', '3', '4']);
      expect(info?.rowcount, `${label}: aria-rowcount`).toBeGreaterThanOrEqual(4);
      expect(info?.colspan, `${label}: aria-colspan Emissions`).toBe('7');
      expect(info?.sr, `${label}: шлях групи для читалки`).toBe('Emissions / Q1 / Jan: ');
      // Справжній axe на згрупованій шапці: ARIA-правила, які зачіпає `GridAriaPlugin` (рядки шапки, діти grid, значення атрибутів).
      for (const rule of ['aria-required-parent', 'aria-required-children', 'aria-valid-attr-value', 'aria-allowed-attr']) {
        expect(await axeViolations(page, rule), `${label}: axe ${rule}`).toEqual([]);
      }
    }
  });

  test('мітка групи лишається видимою, коли перша колонка групи пішла за край (N/5)', async ({ page }) => {
    test.setTimeout(300_000);
    await page.setViewportSize({ width: 800, height: 1000 });
    await open(page);

    for (let i = 0; i < Repeats; i += 1) {
      if (i > 0) await page.reload();
      await expect(page.getByTestId('grid-hscroll')).toBeVisible({ timeout: 60_000 });
      await page.getByTestId('grid-hscroll').evaluate((el) => {
        el.scrollLeft = 0;
      });
      await page.waitForTimeout(300);

      // Прокручуємо так, щоб початок Emissions (C2) пішов за ліву межу, а кінець ще лишався у вікні.
      await page.getByTestId('grid-hscroll').evaluate((el) => {
        el.scrollLeft = 260;
      });
      await page.waitForTimeout(500);

      const geometry = await page.evaluate(() => {
        const grid = document.querySelector('revo-grid');
        const scroller = grid?.querySelector('revogr-viewport-scroll.rgCol');
        const label = Array.from(grid?.querySelectorAll('.ecr-hg-label') ?? []).find((node) => (node.textContent ?? '').trim() === 'Emissions');
        const cell = label?.closest('.rgHeaderCell');
        if (!scroller || !label || !cell) return null;
        const view = scroller.getBoundingClientRect();
        const box = label.getBoundingClientRect();
        const cellBox = cell.getBoundingClientRect();
        return { viewLeft: view.left, viewRight: view.right, left: box.left, right: box.right, cellLeft: cellBox.left, cellRight: cellBox.right };
      });
      const label = `прогін ${String(i + 1)}/${String(Repeats)}`;
      expect(geometry, `${label}: мітка Emissions у DOM`).not.toBeNull();
      if (!geometry) return;
      expect(geometry.cellLeft, `${label}: клітинка групи частково за лівим краєм`).toBeLessThan(geometry.viewLeft);
      expect(geometry.left, `${label}: мітка не за лівим краєм вікна`).toBeGreaterThanOrEqual(geometry.viewLeft - 1);
      expect(geometry.right, `${label}: мітка не за правим краєм вікна`).toBeLessThanOrEqual(geometry.viewRight + 1);
      expect(geometry.left).toBeGreaterThanOrEqual(geometry.cellLeft - 1);
      expect(geometry.right).toBeLessThanOrEqual(geometry.cellRight + 1);
    }
  });

  test('вузьке вікно 500 px: мітка групи або цілком у вікні, або прихована — без обрізка (N/5)', async ({ page }) => {
    test.setTimeout(300_000);
    await page.setViewportSize({ width: 500, height: 900 });
    await open(page);

    for (let i = 0; i < Repeats; i += 1) {
      if (i > 0) await page.reload();
      await expect(page.getByTestId('grid-hscroll')).toBeVisible({ timeout: 60_000 });

      for (const left of [0, 120, 240, 360, 480]) {
        await page.getByTestId('grid-hscroll').evaluate((el, value) => {
          el.scrollLeft = value;
        }, left);
        await page.waitForTimeout(600);

        const bad = await page.evaluate(() => {
          const grid = document.querySelector('revo-grid');
          const scroller = grid?.querySelector('revogr-viewport-scroll.rgCol');
          if (!grid || !scroller) return ['немає сітки'];
          const view = scroller.getBoundingClientRect();
          const problems: string[] = [];
          for (const node of Array.from(grid.querySelectorAll<HTMLElement>('.ecr-hg-label'))) {
            const box = node.getBoundingClientRect();
            const cell = node.closest('.rgHeaderCell')?.getBoundingClientRect();
            if (!cell || cell.right < view.left || cell.left > view.right) continue;
            if (getComputedStyle(node).visibility === 'hidden') continue;
            const clipped = node.scrollWidth > node.clientWidth + 1 && getComputedStyle(node).textOverflow !== 'ellipsis';
            if (box.left < view.left - 1 || box.right > view.right + 1 || clipped) {
              problems.push(`${node.textContent ?? ''}: label [${String(Math.round(box.left))},${String(Math.round(box.right))}] view [${String(Math.round(view.left))},${String(Math.round(view.right))}]`);
            }
          }
          return problems;
        });
        expect(bad, `прогін ${String(i + 1)}/${String(Repeats)}, scrollLeft=${String(left)}`).toEqual([]);
      }
    }
  });

  test('конструктор: діалог «Групи колонок», вибір групи колонки, перегляд із груповою шапкою (N/5)', async ({ page }) => {
    test.setTimeout(420_000);
    await page.setViewportSize({ width: 1500, height: 1000 });
    await signIn(page);
    doc ??= await createGroupedDocument(page);
    const base = `/admin/templates/${String(doc.templateId)}/versions/${String(doc.versionId)}?node=t:${String(doc.tableId)}`;

    for (let i = 0; i < Repeats; i += 1) {
      const label = `прогін ${String(i + 1)}/${String(Repeats)}`;
      await page.goto(`${base}&tab=columns`);
      await page.getByTestId('manage-column-groups').click();
      const dialog = page.getByTestId('column-groups-dialog');
      await expect(dialog, `${label}: діалог груп`).toBeVisible({ timeout: 45_000 });
      await expect(dialog.locator('[data-group-code]'), `${label}: 6 груп`).toHaveCount(6);
      await expect(dialog.locator('[data-group-depth="3"]'), `${label}: рівень 3 — Jan і Feb`).toHaveCount(2);

      // На виду — лише поле поточної мови; решта мов згорнуто під «Other languages».
      await expect(dialog.locator('input[data-group-label="current"]'), `${label}: поле поточної мови в кожної групи`).toHaveCount(6);
      await expect(dialog.locator('input[data-group-label="current"]').first()).toBeVisible();
      await expect(dialog.locator('input[data-group-label]:not([data-group-label="current"])').first(), `${label}: інші мови згорнуто`).toBeHidden();
      await dialog.getByTestId('group-other-languages').first().click();
      await expect(dialog.locator('input[data-group-label]:not([data-group-label="current"])').first(), `${label}: інші мови розгорнулись`).toBeVisible();

      // Додаємо кореневу групу з назвою й зберігаємо.
      await dialog.getByRole('button', { name: 'Add group', exact: true }).click();
      await expect(dialog.locator('[data-group-code]')).toHaveCount(7);
      await dialog.locator('[data-group-code]').last().locator('input[data-group-label="current"]').fill(`Extra ${String(i)}`);
      await dialog.getByRole('button', { name: 'Save groups' }).click();
      await expect(page.getByTestId('column-groups-dialog')).toBeHidden({ timeout: 45_000 });

      // Повторне відкриття бачить 7 груп; прибираємо додану й зберігаємо.
      await page.getByTestId('manage-column-groups').click();
      await expect(dialog.locator('[data-group-code]'), `${label}: 7 груп після збереження`).toHaveCount(7, { timeout: 30_000 });
      await dialog.locator('[data-group-code]').last().getByRole('button', { name: 'Remove group' }).click();
      await expect(dialog.locator('[data-group-code]')).toHaveCount(6);
      await dialog.getByRole('button', { name: 'Save groups' }).click();
      await expect(page.getByTestId('column-groups-dialog')).toBeHidden({ timeout: 45_000 });

      // Перегляд таблиці: три групові рядки, Emissions охоплює 7 колонок.
      await page.goto(`${base}&tab=preview`);
      await expect(page.locator('[data-preview-group-row]'), `${label}: групові рядки перегляду`).toHaveCount(3, { timeout: 45_000 });
      await expect(page.locator('th[data-preview-group="Emissions"]')).toHaveAttribute('colspan', '7');
    }
  });
});
