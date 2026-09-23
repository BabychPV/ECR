import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, it, expect } from 'vitest';
import { surfaces } from '../theme';

/**
 * `U-12`: шапка таблиці зливалася з рядками даних.
 *
 * Заміряно на `/admin/security`: `<th>` — `rgb(246,247,250)`, перший `<td>` —
 * **той самий** `rgb(246,247,250)` (це `surfaces.light.ground`, тобто
 * `--mantine-color-body`), і нижньої межі в шапки не було. Те саме в темній.
 *
 * ⛔ Клас `.ecr-sticky-head` — СПІЛЬНИЙ: його носять чотирнадцять таблиць
 * (матриця прав, `DataTable` набору, імпорт, мапінг, доставки сповіщень,
 * діфи версій…). Тому перевірка стоїть на самому правилі, а не на екрані
 * безпеки: полагоджено одразу всі, і зламати це можна теж одразу всі.
 *
 * ⚠ jsdom не застосовує зовнішні стилі, тож «зміряти колір на екрані» в
 * компонентному тесті неможливо — computed style віддав би порожнечу й був би
 * зеленим за будь-якого CSS. Читається сам файл, як це вже робить
 * `motion.test.tsx` для `prefers-reduced-motion`.
 */

const css = readFileSync(
  path.resolve(process.cwd(), 'src/shared/theme/motion.css'),
  'utf8',
).replace(/\/\*[\s\S]*?\*\//g, '');

/** Тіло правила із заданим селектором. */
function ruleBody(selector: string): string {
  const start = css.indexOf(`${selector} {`);

  // ⚠ Кидаємо, а не `expect`: правило шукається під час складання набору, і
  // тихо порожнє тіло зробило б зеленими перевірки, яким нема на що дивитися.
  if (start < 0) throw new Error(`У motion.css немає правила ${selector}`);

  const open = css.indexOf('{', start);
  const close = css.indexOf('}', open);

  return css.slice(open + 1, close);
}

/** Значення властивості в тілі правила. */
function declaration(body: string, property: string): string {
  const match = new RegExp(`(?:^|;|\\n)\\s*${property}\\s*:([^;]+)`).exec(body);

  return (match?.[1] ?? '').trim();
}

describe('Шапка таблиці відрізняється від рядків даних (U-12)', () => {
  const head = ruleBody('.ecr-sticky-head thead th');
  const firstCell = ruleBody('.ecr-sticky-first tbody td:first-child');
  const corner = ruleBody('.ecr-sticky-first thead th:first-child');

  it('заливка шапки — НЕ та сама змінна, якою залито сторінку і прилиплу колонку', () => {
    const headBackground = declaration(head, 'background');

    // ⛔ Саме це й було дефектом: обидва рядки читали `--mantine-color-body`.
    expect(headBackground).not.toContain('--mantine-color-body');
    expect(declaration(firstCell, 'background')).toContain('--mantine-color-body');
    expect(headBackground).not.toBe(declaration(firstCell, 'background'));
  });

  it('кут перетину лишається шапкою, а не поверхнею рядків', () => {
    expect(declaration(corner, 'background')).toBe(declaration(head, 'background'));
  });

  it('у шапки є нижня межа — інакше смугастий рядок зіллється з нею на другому', () => {
    const border = declaration(head, 'border-bottom');

    // ⚠ Одного тла НЕ досить: `striped` фарбує рядки через один власним
    // значенням Mantine, і «шапка не схожа на перший рядок» ще не означає
    // «шапка не схожа на жоден».
    expect(border).not.toBe('');
    expect(border).toContain('var(--ecr-');
    expect(border).toMatch(/[1-9]px/);
  });

  it('обидві теми лишаються різними — значення беруться з токенів, не з літералів', () => {
    // ⛔ Літеральний колір зробив би читабельною рівно одну схему.
    expect(declaration(head, 'background')).toMatch(/^var\(--ecr-[a-z-]+\)$/);

    // Токен шапки (`sunken`) і токен сторінки (`ground`) справді різні —
    // і у світлій, і в темній.
    expect(surfaces.light.sunken).not.toBe(surfaces.light.ground);
    expect(surfaces.dark.sunken).not.toBe(surfaces.dark.ground);
  });
});
