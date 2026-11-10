import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { readSeedSql } from '@/test/seedSql';

/** Живий прохід 10.11: новий ключ банера заборонених комірок — у коді літералом і в сіді трьома мовами. */

const seed = readSeedSql();
const key = 'grid.deniedCellsSummary';

function catalogValue(lang: 'en' | 'ru' | 'kz'): string | undefined {
  const escaped = key.replace(/\./g, '\\.');
  const pattern =
    lang === 'en'
      ? new RegExp(`\\(N'${escaped}', N'en', N'((?:[^']|'')*)', [01]\\)`, 'g')
      : new RegExp(`\\(N'${escaped}', N'${lang}', N'((?:[^']|'')*)'\\)`, 'g');
  let value: string | undefined;
  for (const match of seed.matchAll(pattern)) value = (match[1] as string).replace(/''/g, "'");

  return value;
}

describe('grid.deniedCellsSummary', () => {
  it('код просить ключ літералом', () => {
    const source = readFileSync(path.resolve(process.cwd(), 'src', 'features', 'grid', 'DocumentGrid.tsx'), 'utf8');
    expect(source).toContain(`t('${key}'`);
  });

  it('є en/ru/kz з підстановкою {count}; en без кирилиці', () => {
    for (const lang of ['en', 'ru', 'kz'] as const) {
      expect(catalogValue(lang), lang).toMatch(/\{count\}/);
    }
    expect(catalogValue('en')).not.toMatch(/[Ѐ-ӿ]/);
  });
});
