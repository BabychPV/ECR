import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { templatesListQuery, templateVersionsQuery } from '../templateQueries';

/**
 * Сторож PSS-19c: «ключ переліку шаблонів / версій шаблону — один `queryFn`, і він читає ВСІ сторінки».
 *
 * ⛔ Предмет. `queryKeys.templates.list()` і `versionsOf(id)` читав десяток екранів, кожен зі своїм `queryFn` і
 * `?limit=100`. 101-й шаблон чи версія мовчки не існували для вибору (проєкт, грант, вираз, скрипт), а спільний
 * ключ означав, що перелік на одному екрані залежав від того, який екран прочитав його першим. Форма відповіді
 * однакова, тож `tsc` цього не бачить — той самий клас, що `projectsQueryKey.guard.test.ts`.
 *
 * ⚠ Що стереже. (1) Ніде, крім `templateQueries.ts`, ці ключі не стоять поруч із власним `queryFn`; (2) ніде в коді
 * немає читання переліку шаблонів чи версій шаблону з `?limit=` (одна сторінка). Інвалідація й читання кешу
 * (`invalidateQueries`, `breadcrumbResolvers`) — не запити, їх не стосується.
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

/**
 * Ключ шаблонів, за яким у тому самому об'єкті йде `queryFn`; група — тіло `queryFn` до кінця об'єкта (грубо: до
 * наступної `}` верхнього рівня або 400 символів).
 */
const KeyWithFetcher = /queryKey:\s*queryKeys\.templates\.(list|versionsOf)\((?:[^()]|\([^()]*\))*\)\s*,\s*queryFn([\s\S]{0,400})/g;

/**
 * Делегування спільному опису — дозволене: той самий ключ і те саме читання, лише опис підвантажено ледачим
 * `import()` (рецензія L10, M3: `TemplateVersionPage` на межі бюджету `D-132`).
 */
const Delegation = /\b(templatesListQuery|templateVersionsQuery)\b/;

/** Читання однієї сторінки переліку шаблонів чи версій шаблону. */
const SinglePageRead = /\/api\/v1\/templates(\?|\/\$\{[^}]+\}\/versions\?)[^'"`]*limit=/;

export function filesWithOwnTemplatesFetcher(files: Record<string, string>): string[] {
  return Object.entries(files)
    .filter(([file, text]) => {
      if (file.endsWith(`${path.sep}templateQueries.ts`)) return false;

      for (const match of codeOf(text).matchAll(KeyWithFetcher)) {
        const body = (match[2] ?? '').split(/\n\s*\}\)/)[0] ?? '';
        if (!Delegation.test(body)) return true;
      }
      return false;
    })
    .map(([file]) => file);
}

export function filesWithSinglePageTemplatesRead(files: Record<string, string>): string[] {
  return Object.entries(files)
    .filter(([, text]) => SinglePageRead.test(codeOf(text)))
    .map(([file]) => file);
}

function load(): Record<string, string> {
  return Object.fromEntries(sources(Root).map((file) => [file, readFileSync(file, 'utf8')]));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PSS-19c: ключі переліку шаблонів і версій — єдиний опис, усі сторінки', () => {
  it('спільні описи дають ті самі ключі, що й фабрика', () => {
    expect([...templatesListQuery.queryKey]).toEqual([...queryKeys.templates.list()]);
    expect([...templateVersionsQuery(7).queryKey]).toEqual([...queryKeys.templates.versionsOf(7)]);
  });

  it('жоден екран не кладе під ці ключі власний queryFn', () => {
    expect(filesWithOwnTemplatesFetcher(load()).map((f) => path.relative(Root, f))).toEqual([]);
  });

  it('жоден екран не читає перелік шаблонів чи версій однією сторінкою (`?limit=`)', () => {
    expect(filesWithSinglePageTemplatesRead(load()).map((f) => path.relative(Root, f))).toEqual([]);
  });

  it('сторож червоніє на старому шаблоні `?limit=100` під спільним ключем (мутація)', () => {
    const bad = {
      [`${Root}${path.sep}pages${path.sep}X.tsx`]:
        "useQuery({\n  queryKey: queryKeys.templates.list(),\n  queryFn: () => apiFetch<TemplatePage>('/api/v1/templates?limit=100'),\n});",
      [`${Root}${path.sep}pages${path.sep}Y.tsx`]:
        'useQuery({ queryKey: queryKeys.templates.versionsOf(id ?? undefined), queryFn: () =>\n apiFetch(`/api/v1/templates/${String(id)}/versions?limit=100`) });',
    };
    expect(filesWithOwnTemplatesFetcher(bad)).toHaveLength(2);
    expect(filesWithSinglePageTemplatesRead(bad)).toHaveLength(2);

    const good = {
      [`${Root}${path.sep}pages${path.sep}Z.tsx`]:
        'useQuery({ ...templatesListQuery, enabled }); await client.invalidateQueries({ queryKey: queryKeys.templates.list() });',
    };
    expect(filesWithOwnTemplatesFetcher(good)).toEqual([]);
    expect(filesWithSinglePageTemplatesRead(good)).toEqual([]);

    // Делегування спільному опису ледачим `import()` — той самий ключ і читання (TemplateVersionPage, M3).
    const delegating = {
      [`${Root}${path.sep}pages${path.sep}W.tsx`]:
        "useQuery({\n  queryKey: queryKeys.templates.versionsOf(id),\n  queryFn: async (context) =>\n    (await import('@/features/templates/templateQueries')).templateVersionsQuery(id).queryFn(context),\n  enabled,\n});",
    };
    expect(filesWithOwnTemplatesFetcher(delegating)).toEqual([]);
  });

  it('templatesListQuery читає ВСІ сторінки: 101-й шаблон є в кеші', async () => {
    const urls: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        urls.push(url);
        const second = url.includes('cursor=');
        const items = (second ? [101] : Array.from({ length: 100 }, (_, i) => i + 1)).map((id) => ({ id, code: `T${String(id)}` }));

        return new Response(JSON.stringify({ items, nextCursor: second ? null : 'next', totalCount: 101 }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );

    const client = new QueryClient();
    const page = await client.fetchQuery(templatesListQuery);

    expect(page.items).toHaveLength(101);
    expect(page.items.at(-1)?.id).toBe(101);
    expect(page.nextCursor).toBeNull();
    expect(urls).toEqual(['/api/v1/templates?limit=500', '/api/v1/templates?limit=500&cursor=next']);
  });

  it('templateVersionsQuery читає всі версії шаблону', async () => {
    const urls: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        urls.push(url);
        const second = url.includes('cursor=');

        return new Response(
          JSON.stringify({ items: [{ id: second ? 2 : 1 }], nextCursor: second ? null : 'v2', totalCount: null }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }),
    );

    const page = await new QueryClient().fetchQuery(templateVersionsQuery(7));

    expect(page.items.map((version) => version.id)).toEqual([1, 2]);
    expect(urls).toEqual(['/api/v1/templates/7/versions?limit=500', '/api/v1/templates/7/versions?limit=500&cursor=v2']);
  });
});
