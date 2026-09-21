import { afterEach, describe, expect, it, vi } from 'vitest';
import { listDocuments, type DocumentSummary } from '@/features/documents/api';

/**
 * Перелік документів із фільтрами `state`/`mine` (`BE-09b`).
 *
 * ⚠ Перевіряється саме АДРЕСА: серверні тести ходять у маршрут самі, тож
 * параметр, названий у клієнті інакше (`status=` замість `state=`), вони не
 * побачили б — фільтр мовчки не діяв би.
 */

const sent: string[] = [];

function mockServer(items: DocumentSummary[]): void {
  sent.length = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      sent.push(String(url));
      return new Response(JSON.stringify({ items, nextCursor: null, totalCount: items.length }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

const row: DocumentSummary = {
  id: 5,
  projectId: 1,
  businessKey: 'P1-V1-0005',
  createdAt: '2026-01-15T10:00:00Z',
  sheetCount: 1,
  sheetStates: {},
  hasLateEdits: true,
};

describe('listDocuments', () => {
  it('передає state і mine іменами, яких чекає сервер', async () => {
    mockServer([row]);

    const page = await listDocuments({ periodKey: 202601, state: 'Rejected', mine: true, cursor: 'a#b' });

    expect(sent).toEqual([
      '/api/v1/documents?limit=50&periodKey=202601&cursor=a%23b&state=Rejected&mine=true',
    ]);
    expect(page.items?.[0]?.hasLateEdits).toBe(true);
  });

  it('порожні фільтри в адресу не потрапляють', async () => {
    mockServer([]);

    await listDocuments({ periodKey: null, state: null, mine: false });

    expect(sent).toEqual(['/api/v1/documents?limit=50']);
  });
});
