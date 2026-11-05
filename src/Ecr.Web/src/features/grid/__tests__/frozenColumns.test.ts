import { describe, expect, it } from 'vitest';
import type { ColumnRegular } from '@revolist/revogrid';
import type { ColumnDto } from '@/api/types';
import {
  FrozenDefaultChoice,
  FrozenNoneChoice,
  MaxDefaultFrozenDataColumns,
  MaxFrozenChoices,
  MinDataColumnsForDefaultFreeze,
  applyFrozenColumns,
  clampFrozenCount,
  defaultFrozenCount,
  frozenChoiceValue,
  frozenChoices,
  frozenOverrideOfChoice,
  frozenProbesOf,
  resolveFrozenCount,
  topGroupIdOf,
  type FrozenColumnProbe,
} from '../frozenColumns';

const text = (width = 140, groupId: number | null = null): FrozenColumnProbe => ({ numeric: false, width, groupId });
const num = (width = 140, groupId: number | null = null): FrozenColumnProbe => ({ numeric: true, width, groupId });

/** Доповнює ведучі колонки до порога «широка таблиця» (інакше типове закріплення вимкнене). */
const wide = (lead: FrozenColumnProbe[], filler: () => FrozenColumnProbe = num): FrozenColumnProbe[] => {
  const probes = [...lead];
  while (probes.length < MinDataColumnsForDefaultFreeze) probes.push(filler());

  return probes;
};

describe('defaultFrozenCount: типове закріплення', () => {
  it('вузька таблиця (менше порога) - типово нічого не закріплюється; на порозі закріплення діє', () => {
    const lead = [text(), text()];

    expect(MinDataColumnsForDefaultFreeze).toBe(12);
    expect(defaultFrozenCount(true, [...lead, ...Array.from({ length: 9 }, () => num())], 260)).toBe(0); // 11 колонок
    expect(defaultFrozenCount(true, [...lead, ...Array.from({ length: 10 }, () => num())], 260)).toBe(3); // 12 колонок
    expect(defaultFrozenCount(false, [...lead, ...Array.from({ length: 10 }, () => num())])).toBe(2);
  });

  it('колонка підпису + ведучі нечислові колонки до першої числової', () => {
    expect(defaultFrozenCount(true, wide([text(), text(), num(), text()]), 260)).toBe(3);
    // Без колонки підпису - лише ведучі колонки даних.
    expect(defaultFrozenCount(false, wide([text(), num(), num()]))).toBe(1);
  });

  it('першою йде числова колонка - закріплюється лише колонка підпису (або нічого)', () => {
    expect(defaultFrozenCount(true, wide([num(), text()]), 260)).toBe(1);
    expect(defaultFrozenCount(false, wide([num(), text()]))).toBe(0);
  });

  it('не більше чотирьох колонок даних', () => {
    const probes = wide([text(80), text(80), text(80), text(80), text(80), text(80)]);

    expect(MaxDefaultFrozenDataColumns).toBe(4);
    expect(defaultFrozenCount(false, probes)).toBe(4);
    expect(defaultFrozenCount(true, probes, 100)).toBe(5);
  });

  it('ведучий пробіг покриває ВСІ колонки - лише колонка підпису, а не «усе закріплено»', () => {
    expect(defaultFrozenCount(true, wide([], text), 260)).toBe(1);
    expect(defaultFrozenCount(false, wide([], text))).toBe(0);
  });

  it('закріплена частина не ширша за стелю: надто широкі колонки не закріплюються', () => {
    // 260 (підпис) + 300 = 560 <= 600, ще 300 вже 860 > 600
    expect(defaultFrozenCount(true, wide([text(300), text(300), num()]), 260)).toBe(2);
    // Одна надширока колонка не вміщується зовсім.
    expect(defaultFrozenCount(true, wide([text(500), num()]), 260)).toBe(1);
  });

  it('межа не розрізає групу шапки: відступає до початку групи', () => {
    const probes = wide([text(100, 7), text(100, 7), text(100, 8), text(100, 8), num(100, 9)]);

    expect(defaultFrozenCount(false, probes)).toBe(4); // група 8 ціла (2,3), межа між групами 8 і 9
    const cut = wide([text(100, 7), text(100, 7), text(100, 8), text(100, 8), text(100, 8), num(100, 9)]);
    // 4 колонки даних перерізали б групу 8 (2,3 | 4) -> відступ до початку групи 8
    expect(defaultFrozenCount(false, cut)).toBe(2);
    // Група, що охоплює весь пробіг, відступає до нуля колонок даних (лишається лише підпис).
    expect(defaultFrozenCount(true, wide([text(100, 5), text(100, 5), text(100, 5), text(100, 5), text(100, 5), num()]), 260)).toBe(1);
  });

  it('жодної групи (groupId null) відступу не викликає', () => {
    expect(defaultFrozenCount(false, wide([text(), text(), num()]))).toBe(2);
  });

  it('завжди лишає хоч одну прокручувану колонку', () => {
    expect(defaultFrozenCount(true, [], 260)).toBe(0);
    expect(defaultFrozenCount(true, [num()], 260)).toBe(0);
    expect(defaultFrozenCount(false, [text()])).toBe(0);
  });
});

