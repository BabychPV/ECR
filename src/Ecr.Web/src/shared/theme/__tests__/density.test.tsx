import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { render, cleanup } from '@testing-library/react';
import { MantineProvider, Table } from '@mantine/core';
import {
  applyDensity,
  measuredRowHeight,
  rowHeight,
  RowHeightVar,
  type Density,
} from '../preferences';

/**
 * `UI-03`: щільність, яку хтось ЧИТАЄ.
 *
 * ⛔ Чому цей файл існує. `UI-01` оголосив `--ecr-row-height`,
 * `--ecr-ctl-height` і `--ecr-rail-item` — і на цьому все скінчилося:
 * споживачів у змінних не було, перемикач у меню профілю писав у
 * `localStorage` і ставив атрибут, а на екрані не змінювалося нічого. Тест, що
 * перевіряв би «клас проставився» чи «змінна оголошена», зеленів би і тоді
 * теж — саме тому нижче міряється ЗНАЧЕННЯ, яке порахував каскад.
 *
 * ### Що jsdom уміє і чого не вміє — дослівно, бо від цього залежить читання
 *
 * Зміряно на цій машині (`vitest@5`, jsdom):
 *
 *   getComputedStyle(root).getPropertyValue('--ecr-row-height')
 *     → "28px" | "36px"      ← КАСКАД РОБИТЬ: селектор `:root[data-…]` матчиться
 *   getComputedStyle(td).height
 *     → "var(--ecr-row-height)"  ← ПІДСТАНОВКИ var() НЕМАЄ
 *
 * ⚠ Тому висота комірки перевіряється у два кроки: (1) правило справді
 * долетіло до РЕАЛЬНОГО `<td>` Mantine — це вирішує селекторний рушій jsdom, а
 * не наш переказ; (2) змінна, з якої ця висота береться, дає різні числа на
 * двох щільностях. Механічна лишається рівно підстановка — те єдине, чого
 * середовище не робить. Сказати «getComputedStyle(td).height === '36px'» тут
 * неможливо в принципі, і мовчати про це означало б лишити по собі сторожа,
 * якого читають як сильнішого, ніж він є.
 *
 * ### Друга пастка, яку цей файл закриває навмисно
 *
 * ⛔ «Змінної немає» не має читатися як «висота правильна». Порожній рядок із
 * `getPropertyValue` і `parseFloat` від нього (`NaN`) порівнюються між собою
 * без жодної скарги — рівно так уже мовчав `expectFocusRing` («різниця
 * 0.00 %»). Тому будь-яке число тут проходить через {@link rowHeightVarPx},
 * який СПЕРШУ вимагає формат `\d+px`, і на це є окремий тест.
 */

const TokensCss = readFileSync(
  path.resolve(process.cwd(), 'src/shared/theme/tokens.css'),
  'utf8',
);

/** Кладе `tokens.css` у документ — так, як це робить `main.tsx`. */
function loadTokens(): void {
  const style = document.createElement('style');
  style.dataset['ecrTestTokens'] = 'true';
  style.textContent = TokensCss;
  document.head.append(style);
}

/**
 * Значення змінної, як його порахував каскад.
 *
 * ⛔ Перевірка формату — не косметика, а сама суть (див. шапку): без неї
 * відсутня змінна дала б `NaN`, і `NaN === NaN` у `toBe` теж падає, а от
 * `toBe('')` проти `''` — ні. Явне твердження про формат називає причину
 * словами замість того, щоб покластися на випадок.
 */
function rowHeightVarPx(): number {
  const raw = getComputedStyle(document.documentElement).getPropertyValue(RowHeightVar).trim();

  expect(
    raw,
    `${RowHeightVar} не оголошена в каскаді: «змінної немає» тихо прочиталося б як «висота правильна».`,
  ).toMatch(/^\d+(?:\.\d+)?px$/);

  return Number.parseFloat(raw);
}

/** Значення довільної змінної щільності — тим самим суворим правилом. */
function densityVarPx(name: string): number {
  const raw = getComputedStyle(document.documentElement).getPropertyValue(name).trim();

  expect(raw, `${name} не оголошена в каскаді.`).toMatch(/^\d+(?:\.\d+)?px$/);

  return Number.parseFloat(raw);
}

function showTable(): HTMLTableCellElement {
  render(
    <MantineProvider>
      <Table>
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Показник</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          <Table.Tr>
            <Table.Td>12,4</Table.Td>
          </Table.Tr>
        </Table.Tbody>
      </Table>
    </MantineProvider>,
  );

  const cell = document.querySelector('tbody td');

  expect(cell, 'таблиця Mantine не намалювала жодної комірки').not.toBeNull();

  return cell as HTMLTableCellElement;
}

