import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { queryKeys } from '@/api/queryKeys';
import { projectsQuery } from '@/features/projects/allProjects';

/**
 * Сторож «один ключ `['projects']` — один `queryFn`» (AN-91 / L8-10, = L9-15).
 *
 * ⛔ Предмет. Ключ `['projects']` читали шість екранів, кожен зі своїм `queryFn`. Один просив `?limit=200` і клав
 * обрізаний перелік у ТОЙ САМИЙ запис кешу, що й решта: проєкт поза першою сторінкою «зникав», і блокування
 * архіву не спрацьовувало. Форма відповіді однакова, тож `tsc` цього не бачить.
 *
 * ⚠ Що стереже. (1) Літерал `['projects']` (рівно один елемент) живе лише в `api/queryKeys.ts`; (2) кожен файл, що
 * бере `queryKeys.projects.all()` як `queryKey`, бере `queryFn` лише з `fetchAllProjects` — прямо, через
 * `projectsQuery` або ледачим `import()`. Префіксні ключі (`['projects', id, 'document-template']`) — інша форма,
 * їх не стосується.
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

const BareLiteral = /\[\s*['"]projects['"]\s*\]/;
const KeyFromFactory = /queryKeys\.projects\.all\(\)/;
const SharedQuery = /\bprojectsQuery\b/;
const SharedFetcher = /\bfetchAllProjects\b/;

/** Файли, де ключ проєктів задано літералом `['projects']` (без коментарів). */
export function filesWithBareProjectsKey(files: Record<string, string>): string[] {
  return Object.entries(files)
    .filter(([file, text]) => !file.endsWith(`${path.sep}queryKeys.ts`) && BareLiteral.test(codeOf(text)))
    .map(([file]) => file);
}

/** Файли, що беруть ключ із фабрики, але не мають `fetchAllProjects` / `projectsQuery` поруч. */
export function filesWithForeignFetcher(files: Record<string, string>): string[] {
  return Object.entries(files)
    .filter(([file, text]) => {
      if (file.endsWith(`${path.sep}queryKeys.ts`)) return false;

      const code = codeOf(text);

      return KeyFromFactory.test(code) && !SharedQuery.test(code) && !SharedFetcher.test(code);
    })
    .map(([file]) => file);
}

function load(): Record<string, string> {
  return Object.fromEntries(sources(Root).map((file) => [file, readFileSync(file, 'utf8')]));
}

describe('ключ кешу проєктів — єдиний опис (L8-10)', () => {
  it('фабрика й спільний запит дають один і той самий ключ', () => {
    expect([...projectsQuery.queryKey]).toEqual(['projects']);
    expect([...queryKeys.projects.all()]).toEqual(['projects']);
  });

  it('літерал `[\'projects\']` поза api/queryKeys.ts відсутній', () => {
    expect(filesWithBareProjectsKey(load()).map((f) => path.relative(Root, f))).toEqual([]);
  });

  it('кожен екран із ключем проєктів бере queryFn лише з fetchAllProjects', () => {
    expect(filesWithForeignFetcher(load()).map((f) => path.relative(Root, f))).toEqual([]);
  });

  it('сторож червоніє на обрізаному `?limit=200` під ключем проєктів (мутація)', () => {
    const bad = {
      [`${Root}${path.sep}pages${path.sep}X.tsx`]:
        "useQuery({ queryKey: ['projects'], queryFn: () => apiFetch('/api/v1/projects?limit=200') });",
    };
    expect(filesWithBareProjectsKey(bad)).toHaveLength(1);

    const viaFactory = {
      [`${Root}${path.sep}pages${path.sep}Y.tsx`]:
        "useQuery({ queryKey: queryKeys.projects.all(), queryFn: () => apiFetch('/api/v1/projects?limit=200') });",
    };
    expect(filesWithForeignFetcher(viaFactory)).toHaveLength(1);

    const good = {
      [`${Root}${path.sep}pages${path.sep}Z.tsx`]: 'useQuery({ ...projectsQuery, enabled });',
    };
    expect(filesWithBareProjectsKey(good)).toEqual([]);
    expect(filesWithForeignFetcher(good)).toEqual([]);
  });
});
