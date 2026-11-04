import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';

/**
 * Текст сіду — рівно в порядку, у якому його виконує сервер (`SeedScript.Order`):
 * `seed/00-prelude.sql` → `seed/features/*.sql` (за іменем, ordinal) → `seed/99-epilogue.sql`.
 *
 * ⛔ Єдине місце, звідки клієнтські тести читають сід. До поділу кожен відкривав
 * `09-seed.sql` сам; тепер склад частин знає лише цей хелпер (і серверний `SeedScript`).
 * Блоки, які тести шукають першим входженням (`MERGE sec.Permission AS t`), лежать у пролозі.
 * ⛔ en-каталог — НЕ один блок `MERGE sys_ecr.UiString AS t`: його рядки лежать порціями
 * `INSERT INTO #UiEn … OPTION (RECOMPILE);` у `seed/features/*.sql` (lane integ-seed-uien).
 * Рядки каталогу тести беруть лише через `readSeedEnCatalog()` / `enCatalogOf()`.
 *
 * ⚠ Від `process.cwd()` = `src/Ecr.Web`, як і решта тестів, що читають файли дерева.
 */
export const seedDir = path.resolve(process.cwd(), '..', 'Ecr.Infrastructure', 'Persistence', 'Sql', 'seed');

let cached: string | undefined;

export function readSeedSql(): string {
  if (cached !== undefined) return cached;

  const read = (file: string): string => {
    const text = readFileSync(file, 'utf8').replace(/^﻿/, '');
    return text.endsWith('\n') ? text : `${text}\n`;
  };

  const features = readdirSync(path.join(seedDir, 'features'))
    .filter((name) => name.endsWith('.sql'))
    // Ordinal, як StringComparer.Ordinal на сервері (не localeCompare).
    .sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));

  cached = [
    read(path.join(seedDir, '00-prelude.sql')),
    ...features.map((name) => read(path.join(seedDir, 'features', name))),
    read(path.join(seedDir, '99-epilogue.sql')),
  ].join('');
  return cached;
}

/** Початок порції en-каталогу (з дужкою — не плутати з `INSERT INTO #UiEnCounts`). */
export const EN_CATALOG_INSERT = 'INSERT INTO #UiEn ([Key]';

/**
 * Рядки en-каталогу: усі порції `INSERT INTO #UiEn` тексту сіду (до `OPTION (RECOMPILE)`), склеєні
 * в порядку виконання. Рядок — `(N'ключ', N'en', N'текст', область)`, як у колишньому VALUES en-MERGE.
 * Те саме, що `SeedSql.EnCatalogOf` на сервері.
 */
export function enCatalogOf(seed: string): string {
  const parts: string[] = [];
  let pos = seed.indexOf(EN_CATALOG_INSERT);
  while (pos >= 0) {
    const stop = seed.indexOf('OPTION (RECOMPILE)', pos);
    if (stop < 0) throw new Error(`seed: порція ${EN_CATALOG_INSERT} не закінчується OPTION (RECOMPILE)`);
    parts.push(seed.slice(pos, stop));
    pos = seed.indexOf(EN_CATALOG_INSERT, stop);
  }
  return parts.join('\n');
}

let cachedEn: string | undefined;

/** en-каталог зведеного сіду; порожній — виняток (тест дивився б не туди). */
export function readSeedEnCatalog(): string {
  if (cachedEn !== undefined) return cachedEn;
  const block = enCatalogOf(readSeedSql());
  if (block.length === 0) throw new Error(`seed: немає порцій en-каталогу (${EN_CATALOG_INSERT})`);
  cachedEn = block;
  return cachedEn;
}