describe('clampFrozenCount / resolveFrozenCount', () => {
  it('у межах 0..total-1, цілі числа', () => {
    expect(clampFrozenCount(3, 10)).toBe(3);
    expect(clampFrozenCount(10, 10)).toBe(9);
    expect(clampFrozenCount(99, 10)).toBe(9);
    expect(clampFrozenCount(-2, 10)).toBe(0);
    expect(clampFrozenCount(2.9, 10)).toBe(2);
    expect(clampFrozenCount(Number.NaN, 10)).toBe(0);
    expect(clampFrozenCount(3, 1)).toBe(0);
    expect(clampFrozenCount(3, 0)).toBe(0);
  });

  it('вибір користувача перекриває типове, включно з явним 0; без вибору - типове', () => {
    expect(resolveFrozenCount(undefined, 3, 10)).toBe(3);
    expect(resolveFrozenCount(0, 3, 10)).toBe(0);
    expect(resolveFrozenCount(5, 3, 10)).toBe(5);
    expect(resolveFrozenCount(50, 3, 10)).toBe(9);
  });
});

describe('applyFrozenColumns: pin на перші N колонок', () => {
  const columns: ColumnRegular[] = [{ prop: 'a' }, { prop: 'b' }, { prop: 'c' }, { prop: 'd' }];

  it('перші N отримують colPinStart, порядок і решта колонок не змінюються', () => {
    const result = applyFrozenColumns(columns, 2);

    expect(result.map((c) => c.prop)).toEqual(['a', 'b', 'c', 'd']);
    expect(result.map((c) => c.pin)).toEqual(['colPinStart', 'colPinStart', undefined, undefined]);
    // Вхідні об'єкти не мутуються.
    expect(columns.every((c) => c.pin === undefined)).toBe(true);
    // Непіднятих колонок не копіюємо.
    expect(result[2]).toBe(columns[2]);
  });

  it('нуль - ті самі колонки без копіювання', () => {
    expect(applyFrozenColumns(columns, 0)).toBe(columns);
  });
});

describe('вибір у випадному списку', () => {
  it('Default, None, потім «до колонки X» для перших колонок; остання не пропонується', () => {
    const names = ['Label', 'Type', 'Q1', 'Q2'];
    const choices = frozenChoices(names);

    expect(choices.map((c) => c.value)).toEqual([FrozenDefaultChoice, FrozenNoneChoice, '1', '2', '3']);
    expect(choices.find((c) => c.value === '2')?.columnName).toBe('Type');
  });

  it('не більше MaxFrozenChoices пунктів «до колонки»', () => {
    const names = Array.from({ length: 63 }, (_, i) => `C${String(i)}`);
    const values = frozenChoices(names).map((c) => c.value);

    expect(values).toHaveLength(2 + MaxFrozenChoices);
    expect(values.at(-1)).toBe(String(MaxFrozenChoices));
  });

  it('значення <-> вибір користувача', () => {
    expect(frozenChoiceValue(undefined, 10)).toBe('default');
    expect(frozenChoiceValue(0, 10)).toBe('0');
    expect(frozenChoiceValue(3, 10)).toBe('3');
    expect(frozenChoiceValue(30, 10)).toBe('9');

    expect(frozenOverrideOfChoice('default')).toBeUndefined();
    expect(frozenOverrideOfChoice(null)).toBeUndefined();
    expect(frozenOverrideOfChoice('0')).toBe(0);
    expect(frozenOverrideOfChoice('4')).toBe(4);
    expect(frozenOverrideOfChoice('abc')).toBeUndefined();
  });
});

describe('frozenProbesOf / topGroupIdOf', () => {
  const groups = [
    { id: 1, parentId: null },
    { id: 2, parentId: 1 },
    { id: 3, parentId: 2 },
    { id: 4, parentId: null },
  ];

  it('група верхнього рівня за найглибшою групою колонки', () => {
    expect(topGroupIdOf(3, groups)).toBe(1);
    expect(topGroupIdOf(2, groups)).toBe(1);
    expect(topGroupIdOf(4, groups)).toBe(4);
    expect(topGroupIdOf(null, groups)).toBeNull();
    expect(topGroupIdOf(99, groups)).toBeNull();
    expect(topGroupIdOf(3, undefined)).toBeNull();
  });

  it('цикл у батьківських групах не зациклює', () => {
    expect(topGroupIdOf(1, [{ id: 1, parentId: 2 }, { id: 2, parentId: 1 }])).not.toBeUndefined();
  });

  it('числова колонка - Decimal/Int/Formula/Calculated; Lookup і Text - ні', () => {
    const column = (code: string, dataType: string, headerGroupId: number | null = null): ColumnDto =>
      ({ code, dataType, headerGroupId, widthPx: null }) as unknown as ColumnDto;
    const probes = frozenProbesOf(
      [column('a', 'Lookup', 3), column('b', 'Text'), column('c', 'Decimal'), column('d', 'Formula')],
      groups,
      () => 100,
    );

    expect(probes.map((p) => p.numeric)).toEqual([false, false, true, true]);
    expect(probes.map((p) => p.groupId)).toEqual([1, null, null, null]);
    expect(probes[0]?.width).toBe(100);
  });
});
