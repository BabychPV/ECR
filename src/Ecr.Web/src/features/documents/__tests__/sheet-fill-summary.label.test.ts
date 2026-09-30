import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * `U-06`: підпис над деревом аркушів існує В КАТАЛОЗІ й каже, що рахується.
 *
 * ⛔ Навіщо окремий набір поруч із `sheet-fill-summary.test.tsx`. Той
 * доводить, що компонент ПРОСИТЬ у каталогу ключ `document.tablesFilled` із
 * двома числами, — і лишився б зеленим, якби ключа в `09-seed.sql` не було
 * взагалі: у продукті на екрані стояло б `⟦document.tablesFilled⟧`, тобто
 * рівно те, чим і виправдовували відсутність підпису до цього коміту. Тому
 * друге твердження перевіряється по САМОМУ сіду.
 *
 * ⛔ І не саме лише «ключ є». Текст, який не називає ТАБЛИЦІ й не каже, що
 * вони заповнені ПОВНІСТЮ, лише замінив би одну двозначність іншою:
 * «Filled: 0 of 91» читається як «заповнено нуль комірок із 91», а це інше
 * і неправдиве твердження — у чисельник не потрапляє таблиця з 710
 * заповненими комірками з 774. Саме тому тут перевіряються слова, а не
 * тільки наявність рядка.
 *
 * ⚠ Читається ЛИШЕ блок `MERGE` — так само, як це робить каталог у
 * `src/test/__tests__/a11yFixtures.tsx`: секції «змінені тексти» й
 * «прибрані ключі» над ним мають той самий вигляд рядка, але несуть старі
 * значення та вже видалені ключі.
 */
function seedMergeBlock(): string {
  const seed = readFileSync(
    path.resolve(process.cwd(), '../../src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql'),
    'utf8',
  );

  const start = seed.indexOf('MERGE sys_ecr.UiString AS t');
  const end = seed.indexOf(') AS s ([Key], Lang, Val, Scope)', start);

  expect(start, '09-seed.sql: не знайдено блоку MERGE sys_ecr.UiString').toBeGreaterThan(-1);
  expect(end, '09-seed.sql: не знайдено кінця блоку MERGE').toBeGreaterThan(start);

  return seed.slice(start, end);
}

/** Значення ключа `document.tablesFilled` для `en` у блоці MERGE. */
function tablesFilledText(): string {
  const row = /\(N'document\.tablesFilled',\s*N'en',\s*N'([^']*)'/.exec(seedMergeBlock());

  expect(row, "09-seed.sql: у MERGE немає ключа document.tablesFilled (en)").not.toBeNull();

  return row?.[1] ?? '';
}

describe('U-06 · підпис заповненості в каталозі', () => {
  it('ключ document.tablesFilled є в сіді й несе обидва параметри', () => {
    const text = tablesFilledText();

    // ⛔ Без обох підстановок підпис показував би одне число з двох — тобто
    // або «скільки», або «зі скількох», і жодне з них само по собі не
    // відповідає на питання, заради якого панель існує.
    expect(text).toContain('{filled}');
    expect(text).toContain('{total}');
  });

  it('текст називає САМЕ те, що рахується: таблиці, заповнені повністю', () => {
    const text = tablesFilledText().toLowerCase();

    // ⛔ «Tables» — бо рахуються таблиці, а не комірки й не аркуші.
    expect(text).toContain('tables');

    // ⛔ «Completely» (або рівнозначне «fully») — бо часткова таблиця в
    // чисельник НЕ йде, і без цього слова «0 of 91» лишається неправдою
    // рівно тієї ж ціни, що й голий дріб.
    expect(text).toMatch(/complete|fully/);
  });
});
