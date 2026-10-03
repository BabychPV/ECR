import { afterEach, describe, expect, it, vi } from 'vitest';
import { loadCatalog } from '../index';

/**
 * T2-09 (в): екран входу вантажить рядки БЕЗ автентифікації — `loadCatalog(lang, 'public')` завжди шле
 * явний `?scope=public` (сервер без `scope` анонімові теж віддає публічну область, але клієнт на це не спирається),
 * а після входу — явний `?scope=private`.
 */
afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

function stub(): ReturnType<typeof vi.fn> {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    const lang = /ui-strings\/(\w+)/.exec(String(input))?.[1] ?? 'en';

    return new Response(JSON.stringify({ languageCode: lang, revision: 1, strings: { 'login.title': 'x' } }), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    });
  });
  vi.stubGlobal('fetch', fetchMock);

  return fetchMock;
}

describe('loadCatalog: область каталогу в адресі', () => {
  it('публічна — ?scope=public, приватна — ?scope=private', async () => {
    const fetchMock = stub();

    await loadCatalog('en', 'public');
    await loadCatalog('en', 'private');

    const urls = fetchMock.mock.calls.map((call: unknown[]) => String(call[0]));
    expect(urls.some((u) => u.includes('/api/v1/ui-strings/en?scope=public'))).toBe(true);
    expect(urls.some((u) => u.includes('/api/v1/ui-strings/en?scope=private'))).toBe(true);
    expect(urls.every((u) => u.includes('scope='))).toBe(true);
  });
});
