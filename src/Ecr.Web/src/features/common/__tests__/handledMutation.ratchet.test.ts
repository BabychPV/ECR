import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Храповик «одна відмова — одне сповіщення» (L9-01).
 *
 * ⛔ Предмет. Глобальна сітка `MutationCache.onError` (`app/queryClient.ts`)
 * кидає тост для кожної зміни без `onError` і без `meta: { handled: true }`.
 * Зміна, що сама показує відмову в рендері (`x.error` / `x.isError` →
 * `ErrorAlert`), без `handled` дає ДВА сповіщення на одну відмову.
 *
 * ⚠ Перелік — лише місця поза зоною цього лейна (сітка документа й сторінка
 * впливу довідника ведуть інші лінії). Перевірка ОДНОБІЧНА свідомо: коли ті
 * лінії доставлять `handled`, рядок стане зайвим, але не червоним — інакше
 * влиття чужої лінії ламало б вершину. Прибирати рядок — разом із фіксом.
 */
const Ledger: ReadonlySet<string> = new Set([
  'features/documents/VersionMigrationDialog.tsx:dryRun',
  'features/documents/VersionMigrationDialog.tsx:apply',
  'features/registries/impact/RegistryImpactPage.tsx:recalculate',
]);

const Root = path.resolve(process.cwd(), 'src');

const Declaration = /const\s+(\w+)\s*=\s*useMutation(?:<[^(]*>)?\(\s*\{/g;

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const full = path.join(dir, name);

    if (statSync(full).isDirectory()) return name === '__tests__' ? [] : sources(full);

    return /\.(ts|tsx)$/.test(name) && !/\.(test|spec)\.tsx?$/.test(name) ? [full] : [];
  });
}

/** Код без коментарів: згадка в поясненні — не місце. */
function codeOf(text: string): string {
  return text.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`\\])\/\/.*$/gm, '$1');
}

/** Імена змін без `onError`/`handled`, чию помилку файл показує сам. */
function unhandledShown(text: string): string[] {
  const code = codeOf(text);
  const found: string[] = [];

  for (const match of code.matchAll(Declaration)) {
    let end = (match.index ?? 0) + match[0].length;
    let depth = 1;
    while (depth > 0 && end < code.length) {
      if (code[end] === '{') depth++;
      else if (code[end] === '}') depth--;
      end++;
    }

    const options = code.slice(match.index, end);
    if (/\bonError\b|\bhandled\b/.test(options)) continue;

    const name = match[1] ?? '';
    if (new RegExp(`\\b${name}\\.(error|isError)\\b`).test(code)) found.push(name);
  }

  return found;
}

describe('L9-01: зміна, що показує відмову сама, позначена handled', () => {
  it('нових місць із подвійним сповіщенням немає', () => {
    const failures: string[] = [];

    for (const file of sources(Root)) {
      const relative = path.relative(Root, file).split(path.sep).join('/');
      for (const name of unhandledShown(readFileSync(file, 'utf8'))) {
        if (!Ledger.has(`${relative}:${name}`)) {
          failures.push(`${relative}: ${name} — додай \`meta: { handled: true }\` (або власний onError).`);
        }
      }
    }

    expect(failures.join('\n')).toBe('');
  });

  it('сито бачить німу зміну з ErrorAlert і не бачить позначену', () => {
    const bad = 'const save = useMutation({ mutationFn: f });\n{save.error && <ErrorAlert error={save.error} />}';
    const good = 'const save = useMutation({ meta: { handled: true }, mutationFn: f });\n{save.error && <X />}';
    const own = 'const save = useMutation({ mutationFn: f, onError: g });\n{save.isError && <X />}';
    const silent = 'const save = useMutation({ mutationFn: f });';

    expect(unhandledShown(bad)).toEqual(['save']);
    expect(unhandledShown(good)).toEqual([]);
    expect(unhandledShown(own)).toEqual([]);
    expect(unhandledShown(silent)).toEqual([]);
  });
});
