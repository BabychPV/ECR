import { describe, expect, it, vi } from 'vitest';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import {
  fieldLabel,
  parsePeriodParam,
  periodOptions,
  usageHref,
  usageKey,
  wherePrimary,
  whereSecondary,
} from '../usageModel';
import { cellItem, headerItem } from './usageFixtures';

/**
 * Модель вкладки «Used in» (PERMIT-USAGE): адреса місця, підписи «де»/«поле», фільтр періоду. Тексти — справжні, із сіду.
 *
 * Мутаційні докази (виконано вручну, відновлено): у `usageHref` без `sheet` у параметрах валиться «комірка веде на
 * період, аркуш і таблицю»; у `usageKey` без `rowKey` валиться «два рядки однієї колонки — різні ключі»; у
 * `periodOptions` сортування `a - b` валить «від нового до старого».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

describe('usageModel', () => {
  it('шапка веде на документ без параметрів; комірка — на період, аркуш і таблицю', () => {
    expect(usageHref(headerItem())).toBe('/documents/2');
    expect(usageHref(cellItem())).toBe('/documents/2?periodKey=202606&sheet=WATER&table=USE');
  });

  it('коди з пробілами й символами кодуються в адресі', () => {
    expect(usageHref(cellItem({ sheetCode: 'A B', tableCode: 'T&1' }))).toBe('/documents/2?periodKey=202606&sheet=A+B&table=T%261');
  });

  it('«де»: шапка — «Document header»; комірка — «аркуш · таблиця» і «Row N»', () => {
    expect(wherePrimary(headerItem())).toBe('Document header');
    expect(whereSecondary(headerItem())).toBeNull();
    expect(wherePrimary(cellItem())).toBe('Water · Water use');
    expect(whereSecondary(cellItem())).toBe('Row 3');
  });

  it('без назв аркуша й таблиці — коди; без номера рядка — ключ рядка', () => {
    const bare = cellItem({ sheetNameL10n: null, tableNameL10n: { en: '' }, rowOrdinal: null });
    expect(wherePrimary(bare)).toBe('WATER · USE');
    expect(whereSecondary(bare)).toBe('R3');
  });

  it('підпис поля мовою користувача, без підпису — код', () => {
    expect(fieldLabel(headerItem())).toBe('Permit number');
    expect(fieldLabel(cellItem({ fieldLabelL10n: {} }))).toBe('PCOL');
  });

  it('два рядки однієї колонки — різні ключі; шапка і комірка того самого документа — теж', () => {
    const keys = new Set([usageKey(cellItem()), usageKey(cellItem({ rowKey: 'R4', rowOrdinal: 4 })), usageKey(headerItem())]);
    expect(keys.size).toBe(3);
  });

  it('варіанти періоду — унікальні, без шапки (null), від нового до старого', () => {
    const options = periodOptions([
      headerItem(),
      cellItem({ periodKey: 202605 }),
      cellItem({ periodKey: 202606 }),
      cellItem({ periodKey: 202606, rowKey: 'R4' }),
    ]);
    expect(options.map((option) => option.value)).toEqual(['202606', '202605']);
    expect(options[0]?.label).toMatch(/2026/);
  });

  it('період з адреси — лише ціле; інакше фільтра немає', () => {
    expect(parsePeriodParam('202606')).toBe(202606);
    expect(parsePeriodParam(null)).toBeNull();
    expect(parsePeriodParam('abc')).toBeNull();
    expect(parsePeriodParam('2026-06')).toBeNull();
  });
});
