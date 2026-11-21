import { afterEach, describe, expect, it, vi } from 'vitest';
import { fetchDocuments } from '../sourceEventsData';

/**
 * PSS-19c: вкладка «Події з PI» брала документи однією сторінкою (`?limit=500`) — прив'язка до документа поза нею
 * лишалась без назви й без вибору. Тепер — усі сторінки (`fetchAllPages`).
 *
 * Мутаційний доказ (не в коміті): повернути `apiFetch('/api/v1/documents?limit=500')` → червоний (501-го немає).
 */

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PSS-19c: документи для мапінгу подій — усі сторінки', () => {
  it('501-й документ є в переліку', async () => {
    const urls: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        urls.push(url);
        const second = url.includes('cursor=');
        const ids = second ? [501] : Array.from({ length: 500 }, (_, i) => i + 1);

        return new Response(
          JSON.stringify({ items: ids.map((id) => ({ id })), nextCursor: second ? null : 'p2', totalCount: null }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }),
    );

    const documents = await fetchDocuments();

    expect(documents).toHaveLength(501);
    expect(documents.at(-1)?.id).toBe(501);
    expect(urls).toEqual(['/api/v1/documents?limit=500', '/api/v1/documents?limit=500&cursor=p2']);
  });
});
