import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * `ФВ-14.29`, перша частина: «заголовок і перша колонка закріплені при
 * прокручуванні».
 *
 * ⚠ Закріплення живе в СПІЛЬНИХ класах `motion.css` (`.ecr-sticky-head`,
 * `.ecr-sticky-first`), а не в кожній таблиці окремо, — тож перевіряється саме
 * правило. `stickyHeadSurface.test.ts` уже тримає ЗАЛИВКУ цих правил (`U-12`),
 * але не саме закріплення: прибери `position: sticky` — і він лишиться
 * зеленим, а шапка поїде разом із рядками.
 *
 * ⚠ jsdom не застосовує зовнішні стилі, і `getComputedStyle` віддав би
 * порожнечу за будь-якого CSS — тому читається сам файл, як у
 * `stickyHeadSurface.test.ts` і `motion.test.tsx`.
 *
 * ⛔ Межа доказу: це правило для таблиць на `Table` Mantine. Сітка документа
 * (RevoGrid, `DocumentGrid.tsx`) — окремий механізм, і її першу колонку ці
 * класи не закріплюють.
 */
const css = readFileSync(
  path.resolve(process.cwd(), 'src/shared/theme/motion.css'),
  'utf8',
).replace(/\/\*[\s\S]*?\*\//g, '');

function ruleBody(selector: string): string {
  const start = css.indexOf(`${selector} {`);

  if (start < 0) throw new Error(`У motion.css немає правила ${selector}`);

  const open = css.indexOf('{', start);

  return css.slice(open + 1, css.indexOf('}', open));
}

function declaration(body: string, property: string): string {
  const match = new RegExp(`(?:^|;|\\n)\\s*${property}\\s*:([^;]+)`).exec(body);

  return (match?.[1] ?? '').trim();
}

describe('Таблиця тримає контекст при прокручуванні', () => {
  // ⚠ ФВ-14.29: мутаційно НЕ доведено (класифікатор дозволів, 2026-09-27/28;
  // рішення людини — здавати з приміткою).
  it('ФВ-14.29: шапка прилипає до верху, перша колонка — до лівого краю', () => {
    const head = ruleBody('.ecr-sticky-head thead th');
    const firstCell = ruleBody('.ecr-sticky-first tbody td:first-child');
    const corner = ruleBody('.ecr-sticky-first thead th:first-child');

    expect(declaration(head, 'position')).toBe('sticky');
    expect(declaration(head, 'top')).toBe('0');

    expect(declaration(firstCell, 'position')).toBe('sticky');
    expect(declaration(firstCell, 'left')).toBe('0');

    // ⚠ Кут перетину прилипає в обидва боки: інакше при горизонтальній
    // прокрутці клітинка шапки над першою колонкою їхала б геть.
    expect(declaration(corner, 'position')).toBe('sticky');
    expect(declaration(corner, 'left')).toBe('0');

    // ⚠ Прилипла шапка має бути НАД прилиплою колонкою, а та — над рядками:
    // без порядку шарів дані при прокрутці малювалися б поверх заголовка.
    expect(Number(declaration(head, 'z-index'))).toBeGreaterThan(
      Number(declaration(firstCell, 'z-index')),
    );
  });
});
