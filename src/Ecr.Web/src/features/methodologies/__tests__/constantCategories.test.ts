import { describe, expect, it } from 'vitest';
import type { MethodologyConstantDto } from '@/api/types';
import { t } from '@/shared/i18n';
import type { MethodologyCategoryDto } from '../api';
import {
  SharedFilterValue,
  categoryFilterOptions,
  categoryOptions,
  constantCategoryLabel,
  filterConstantsByCategory,
  mayDeleteCategory,
  mayRetireCategory,
} from '../constantCategories';

/**
 * План CONST-CATEGORY: категорія константи — зі списку версії; фільтр таблиці за категорією.
 *
 * ⚠ Чисті функції: рендер панелі в jsdom дорогий, а саме тут живуть рішення, що мовчки псують дані
 * (зникле поточне значення в полі вибору = константа тихо стає спільною).
 */

function constant(id: number, code: string, category: string | null): MethodologyConstantDto {
  return {
    id,
    code,
    kind: 'Numeric',
    value: 1,
    textValue: null,
    unitId: null,
    category,
    source: null,
    isResolved: true,
    validFrom: null,
    validTo: null,
  } as unknown as MethodologyConstantDto;
}

function category(code: string, over: Partial<MethodologyCategoryDto> = {}): MethodologyCategoryDto {
  return { id: 1, code, description: null, constantCount: 0, isRetired: false, ordinal: 1, ...over };
}

const Rows = [
  constant(1, 'EF_gsec_1325_', 'Loc_BeforeMR_A'),
  constant(2, 'EF_gsec_1325_', 'Loc_BeforeMR_B'),
  constant(3, 'EF_gsec_1325_', null),
  constant(4, 'K', ''),
];

describe('constantCategories', () => {
  it('фільтр: без значення — усі, «спільні» — лише без категорії, код — точний збіг з регістром', () => {
    expect(filterConstantsByCategory(Rows, null).map((r) => r.id)).toEqual([1, 2, 3, 4]);
    expect(filterConstantsByCategory(Rows, SharedFilterValue).map((r) => r.id)).toEqual([3, 4]);
    expect(filterConstantsByCategory(Rows, 'Loc_BeforeMR_B').map((r) => r.id)).toEqual([2]);
    // ⛔ Регістр — частина ключа зіставлення: `loc_beforemr_b` — інша категорія.
    expect(filterConstantsByCategory(Rows, 'loc_beforemr_b')).toEqual([]);
  });

  it('порожня категорія показується «Shared», а не тире', () => {
    expect(constantCategoryLabel(null)).toBe(t('methodologies.categoryShared'));
    expect(constantCategoryLabel('')).toBe(t('methodologies.categoryShared'));
    expect(constantCategoryLabel('Diesel')).toBe('Diesel');
  });

  it('вибір: виведену не пропонує, але ПОТОЧНЕ значення лишає — інакше збереження зробить константу спільною', () => {
    const list = [
      category('B', { ordinal: 2 }),
      category('A', { ordinal: 1 }),
      category('Old', { ordinal: 3, isRetired: true }),
    ];

    expect(categoryOptions(list, '').map((o) => o.value)).toEqual(['A', 'B']);

    const withRetired = categoryOptions(list, 'Old');
    expect(withRetired.map((o) => o.value)).toEqual(['A', 'B', 'Old']);
    expect(withRetired[2]?.label).toContain(t('methodologies.constCategories.retired'));

    // Значення поза переліком (дані до плану) теж видно — сервер назве причину `422`, а не поле мовчки.
    const unknown = categoryOptions(list, 'Typo');
    expect(unknown.at(-1)).toEqual({ value: 'Typo', label: `Typo · ${t('methodologies.constCategories.notInList')}` });
  });

  it('варіанти фільтра: «спільні» першими, далі перелік і коди з констант без дублів', () => {
    const options = categoryFilterOptions([category('Loc_BeforeMR_A')], Rows);
    expect(options.map((o) => o.value)).toEqual([SharedFilterValue, 'Loc_BeforeMR_A', 'Loc_BeforeMR_B']);
  });

  it('прибрати й вивести з ужитку можна лише незайняту категорію (сервер інакше — 409 categoryInUse)', () => {
    expect(mayDeleteCategory(category('A'))).toBe(true);
    expect(mayDeleteCategory(category('A', { constantCount: 3 }))).toBe(false);
    expect(mayRetireCategory(category('A'))).toBe(true);
    expect(mayRetireCategory(category('A', { constantCount: 1 }))).toBe(false);
    expect(mayRetireCategory(category('A', { isRetired: true }))).toBe(false);
  });
});
