import { describe, expect, it, vi } from 'vitest';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { cell, Dash, filterSources, sourceTypeLabel, sourceTypeShort } from '../airCardModel';
import { nodeOptions } from '../import/naeNodes';
import { card, tree } from './airFixtures';

vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string) => Catalog[key] ?? `⟦${key}⟧`,
}));

describe('airCardModel', () => {
  it('тип джерела: латинські T/P1 з сервера -> підписи каталогу; невідомий показується як є', () => {
    expect(sourceTypeLabel('T')).toBe(Catalog['permits.air.type.point']);
    expect(sourceTypeLabel('P1')).toBe(Catalog['permits.air.type.diffuse']);
    expect(sourceTypeLabel('X9')).toBe('X9');
    expect(sourceTypeLabel(null)).toBe(Dash);
    expect(sourceTypeShort('P1')).toBe(Catalog['permits.air.type.diffuseShort']);
  });

  it('порожнє значення - прочерк, число рядком - як є (без округлення)', () => {
    expect(cell(null)).toBe(Dash);
    expect(cell('')).toBe(Dash);
    expect(cell('72.3171311')).toBe('72.3171311');
  });

  it('пошук: код НАЕ, назва джерела, код і назва речовини', () => {
    const { sources } = card();
    expect(filterSources(sources, '').length).toBe(2);
    expect(filterSources(sources, '1085').map((s) => s.naeCode)).toEqual(['1085']);
    expect(filterSources(sources, 'wellheads').map((s) => s.naeCode)).toEqual(['6001']);
    expect(filterSources(sources, '0333').map((s) => s.naeCode)).toEqual(['6001']);
    expect(filterSources(sources, 'sulphide').map((s) => s.naeCode)).toEqual(['6001']);
    expect(filterSources(sources, 'nothing')).toEqual([]);
  });
});

describe('nodeOptions', () => {
  it('вузли без джерел, підпис - шлях від кореня', () => {
    expect(nodeOptions(tree().nodes).map((o) => o.label)).toEqual(['Atyrau', 'Atyrau › Offshore', 'Atyrau › Offshore › DC05_Island']);
  });

  it('сирота й цикл не зациклюють', () => {
    const nodes = [
      { ...tree().nodes[0]!, id: 10, parentEntryId: 11, display: 'A' },
      { ...tree().nodes[0]!, id: 11, parentEntryId: 10, display: 'B' },
    ];
    expect(nodeOptions(nodes)).toHaveLength(2);
  });
});
