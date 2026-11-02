import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';

/**
 * Текст сіду — рівно в порядку, у якому його виконує сервер (`SeedScript.Order`):
 * `seed/00-prelude.sql` → `seed/features/*.sql` (за іменем, ordinal) → `seed/99-epilogue.sql`.
 *
 * ⛔ Єдине місце, звідки клієнтські тести читають сід. До поділу кожен відкривав
 * `09-seed.sql` сам; тепер склад частин знає лише цей хелпер (і серверний `SeedScript`).
 * Блоки, які тести шукають першим входженням (`MERGE sys_ecr.UiString AS t` — en-каталог,
 * `MERGE sec.Permission AS t`), лежать у пролозі, тож перше входження — те саме, що й раніше.
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
