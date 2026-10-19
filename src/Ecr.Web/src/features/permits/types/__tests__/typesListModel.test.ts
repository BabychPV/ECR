import { describe, expect, it, vi } from 'vitest';
import {
  categoryLabel,
  categoryOptions,
  filterTypes,
  hasDraftChanges,
  listStats,
  matchesNeedle,
  tablesText,
  typeName,
  typeSubtitle,
} from '../typesListModel';
import { FixtureTypes, makeType } from './typesFixture';

vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string) => key,
}));

/**
 * Модель переліку типів (PM-08a): назви, стани, показники, фільтри.
 *
 * Мутаційний доказ (вручну, відновлено): `hasDraftChanges` без `|| status === 'Draft'` валить «стани» і
 * «показники»; `listStats.tables` рахує типи замість таблиць — валить «показники смуги»; `filterTypes`
 * без перевірки категорії валить «категорія»; `categoryOptions` без `present.has('Other')` валить «Other лише коли є».
 */
describe('typeName / typeSubtitle', () => {
  it('назва мовою інтерфейсу, запасна — англійська, потім будь-яка, потім код', () => {
    const [air] = FixtureTypes;
    expect(typeName(air!, 'ru')).toBe('Разрешение на выбросы');
    expect(typeName(air!, 'en')).toBe('Air emission permit');
    expect(typeName(air!, 'kz')).toBe('Air emission permit');
    expect(typeName(makeType('Raw', { nameL10n: {} }), 'en')).toBe('Raw');
    expect(typeName(makeType('Only', { nameL10n: { kz: 'Тек' } }), 'en')).toBe('Тек');
  });

  it('підзаголовок — російська назва під англійською, і навпаки; без другої мови — код', () => {
    const [air, , waste] = FixtureTypes;
    expect(typeSubtitle(air!, 'en')).toBe('Разрешение на выбросы');
    expect(typeSubtitle(air!, 'ru')).toBe('Air emission permit');
    expect(typeSubtitle(waste!, 'en')).toBe('WasteStorage');
  });

  it('підзаголовок не повторює основну назву', () => {
    const same = makeType('Same', { nameL10n: { en: 'Noise', ru: 'Noise' } });
    expect(typeSubtitle(same, 'en')).toBe('Same');
  });
});

describe('стани й показники', () => {
  it('стани: опублікований без змін — ні; зі змінами в чернетці — так; ніколи не публікувався — так', () => {
    const [air, water, waste] = FixtureTypes;
    expect(hasDraftChanges(air!)).toBe(false);
    expect(hasDraftChanges(water!)).toBe(true);
    expect(hasDraftChanges(waste!)).toBe(true);
    expect(hasDraftChanges(makeType('X', { status: 'Draft' }))).toBe(true);
  });

  it('показники смуги: типи, таблиці, зі змінами', () => {
    expect(listStats(FixtureTypes)).toEqual({ types: 3, tables: 4, draft: 2 });
    expect(listStats([])).toEqual({ types: 0, tables: 0, draft: 0 });
  });
});

describe('tablesText', () => {
  it('таблиці через « · » з кількістю стовпців, код замість порожньої назви', () => {
    const [air] = FixtureTypes;
    expect(tablesText(air!, 'en')).toBe('Emission limits (6) · Sources (4)');
    const bare = makeType('B', { tables: [{ code: 'Raw', nameL10n: {}, columnCount: 2 }] });
    expect(tablesText(bare, 'en')).toBe('Raw (2)');
    expect(tablesText(makeType('E'), 'en')).toBe('');
  });
});

describe('пошук і фільтри', () => {
  it('пошук за назвою будь-якою мовою й за кодом, без регістру', () => {
    const [air] = FixtureTypes;
    expect(matchesNeedle(air!, 'AIR EMISSION')).toBe(true);
    expect(matchesNeedle(air!, 'выбросы')).toBe(true);
    expect(matchesNeedle(air!, 'airemission')).toBe(true);
    expect(matchesNeedle(air!, 'noise')).toBe(false);
    expect(matchesNeedle(air!, '   ')).toBe(true);
  });

  it('категорія', () => {
    expect(filterTypes(FixtureTypes, { needle: '', category: 'Water', stat: null }).map((i) => i.code)).toEqual([
      'WaterDischarge',
    ]);
  });

  it('показник «зі змінами» лишає лише типи з чернеткою; решта показників не фільтрує', () => {
    expect(filterTypes(FixtureTypes, { needle: '', category: '', stat: 'draft' }).map((i) => i.code)).toEqual([
      'WaterDischarge',
      'WasteStorage',
    ]);
    expect(filterTypes(FixtureTypes, { needle: '', category: '', stat: 'tables' })).toHaveLength(3);
  });

  it('пошук, категорія й показник діють разом', () => {
    expect(filterTypes(FixtureTypes, { needle: 'waste', category: 'Waste', stat: 'draft' })).toHaveLength(1);
    expect(filterTypes(FixtureTypes, { needle: 'waste', category: 'Air', stat: null })).toHaveLength(0);
  });
});

describe('категорії', () => {
  it('три категорії макета; Other — лише коли такі типи є; чужа категорія не губиться', () => {
    expect(categoryOptions(FixtureTypes)).toEqual(['Air', 'Water', 'Waste']);
    expect(categoryOptions([...FixtureTypes, makeType('O', { category: 'Other' })])).toEqual([
      'Air',
      'Water',
      'Waste',
      'Other',
    ]);
    expect(categoryOptions([makeType('N', { category: 'Noise' })])).toEqual(['Air', 'Water', 'Waste', 'Noise']);
  });

  it('підпис категорії: відома — словом каталогу, невідома — як прийшла', () => {
    expect(categoryLabel('Air')).toBe('permits.category.Air');
    expect(categoryLabel('Noise')).toBe('Noise');
  });
});
