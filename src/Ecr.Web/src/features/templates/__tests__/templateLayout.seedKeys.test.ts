import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { readSeedSql } from '@/test/seedSql';

/**
 * ✎ D-474: кожен ключ `template.layout.*`, який просить «Layout» версії, є в сіді (`seed/features/TMPL-layout.sql`)
 * усіма трьома мовами — інакше на екрані `⟦template.layout.…⟧`.
 */
const control = path.resolve(process.cwd(), 'src', 'features', 'templates', 'TemplateLayoutControl.tsx');

describe('каталог «Layout» версії шаблону', () => {
  it('ключі контролу є в сіді en, ru і kz', () => {
    const seed = readSeedSql();
    const used = new Set([...readFileSync(control, 'utf8').matchAll(/template\.layout\.[A-Za-z]+/g)].map((m) => m[0]));

    expect(used.size).toBe(5);
    const missing = [...used].flatMap((key) =>
      (['en', 'ru', 'kz'] as const).filter((lang) => !seed.includes(`(N'${key}', N'${lang}', N'`)).map((lang) => `${key}:${lang}`),
    );
    expect(missing).toEqual([]);
  });
});
