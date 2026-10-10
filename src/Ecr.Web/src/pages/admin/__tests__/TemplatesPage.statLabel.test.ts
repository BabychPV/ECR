import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { matchesStat, publishedCount, type TemplateListRow } from '@/features/templates/templateListModel';

/**
 * AN-96: плитка `templates.stat.published` рахує ШАБЛОНИ з опублікованою версією (`publishedCount` =
 * предикат фільтра плитки), а підпис казав «published versions» — число й слово розходились.
 *
 * ⛔ Мутаційний доказ: поверни «published versions» у MERGE (en) або «опубликованных версий» (ru) — падає.
 *
 * ⚠ Підпис читається з САМОГО сіду (тести клієнта показують ключі `⟦…⟧`, не тексти) — той самий прийом, що в
 * `sheet-fill-summary.label.test.ts`.
 */
const seed = readFileSync(
  path.resolve(process.cwd(), '../../src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql'),
  'utf8',
);

function mergeText(lang: 'en' | 'ru'): string {
  const start = seed.indexOf('MERGE sys_ecr.UiString AS t');
  const merge = seed.slice(start);
  const row = new RegExp(`\(N'templates\.stat\.published',\s*N'${lang}',\s*N'([^']*)'`).exec(merge);
  expect(row, `09-seed.sql: немає templates.stat.published (${lang}) після MERGE`).not.toBeNull();

  return row?.[1] ?? '';
}

function rowWith(current: object | null): TemplateListRow {
  return { template: {}, name: 'T', versions: [], current, draft: null, lastDeprecated: null, state: null } as never;
}

describe('плитка «published»: підпис відповідає тому, що рахується', () => {
  it('рахує шаблони, а не версії: два Published-шаблони = 2', () => {
    const rows = [rowWith({ id: 1 }), rowWith({ id: 2 }), rowWith(null)];

    expect(publishedCount(rows)).toBe(2);
    expect(rows.filter((row) => matchesStat(row, 'published'))).toHaveLength(2);
  });

  it('en: не «versions» як одиниця рахунку', () => {
    expect(mergeText('en')).toBe('with a published version');
  });

  it('ru: не «версий» як одиниця рахунку', () => {
    expect(mergeText('ru')).toBe('с опубликованной версией');
  });
});
