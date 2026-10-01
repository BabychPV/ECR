import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Храповик сирих текстів відмови на клієнті (`ФВ-14.9a`, `Q-341`).
 *
 * ⛔ Предмет. `EcrApiError.message` — це `detail ?? title` БЕЗ розбору мови:
 * для відмови без `messageKey` це речення розробника українською — мовою, якої
 * в продукті немає (`D-95`: en, ru, kz). Показувати можна лише те, що пропустив
 * `problemText` (`ErrorAlert`, `showApiError`, `problemText(…).detail`).
 * Серверну половину боргу стереже `MessageKeyRatchetTests` (Architecture);
 * ця — клієнтська: місця, де екран бере текст відмови ПОВЗ розбір.
 *
 * ⚠ Чому перелік файлів із числом, а не заборона. Частина входжень законна:
 * технічна подробиця під розгортанням (`RenderErrorScreen`), сам розбір
 * (`problemText`), повідомлення ПЕРЕВІРКИ, яке сервер уже віддав мовою
 * користувача. Заборона зробила б їх усіх червоними; перелік фіксує замір і
 * не дає йому рости. Перевірка йде В ОБИДВА БОКИ, як у серверного сторожа:
 * число, яке стало меншим, теж червоне — борг звужується лише записом.
 *
 * ⚠ Сито текстове і грубе свідомо: воно рахує `<щось>error.message`,
 * `<щось>Error.message` і `String(<щось>error)` у коді без коментарів. Воно
 * не бачить деструктуризації (`const { message } = error`) — це межа заміру,
 * названа тут, а не прихована.
 */

/** Замір: файл (від `src/`) → скільки входжень дозволено. */
const Ledger: Readonly<Record<string, number>> = {
  // Технічна подробиця під розгортанням — не для читання, для звернення.
  'app/RenderErrorScreen.tsx': 1,
  // Сам розбір: `suppressed` іде в діагностику, не на екран.
  'shared/ui/problemText.ts': 1,
};

const Root = path.resolve(process.cwd(), 'src');

const Site = /\b\w*[eE]rror\??\.message\b|String\(\s*(?:[\w.?]+\.)?\w*[eE]rror\s*\)/g;

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const full = path.join(dir, name);

    if (statSync(full).isDirectory()) return name === '__tests__' ? [] : sources(full);

    return /\.(ts|tsx)$/.test(name) && !/\.(test|spec)\.tsx?$/.test(name) && !name.endsWith('.d.ts') ? [full] : [];
  });
}

/** Код без коментарів: згадка дефекту в поясненні — не дефект. */
function codeOf(file: string): string {
  return readFileSync(file, 'utf8')
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/(^|[^:'"`\\])\/\/.*$/gm, '$1');
}

function measure(): Map<string, number> {
  const found = new Map<string, number>();

  for (const file of sources(Root)) {
    const count = codeOf(file).match(Site)?.length ?? 0;

    if (count > 0) found.set(path.relative(Root, file).split(path.sep).join('/'), count);
  }

  return found;
}

describe('ФВ-14.9a: сирий текст відмови повз problemText не стає частішим', () => {
  it('замір збігається з переліком в обидва боки', () => {
    const actual = measure();
    const failures: string[] = [];

    for (const [file, count] of [...actual].sort()) {
      const allowed = Ledger[file] ?? 0;

      if (count > allowed) {
        failures.push(
          `${file}: сирих текстів відмови ${count}, дозволено ${allowed}. ` +
            'Покажи `problemText(error).detail ?? problemText(error).title`, `refusalText(error)` (grid) або `<ErrorAlert error={…} />`.',
        );
      } else if (count < allowed) {
        failures.push(`${file}: лишилось ${count}, а перелік обіцяє ${allowed} — зменш число в Ledger.`);
      }
    }

    for (const [file, allowed] of Object.entries(Ledger)) {
      if (!actual.has(file)) failures.push(`${file}: у переліку ${allowed}, а входжень немає — прибери рядок.`);
    }

    expect(failures.join('\n')).toBe('');
  });

  it('сито бачить усі три форми й не бачить коментар', () => {
    const sample = [
      '<Text>{remove.error?.message}</Text>',
      'setSaveError(error.message);',
      'setSaveError(String(error));',
      'show(String(usage.error));',
      '// error.message у поясненні',
      'const text = problemText(error).detail;',
    ].join('\n');

    expect(sample.replace(/(^|[^:'"`\\])\/\/.*$/gm, '$1').match(Site)).toHaveLength(4);
  });
});
