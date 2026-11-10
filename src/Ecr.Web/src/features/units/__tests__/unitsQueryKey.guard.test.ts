import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { queryKeys } from '@/api/queryKeys';

/**
 * Сторож «ключ каталогу одиниць — лише з фабрики» (D13).
 *
 * ⛔ Предмет. Ключ `['units']` і його префіксні форми (`['units', id, 'detail' | 'usage']`) були літералами
 * в дванадцяти файлах. `UnitsPage` і `UnitEditModal` інвалідовують каталог за префіксом `['units']`; варто
 * одному екрану взяти іншу форму — і після збереження одиниці він мовчки показує застарілий перелік. `tsc`
 * цього не бачить: масив рядків є масивом рядків.
 *
 * ⚠ Що стереже. Літерал масиву, першим елементом якого стоїть `'units'`, живе лише в `api/queryKeys.ts`.
 * Коментарі й тести не рахуються (згадка в поясненні — не місце; тести читають кеш навмисно).
 */
const Root = path.resolve(process.cwd(), 'src');

function sources(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const full = path.join(dir, name);

    if (statSync(full).isDirectory()) return name === '__tests__' ? [] : sources(full);

    return /\.(ts|tsx)$/.test(name) && !/\.(test|spec)\.tsx?$/.test(name) && name !== 'schema.d.ts' ? [full] : [];
  });
}

/** Код без коментарів: згадка в поясненні — не місце. */
function codeOf(text: string): string {
  return text.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:'"`\\])\/\/.*$/gm, '$1');
}

const UnitsLiteral = /\[\s*['"`]units['"`]\s*[,\]]/;

/** Файли, де ключ одиниць задано літералом `['units'…]` (без коментарів). */
export function filesWithBareUnitsKey(files: Record<string, string>): string[] {
  return Object.entries(files)
    .filter(([file, text]) => !file.endsWith(`${path.sep}queryKeys.ts`) && UnitsLiteral.test(codeOf(text)))
    .map(([file]) => file);
}

function load(): Record<string, string> {
  return Object.fromEntries(sources(Root).map((file) => [file, readFileSync(file, 'utf8')]));
}

describe('ключ кешу одиниць — єдиний опис (D13)', () => {
  it('фабрика дає префікс `units` для каталогу, подробиць і вживання', () => {
    expect([...queryKeys.units.all()]).toEqual(['units']);
    expect([...queryKeys.units.detail(7)]).toEqual(['units', 7, 'detail']);
    expect([...queryKeys.units.usage(7)]).toEqual(['units', 7, 'usage']);
  });

  it('літерал `[\'units\'…]` поза api/queryKeys.ts відсутній', () => {
    expect(filesWithBareUnitsKey(load()).map((f) => path.relative(Root, f))).toEqual([]);
  });

  it('сторож червоніє на літералі каталогу й подробиць, але не на коментарі (мутація)', () => {
    const at = (name: string): string => `${Root}${path.sep}pages${path.sep}${name}`;

    expect(filesWithBareUnitsKey({ [at('A.tsx')]: "useQuery({ queryKey: ['units'], queryFn });" })).toHaveLength(1);
    expect(
      filesWithBareUnitsKey({ [at('B.tsx')]: 'useQuery({ queryKey: ["units", id, "usage"], queryFn });' }),
    ).toHaveLength(1);
    expect(
      filesWithBareUnitsKey({ [at('C.tsx')]: "// кеш `['units']` спільний\nuseQuery({ queryKey: queryKeys.units.all() });" }),
    ).toEqual([]);
    expect(filesWithBareUnitsKey({ [at('D.tsx')]: "const x = ['unitsOfMeasure'];" })).toEqual([]);
  });
});
