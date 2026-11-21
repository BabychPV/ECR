import { afterEach, describe, expect, it, vi } from 'vitest';
import { AllPagesMaxPages, fetchAllPages } from '../fetchAllPages';

/**
 * PSS-19c: спільний прохід курсорними сторінками — усі сторінки, межа, скасування.
 *
 * Мутаційні докази (не в коміті): прибрати `cursor` із наступного запиту → червоний «усі сторінки» (вічна
 * перша сторінка до запобіжника); прибрати перевірку повтору курсора → червоний «курсор, що не рухається»
 * (20 запитів замість 2).
 */

const urls: string[] = [];

function serve(pages: Record<string, { items: number[]; nextCursor: string | null; totalCount?: number | null }>): void {
  urls.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.signal?.aborted === true) throw new DOMException('aborted', 'AbortError');
      const url = String(input);
      urls.push(url);
      const cursor = new URL(url, 'http://x').searchParams.get('cursor') ?? '';
      const page = pages[cursor] ?? { items: [], nextCursor: null };

      return new Response(JSON.stringify({ totalCount: null, ...page }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PSS-19c: fetchAllPages', () => {
  it('усі сторінки по черзі, 500 на сторінку, курсор наступної — у запиті; форма однієї сторінки', async () => {
    serve({
      '': { items: [1, 2], nextCursor: 'a', totalCount: 5 },
      a: { items: [3, 4], nextCursor: 'b' },
      b: { items: [5], nextCursor: null },
    });

    const all = await fetchAllPages<number>('/api/v1/templates');

    expect(all).toEqual({ items: [1, 2, 3, 4, 5], nextCursor: null, totalCount: 5 });
    expect(urls).toEqual([
      '/api/v1/templates?limit=500',
      '/api/v1/templates?limit=500&cursor=a',
      '/api/v1/templates?limit=500&cursor=b',
    ]);
  });

  it('власні параметри адреси зберігаються (`&limit`, не другий `?`)', async () => {
    serve({ '': { items: [1], nextCursor: null } });

    await fetchAllPages<number>('/api/v1/documents?periodKey=202609');

    expect(urls).toEqual(['/api/v1/documents?periodKey=202609&limit=500']);
  });

  it('запобіжник: після maxPages зупиняється і чесно віддає nextCursor (перелік неповний)', async () => {
    const endless = Object.fromEntries(
      Array.from({ length: AllPagesMaxPages + 5 }, (_, i) => [i === 0 ? '' : `c${String(i)}`, { items: [i], nextCursor: `c${String(i + 1)}` }]),
    );
    serve(endless);

    const all = await fetchAllPages<number>('/api/v1/users');

    expect(urls).toHaveLength(AllPagesMaxPages);
    expect(all.items).toHaveLength(AllPagesMaxPages);
    expect(all.nextCursor).toBe(`c${String(AllPagesMaxPages)}`);
  });

  it('курсор, що не рухається: зупинка, а не вічний цикл; nextCursor не null', async () => {
    serve({ '': { items: [1], nextCursor: 'same' }, same: { items: [2], nextCursor: 'same' } });

    const all = await fetchAllPages<number>('/api/v1/users');

    expect(urls).toHaveLength(2);
    expect(all).toEqual({ items: [1, 2], nextCursor: 'same', totalCount: null });
  });

  it('курсор колом A→B→A і перекриття сторінок: той самий запис (за `id`) не додається вдруге', async () => {
    urls.length = 0;
    const page = (ids: number[], nextCursor: string | null): Response =>
      new Response(JSON.stringify({ items: ids.map((id) => ({ id })), nextCursor, totalCount: null }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        urls.push(url);
        const cursor = new URL(url, 'http://x').searchParams.get('cursor');
        if (cursor === null) return page([1, 2], 'A');
        if (cursor === 'A') return page([2, 3], 'B');
        return page([3, 1], 'A');
      }),
    );

    const all = await fetchAllPages<{ id: number }>('/api/v1/users');

    expect(all.items.map((item) => item.id)).toEqual([1, 2, 3]);
    expect(all.nextCursor).toBe('A');
    expect(urls).toHaveLength(3);
  });

  it('скасування: `signal` доходить до запиту — покинутий екран не дочитує решту', async () => {
    serve({ '': { items: [1], nextCursor: 'a' }, a: { items: [2], nextCursor: null } });
    const controller = new AbortController();
    controller.abort();

    await expect(fetchAllPages<number>('/api/v1/users', { signal: controller.signal })).rejects.toThrow();
    expect(urls).toEqual([]);
  });
});
