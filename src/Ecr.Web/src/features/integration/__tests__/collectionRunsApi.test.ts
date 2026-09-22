import { afterEach, describe, expect, it, vi } from 'vitest';
import { listCollectionRuns, getCollectionRunDetail } from '../collectionRunsApi';

/**
 * `collectionRunsApi.ts` — побудова запиту журналу прогонів (ФВ-5.23).
 *
 * ⛔ Головне твердження файлу: КОЖЕН заданий фільтр іде в адресу запиту.
 * Мутаційний доказ на `state` — рядок нижче падає, якщо `listCollectionRuns`
 * перестає дописувати `state=` у query (наприклад, якщо рядок
 * `query.set('state', filters.state)` прибрати чи заховати за умовою, яка
 * завжди хибна).
 */

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function lastUrl(mock: ReturnType<typeof vi.fn>): string {
  const calls = mock.mock.calls;
  expect(calls.length).toBeGreaterThan(0);

  return String(calls[calls.length - 1]?.[0]);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('listCollectionRuns: фільтри в адресі запиту', () => {
  it('без фільтрів — лише курсор і розмір сторінки', async () => {
    const fetchMock = vi.fn(async () => json({ items: [], nextCursor: null, totalCount: null }));
    vi.stubGlobal('fetch', fetchMock);

    await listCollectionRuns({}, null);

    const url = lastUrl(fetchMock);
    expect(url).toContain('/api/v1/collection-runs?');
    expect(url).toContain('limit=50');
    expect(url).not.toContain('dataSource=');
    expect(url).not.toContain('entity=');
    expect(url).not.toContain('state=');
    expect(url).not.toContain('from=');
    expect(url).not.toContain('to=');
    expect(url).not.toContain('cursor=');
  });

  it('стан фільтра ПОТРАПЛЯЄ в запит', async () => {
    const fetchMock = vi.fn(async () => json({ items: [], nextCursor: null, totalCount: null }));
    vi.stubGlobal('fetch', fetchMock);

    await listCollectionRuns({ state: 'Failed' }, null);

    expect(lastUrl(fetchMock)).toContain('state=Failed');
  });

  it('джерело, сутність, діапазон і курсор — усі разом', async () => {
    const fetchMock = vi.fn(async () => json({ items: [], nextCursor: null, totalCount: null }));
    vi.stubGlobal('fetch', fetchMock);

    await listCollectionRuns(
      {
        dataSource: 7,
        entity: 42,
        from: '2026-09-01T00:00:00.000Z',
        to: '2026-09-08T00:00:00.000Z',
      },
      'next-page-token',
    );

    const url = lastUrl(fetchMock);
    expect(url).toContain('dataSource=7');
    expect(url).toContain('entity=42');
    expect(url).toContain(`from=${encodeURIComponent('2026-09-01T00:00:00.000Z')}`);
    expect(url).toContain(`to=${encodeURIComponent('2026-09-08T00:00:00.000Z')}`);
    expect(url).toContain('cursor=next-page-token');
  });
});

describe('getCollectionRunDetail', () => {
  it('запитує деталь за адресою з ідентифікатором', async () => {
    const fetchMock = vi.fn(async () =>
      json({ run: null, errorMessage: null, coverage: [], coverageTruncated: false }),
    );
    vi.stubGlobal('fetch', fetchMock);

    await getCollectionRunDetail(123);

    expect(lastUrl(fetchMock)).toContain('/api/v1/collection-runs/123');
  });
});
