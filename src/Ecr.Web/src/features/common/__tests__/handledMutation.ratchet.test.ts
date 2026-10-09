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
 * ⚠ Перелік винятків порожній (L9-01/AN-85: `VersionMigrationDialog` і `RegistryImpactPage` отримали
 * `handled`). Перевірка ОДНОБІЧНА свідомо: зайвий рядок у переліку не червоніє. Новий виняток — лише разом
 * із поясненням, чому відмову не можна позначити.
 *
 * ⚠ Дві форми оголошення: `const x = useMutation({…})` (змінна в тому ж файлі) і хук
 * `return useMutation({…})` (L9-01): для другої споживачі шукаються по всьому дереву — `const x = useHook(…)`
 * і `x.error`/`x.isError` у файлі споживача.
 */
const Ledger: ReadonlySet<string> = new Set<string>([]);

const Root = path.resolve(process.cwd(), 'src');

const Declaration = /const\s+(\w+)\s*=\s*useMutation(?:<[^(]*>)?\(\s*\{/g;
const ReturnDeclaration = /return\s+useMutation(?:<[^(]*>)?\(\s*\{/g;

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

/** Текст параметрів `useMutation({…})` від початку збігу до парної `}`. */
function optionsAt(code: string, match: RegExpMatchArray): string {
  let end = (match.index ?? 0) + match[0].length;
  let depth = 1;
  while (depth > 0 && end < code.length) {
    if (code[end] === '{') depth++;
    else if (code[end] === '}') depth--;
    end++;
  }

  return code.slice(match.index, end);
}

/** Імена змін без `onError`/`handled`, чию помилку файл показує сам. */
function unhandledShown(text: string): string[] {
  const code = codeOf(text);
  const found: string[] = [];

  for (const match of code.matchAll(Declaration)) {
    if (/\bonError\b|\bhandled\b/.test(optionsAt(code, match))) continue;

    const name = match[1] ?? '';
    if (new RegExp(String.raw`\b${name}\.(error|isError)\b`).test(code)) found.push(name);
  }

  return found;
}

/** Хуки `function useX() { return useMutation({…}) }` без `onError`/`handled`. */
function silentHooks(text: string): string[] {
  const code = codeOf(text);
  const hooks: string[] = [];

  for (const match of code.matchAll(ReturnDeclaration)) {
    if (/\bonError\b|\bhandled\b/.test(optionsAt(code, match))) continue;

    const before = code.slice(0, match.index ?? 0);
    const hook = [...before.matchAll(/function\s+(use\w+)/g)].at(-1)?.[1];
    if (hook !== undefined) hooks.push(hook);
  }

  return hooks;
}

/** Змінні, у які споживач кладе результат хука, якщо файл читає їхню помилку в рендері. */
function consumersShowing(text: string, hook: string): string[] {
  const code = codeOf(text);
  const shown: string[] = [];

  for (const match of code.matchAll(new RegExp(String.raw`const\s+(\w+)\s*=\s*${hook}\(`, 'g'))) {
    const name = match[1] ?? '';
    if (new RegExp(String.raw`\b${name}\.(error|isError)\b`).test(code)) shown.push(name);
  }

  return shown;
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

  it('хук `return useMutation(` без handled/onError не має споживача, що показує відмову сам', () => {
    const files = sources(Root).map((file) => ({
      relative: path.relative(Root, file).split(path.sep).join('/'),
      text: readFileSync(file, 'utf8'),
    }));
    const failures: string[] = [];

    for (const { relative, text } of files) {
      for (const hook of silentHooks(text)) {
        for (const consumer of files) {
          for (const name of consumersShowing(consumer.text, hook)) {
            failures.push(`${consumer.relative}: ${name} (= ${hook}, ${relative}) — додай \`meta: { handled: true }\` у хук.`);
          }
        }
      }
    }

    expect(failures.join('\n')).toBe('');
  });

  it('сито хуків: бачить німий хук і споживача з ErrorAlert, не бачить позначений', () => {
    const bad = 'export function useSave() {\n  return useMutation({ mutationFn: f });\n}';
    const good = 'export function useSave() {\n  return useMutation({ meta: { handled: true }, mutationFn: f });\n}';
    const consumer = 'const save = useSave();\n{save.error && <ErrorAlert error={save.error} />}';
    const quiet = 'const save = useSave();\nsave.mutate(1);';

    expect(silentHooks(bad)).toEqual(['useSave']);
    expect(silentHooks(good)).toEqual([]);
    expect(consumersShowing(consumer, 'useSave')).toEqual(['save']);
    expect(consumersShowing(quiet, 'useSave')).toEqual([]);
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
