import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { readSeedSql } from '@/test/seedSql';

/** PSS-17: ключі тоста збереження частинами — у коді літералом і в сіді трьома мовами з тими самими підстановками. */

const seed = readSeedSql();

const Keys: Record<string, readonly string[]> = {
  'grid.saveParts.progress': ['{saved}', '{total}'],
  'grid.saveParts.done': ['{total}', '{parts}'],
  'grid.saveParts.partial': ['{saved}', '{total}', '{rest}'],
  'grid.saveParts.waiting': ['{saved}', '{total}', '{rest}'],
  'grid.saveParts.stillUnsaved': ['{total}', '{rest}'],
  'grid.saveParts.stopped': ['{saved}', '{total}', '{rest}'],
};

function catalogValue(key: string, lang: 'en' | 'ru' | 'kz'): string | undefined {
  const escaped = key.replace(/\./g, '\\.');
  const pattern =
    lang === 'en'
      ? new RegExp(`\\(N'${escaped}', N'en', N'((?:[^']|'')*)', [01]\\)`, 'g')
      : new RegExp(`\\(N'${escaped}', N'${lang}', N'((?:[^']|'')*)'\\)`, 'g');
  let value: string | undefined;
  for (const match of seed.matchAll(pattern)) value = (match[1] as string).replace(/''/g, "'");

  return value;
}

describe('grid.saveParts.*', () => {
  const source = readFileSync(path.resolve(process.cwd(), 'src', 'features', 'grid', 'patchParts.ts'), 'utf8');

  for (const [key, params] of Object.entries(Keys)) {
    it(`${key}: код просить ключ літералом; en/ru/kz з підстановками ${params.join(' ')}; en без кирилиці`, () => {
      expect(source).toContain(`t('${key}'`);

      for (const lang of ['en', 'ru', 'kz'] as const) {
        const value = catalogValue(key, lang);
        expect(value, `${key} ${lang}`).toBeDefined();
        for (const param of params) expect(value, `${key} ${lang}`).toContain(param);
      }
      expect(catalogValue(key, 'en')).not.toMatch(/[Ѐ-ӿ]/);
    });
  }
});
