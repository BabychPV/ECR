import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { readSeedSql } from '@/test/seedSql';

/**
 * ✎ B155: усе, що екрани маршрутів просять у `t()` під `approvalRoutes.*`, заведено в сіді трьома мовами (en —
 * `features/CATALOG-en-base.sql`, ru/kz — `00-prelude.sql`), рівно по разу, з однаковими підстановками; en без
 * кирилиці, ru/kz перекладені.
 *
 * ⚠ Ключі джерела (`approvalRoutes.source.*`) складаються в коді рядком-шаблоном — тому перелічені тут явно.
 */
const webRoot = process.cwd();
const seed = readSeedSql();

const featureDir = path.resolve(webRoot, 'src', 'features', 'approvalRoutes');
const files = [
  ...readdirSync(featureDir)
    .filter((name) => /\.tsx?$/.test(name))
    .map((name) => path.join(featureDir, name)),
  path.resolve(webRoot, 'src', 'pages', 'admin', 'ProjectSettingsPage.tsx'),
  path.resolve(webRoot, 'src', 'pages', 'admin', 'SecurityPage.tsx'),
  path.resolve(webRoot, 'src', 'pages', 'admin', 'TemplateCardPage.tsx'),
];

const used = new Set<string>(['own', 'template', 'project', 'default', 'none'].map((s) => `approvalRoutes.source.${s}`));
for (const file of files) {
  for (const match of readFileSync(file, 'utf8').matchAll(/'(approvalRoutes(?:\.[A-Za-z0-9]+)+)'/g)) {
    used.add(match[1] ?? '');
  }
}

function value(key: string, lang: 'en' | 'ru' | 'kz'): string[] {
  const escaped = key.replace(/\./g, '\\.');
  const pattern = new RegExp(`\\(N'${escaped}',\\s*N'${lang}',\\s*N'((?:[^']|'')*)'`, 'g');

  return [...seed.matchAll(pattern)].map((m) => (m[1] ?? '').replace(/''/g, "'"));
}

const placeholders = (text: string): string => [...text.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort().join(',');

describe('каталог маршрутів погодження (B155)', () => {
  it('код просить ключі, і кожен заведений рівно раз у en, ru і kz', () => {
    expect(used.size).toBeGreaterThan(30);

    const problems = [...used].flatMap((key) =>
      (['en', 'ru', 'kz'] as const)
        .map((lang) => ({ lang, count: value(key, lang).length }))
        .filter(({ count }) => count !== 1)
        .map(({ lang, count }) => `${key}/${lang}: ${String(count)}`),
    );
    expect(problems).toEqual([]);
  });

  it('підстановки {…} однакові в усіх трьох мовах; en без кирилиці, ru/kz — кирилицею', () => {
    const problems: string[] = [];
    for (const key of used) {
      const [en = ''] = value(key, 'en');
      const [ru = ''] = value(key, 'ru');
      const [kz = ''] = value(key, 'kz');
      if (placeholders(ru) !== placeholders(en) || placeholders(kz) !== placeholders(en)) problems.push(`${key}: {}`);
      if (/[Ѐ-ӿ]/.test(en)) problems.push(`${key}: en кирилицею`);
      if (!/[Ѐ-ӿ]/.test(ru) || !/[Ѐ-ӿ]/.test(kz)) problems.push(`${key}: ru/kz не перекладено`);
    }
    expect(problems).toEqual([]);
  });
});
