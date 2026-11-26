import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { readSeedEnCatalog, readSeedSql } from '@/test/seedSql';

/**
 * ✎ DOCUX: кожен НОВИЙ ключ екрана документа (L1–L9, L4, легенда, «звідки число») є в сіді трьома мовами з однаковими
 * підстановками (`seed/features/DOCUX-document-screen.sql`). Ключ без рядка — `⟦key⟧` на екрані.
 */
const root = process.cwd();
const files = [
  'src/features/grid/needsInput.ts',
  'src/features/grid/needsInputStore.ts',
  'src/features/grid/NeedsInputCounter.tsx',
  'src/features/grid/CellLegend.tsx',
  'src/features/grid/GridKeyHint.tsx',
  'src/features/grid/DocumentGrid.tsx',
  'src/features/documents/inspector/cellProvenance.ts',
  'src/features/documents/inspector/DocumentInspector.tsx',
  'src/features/documents/notices/NoticeLine.tsx',
  'src/features/documents/calcStatus/CalcStatusChip.tsx',
  'src/features/workflow/submitGuide.tsx',
  'src/features/workflow/SheetActions.tsx',
];
const Docux =
  /'((?:grid\.needsInput|grid\.legend|grid\.columnRequiredSomeRows|grid\.keys\.nextRequired|calcStatus|inspector\.info\.(?:calculatedBy|calculatedByStep|readBy|readByRequired)|inspector\.tabStatus|document\.notices\.(?:lineLabel|more|none|goToNext|notSavedRequired|cannotSubmitRequired)|workflow\.recalculate(?:Sheet|All)Approved)[A-Za-z0-9.]*)'/g;

const used = new Set<string>();
for (const file of files) {
  for (const match of readFileSync(path.resolve(root, file), 'utf8').matchAll(Docux)) used.add(match[1] ?? '');
}

const seed = `${readSeedEnCatalog()}\n${readSeedSql()}`;
function text(key: string, lang: 'en' | 'ru' | 'kz'): string | null {
  const escaped = key.replace(/\./g, '\\.');
  const found = new RegExp(`\\(N'${escaped}', N'${lang}', N'((?:[^']|'')*)'`).exec(seed);
  return found?.[1] ?? null;
}
const placeholders = (value: string): string => [...value.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort().join(',');

describe('DOCUX: тексти в сіді', () => {
  it('ключів знайдено (сторож не порожній)', () => {
    expect(used.size).toBeGreaterThanOrEqual(40);
  });

  it.each([...used].sort())('%s — en/ru/kz, ті самі підстановки, en без кирилиці', (key) => {
    const en = text(key, 'en');
    const ru = text(key, 'ru');
    const kz = text(key, 'kz');
    expect(en, `${key} en`).not.toBeNull();
    expect(ru, `${key} ru`).not.toBeNull();
    expect(kz, `${key} kz`).not.toBeNull();
    expect(placeholders(ru ?? '')).toBe(placeholders(en ?? ''));
    expect(placeholders(kz ?? '')).toBe(placeholders(en ?? ''));
    expect(en).not.toMatch(/[Ѐ-ӿ]/);
  });
});
