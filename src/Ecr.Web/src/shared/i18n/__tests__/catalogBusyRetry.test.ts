import { afterEach, describe, expect, it, vi } from 'vitest';
import { catalogFailure, isCatalogFailed, isCatalogResolved, isCatalogRetrying, loadCatalog } from '@/shared/i18n';

/**
 * Каталог рядків на `429` (PSS-29, e2e RC #988).
 *
 * ⛔ Дефект: межа частоти анонімних читань відповідала екрану входу `429` з `Retry-After`, а `loadCatalog` одразу
 * позначав відмову — і сторінка входу казала «Interface texts could not be loaded — check the network
 * connection». Тепер «зайнято» повторюється сам за `Retry-After`, а якщо й далі зайнято — причина відмови
 * «сервер зайнятий», а не «мережа».
 *
 * ⚠ Мова на тест — унікальна: `loaded`/`failed` — модульний стан, що переживає окремі `it()`.
 */
function busy(retryAfter: string): Response {
  // Рівно як сервер (`LoginRateLimiting.RejectBare`): голе 429 без тіла, лише заголовок.
  return new Response(null, { status: 429, headers: { 'Retry-After': retryAfter } });
}

function ok(languageCode: string): Response {
  return new Response(JSON.stringify({ languageCode, revision: 1, strings: { 'login.title': 'ECR' } }), {
    status: 200,
    headers: { 'Content-Type': 'application/json', ETag: `"public-${languageCode}-1"` },
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('loadCatalog: сервер відповів «зайнято»', () => {
  it('429 → повтор за Retry-After → каталог завантажено, відмови немає', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(busy('0')).mockResolvedValueOnce(busy('0')).mockResolvedValueOnce(ok('b1'));
    vi.stubGlobal('fetch', fetch);

    await loadCatalog('b1', 'public');

    expect(fetch).toHaveBeenCalledTimes(3);
    expect(isCatalogResolved('b1', 'public')).toBe(true);
    expect(isCatalogFailed('b1', 'public')).toBe(false);
    expect(catalogFailure('b1', 'public')).toBeNull();
    expect(isCatalogRetrying('b1', 'public')).toBe(false);
  });

  it('поки чекає повтору — «повторюємо», а не німе очікування', async () => {
    const fetch = vi.fn().mockResolvedValueOnce(busy('1')).mockResolvedValueOnce(ok('b2'));
    vi.stubGlobal('fetch', fetch);

    const pending = loadCatalog('b2', 'public');
    await vi.waitFor(() => expect(isCatalogRetrying('b2', 'public')).toBe(true));
    expect(isCatalogResolved('b2', 'public')).toBe(false);

    await pending;
    expect(isCatalogRetrying('b2', 'public')).toBe(false);
    expect(isCatalogFailed('b2', 'public')).toBe(false);
  });

  it('сервер просить чекати довше за бюджет — відмова «зайнято» зі строком, а не «мережа»', async () => {
    const fetch = vi.fn().mockResolvedValue(busy('120'));
    vi.stubGlobal('fetch', fetch);

    await loadCatalog('b3', 'public');

    expect(fetch).toHaveBeenCalledTimes(1);
    expect(isCatalogFailed('b3', 'public')).toBe(true);
    expect(catalogFailure('b3', 'public')).toEqual({ busy: true, retryAfterSeconds: 120 });
  });

  it('контроль: справжній збій (500) не повторюється і зайнятістю не називається', async () => {
    const fetch = vi.fn().mockResolvedValue(new Response(null, { status: 500 }));
    vi.stubGlobal('fetch', fetch);

    await loadCatalog('b4', 'public');

    expect(fetch).toHaveBeenCalledTimes(1);
    expect(isCatalogFailed('b4', 'public')).toBe(true);
    expect(catalogFailure('b4', 'public')).toEqual({ busy: false, retryAfterSeconds: null });
  });
});