afterEach(() => {
  cleanup();

  for (const style of document.querySelectorAll('style[data-ecr-test-tokens]')) style.remove();
  delete document.documentElement.dataset['ecrDensity'];
});

describe('UI-03: висоту рядка таблиці задає щільність', () => {
  it('перемикання змінює ОБЧИСЛЕНУ висоту рядка таблиці', () => {
    loadTokens();

    const cell = showTable();

    // (1) Правило долетіло до справжнього `<td>` Mantine. Це твердження про
    // СЕЛЕКТОР: перейменують статичний клас — і тут стане `''`.
    expect(
      getComputedStyle(cell).height,
      'висота комірки таблиці не береться з --ecr-row-height: правило не застосувалося до <td> Mantine',
    ).toBe(`var(${RowHeightVar})`);

    // (2) …і сама змінна дає РІЗНІ числа на двох щільностях.
    applyDensity('compact');
    const compact = rowHeightVarPx();

    applyDensity('comfortable');
    const comfortable = rowHeightVarPx();

    expect(compact).toBe(28);
    expect(comfortable).toBe(36);

    // ⚠ Окремим твердженням, а не «28 ≠ 36»: числа колись зміняться разом із
    // макетом, а от «просторіше означає вище» не зміниться ніколи.
    expect(comfortable).toBeGreaterThan(compact);
  });

  it('усі три змінні макета перемикаються разом — 28/28/32 → 36/36/40', () => {
    loadTokens();

    applyDensity('compact');
    expect([
      densityVarPx(RowHeightVar),
      densityVarPx('--ecr-ctl-height'),
      densityVarPx('--ecr-rail-item'),
    ]).toEqual([28, 28, 32]);

    applyDensity('comfortable');

    // ⛔ Числа макета (`docs/design/hybrid/index.html:22`), а не директиви №15
    // §1 («36 / 32 / 36»). Розбіжність між ними — не наша: директива
    // посилається на цей макет як на еталон. `UI-01` узяв макет, `UI-03` з ним
    // узгоджений; розбіжність названа в описі PR.
    expect([
      densityVarPx(RowHeightVar),
      densityVarPx('--ecr-ctl-height'),
      densityVarPx('--ecr-rail-item'),
    ]).toEqual([36, 36, 40]);
  });

  it('редактор комірки сітки бере висоту з --ecr-ctl-height', () => {
    loadTokens();

    // ⚠ Розмітка редактора RevoGrid, зібрана вручну: сам веб-компонент у jsdom
    // не визначено, а перевіряється тут СЕЛЕКТОР, тобто те, чи долетить
    // правило до поля вводу всередині сітки.
    const grid = document.createElement('revo-grid');
    grid.setAttribute('theme', 'compact');

    const editor = document.createElement('revogr-edit');
    const input = document.createElement('input');

    editor.append(input);
    grid.append(editor);
    document.body.append(grid);

    expect(getComputedStyle(input).height).toBe('var(--ecr-ctl-height)');

    applyDensity('compact');
    expect(densityVarPx('--ecr-ctl-height')).toBe(28);

    applyDensity('comfortable');
    expect(densityVarPx('--ecr-ctl-height')).toBe(36);

    grid.remove();
  });
});

describe('UI-03: вимірювання не зеленіє на порожнечі', () => {
  it('без tokens.css змінної НЕМАЄ, і вимірювання каже саме це, а не число', () => {
    // ⛔ Другий бік доказу. `applyDensity()` більше не пише `--ecr-row-height`
    // інлайном: єдине джерело — таблиця стилів. Прибери її (або оголошення в
    // ній) — і читання мусить повернути `null`, а не «розумний дефолт», який
    // випадково збігся б із правильною відповіддю.
    applyDensity('comfortable');

    expect(measuredRowHeight()).toBeNull();
  });

  it('сторож самого виміру: без змінної твердження ПАДАЄ', () => {
    // ⛔ Це перевірка перевірки. Без неї «змінної немає» і «висота правильна»
    // лишалися б нерозрізненними для тестів вище — рівно та пастка, що вже
    // спрацювала в `expectFocusRing` і в резолвері токенів.
    expect(() => rowHeightVarPx()).toThrow();
  });
});

describe('UI-03: два написання одного числа не розходяться', () => {
  it.each<Density>(['compact', 'comfortable'])(
    '%s: tokens.css і запасне число theme.ts дають те саме',
    (value) => {
      loadTokens();
      applyDensity(value);

      // ⛔ `theme.other.rowHeight*` лишається ЗАПАСНИМ значенням для
      // середовища без цієї таблиці стилів (модульний тест без `main.tsx`).
      // Запасне значення, що розійшлося з основним, гірше за його відсутність:
      // сітка мовчки жила б в іншій щільності, ніж решта екрана.
      expect(rowHeightVarPx()).toBe(rowHeight(value));
    },
  );
});
