import { describe, it, expect, vi, afterEach } from 'vitest';
import { fetchAllProjects } from '@/features/projects/allProjects';

/**
 * `X-07`: вибір проєкту брав `?limit=200` і показував те, що прийшло, — 201-й
 * проєкт не існував для вибору, і ніщо про це не казало.
 */

const project = (id: number) => ({
  id, code: `P${String(id)}`, status: 'Active', periodKind: 'Monthly', periodCount: 12, currentPeriodId: null, timeZoneId: 'UTC',
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('fetchAllProjects', () => {
  it('іде курсором до кінця й зводить усі сторінки в одну', async () => {
    const urls: string[] = [];

    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        urls.push(url);

        const body = url.includes('cursor=c2')
          ? { items: [project(3)], nextCursor: null, totalCount: 3 }
          : { items: [project(1), project(2)], nextCursor: 'c2', totalCount: 3 };

        return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }),
    );

    const all = await fetchAllProjects();

    // ⛔ Мутація «одна сторінка» (стара поведінка) лишає тут два проєкти.
    expect(all.items.map((p) => p.id)).toEqual([1, 2, 3]);
    expect(all.nextCursor).toBeNull();
    expect(urls).toEqual(['/api/v1/projects?limit=500', '/api/v1/projects?limit=500&cursor=c2']);
  });
});
