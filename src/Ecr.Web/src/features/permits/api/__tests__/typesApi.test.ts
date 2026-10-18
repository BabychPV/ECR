import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  createPermitType,
  deletePermitType,
  getPermitType,
  listPermitTypes,
  openPermitTypeVersion,
  publishPermitType,
  replacePermitType,
} from '@/features/permits/api/typesApi';

/**
 * Споживач API типів дозволів (Permits, PM-03): адреса, метод і тіло кожної дії. Екрана ще немає
 * (PM-06/PM-08) — сторож «кожна дія сервера має споживача» бачить літерали, але не бачить, ЩО відправлено.
 */

interface Attempt {
  url: string;
  method: string;
  body: unknown;
}

function mockFetch(status = 200, body: unknown = {}): Attempt[] {
  const attempts: Attempt[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      attempts.push({
        url: String(input),
        method: (init?.method ?? 'GET').toUpperCase(),
        body: typeof init?.body === 'string' ? JSON.parse(init.body) : null,
      });

      return status === 204
        ? new Response(null, { status })
        : new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
    }),
  );

  return attempts;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('API типів дозволів', () => {
  it('перелік без фільтрів кличе адресу без запиту, з фільтрами — кодує параметри', async () => {
    const attempts = mockFetch(200, { items: [], nextCursor: null, totalCount: null });

    await listPermitTypes();
    await listPermitTypes({ category: 'Air', status: 'Published', q: 'a b', limit: 20, cursor: 'WATER' });

    expect(attempts[0]).toMatchObject({ url: '/api/v1/permit-types', method: 'GET' });
    expect(attempts[1]!.url).toBe('/api/v1/permit-types?category=Air&status=Published&q=a+b&limit=20&cursor=WATER');
  });

  it('читання й нова версія кодують код типу в адресі', async () => {
    const attempts = mockFetch();

    await getPermitType('A/B');
    await openPermitTypeVersion('A/B');

    expect(attempts[0]).toMatchObject({ url: '/api/v1/permit-types/A%2FB', method: 'GET' });
    expect(attempts[1]).toMatchObject({ url: '/api/v1/permit-types/A%2FB/new-version', method: 'POST' });
  });

  it('створення, заміна й публікація шлють тіло відповідними методами', async () => {
    const attempts = mockFetch();

    await createPermitType({ code: 'AIR', nameL10n: { en: 'Air' }, category: 'Air' });
    await replacePermitType('AIR', { rowVersion: 'rv', nameL10n: { en: 'Air' }, category: 'Air', tables: [], rules: [] });
    await publishPermitType('AIR', { reason: 'перший випуск типу', rowVersion: 'rv' });

    expect(attempts.map((a) => [a.method, a.url])).toEqual([
      ['POST', '/api/v1/permit-types'],
      ['PUT', '/api/v1/permit-types/AIR'],
      ['POST', '/api/v1/permit-types/AIR/publish'],
    ]);
    expect(attempts[0]!.body).toEqual({ code: 'AIR', nameL10n: { en: 'Air' }, category: 'Air' });
    expect(attempts[2]!.body).toEqual({ reason: 'перший випуск типу', rowVersion: 'rv' });
  });

  it('видалення — DELETE без тіла, відповідь 204 не розбирається', async () => {
    const attempts = mockFetch(204);

    await expect(deletePermitType('AIR')).resolves.toBeUndefined();

    expect(attempts[0]).toMatchObject({ url: '/api/v1/permit-types/AIR', method: 'DELETE', body: null });
  });
});
