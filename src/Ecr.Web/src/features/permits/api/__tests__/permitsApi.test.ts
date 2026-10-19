import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  activatePermit,
  createPermit,
  deletePermit,
  endPermit,
  getPermit,
  listPermits,
  renewPermit,
  updatePermit,
} from '@/features/permits/api/permitsApi';

/**
 * Споживач API дозволів (Permits, PM-04): адреса, метод і тіло кожної дії. Екрана ще немає (PM-07) — сторож «кожна дія
 * сервера має споживача» бачить літерали, але не бачить, ЩО відправлено.
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

describe('API дозволів', () => {
  it('перелік без фільтрів кличе адресу без запиту, з фільтрами — кодує параметри', async () => {
    const attempts = mockFetch(200, { items: [], nextCursor: null, totalCount: 0 });

    await listPermits();
    await listPermits({ type: 'AirPermit', status: 'Active', q: 'a b', asOf: '2026-10-18', limit: 100, cursor: '42' });

    expect(attempts[0]).toMatchObject({ url: '/api/v1/permits', method: 'GET' });
    expect(attempts[1]!.url).toBe('/api/v1/permits?type=AirPermit&status=Active&q=a+b&asOf=2026-10-18&limit=100&cursor=42');
  });

  it('читання дозволу за Id — з датою стану чи без неї', async () => {
    const attempts = mockFetch();

    await getPermit(7);
    await getPermit(7, '2026-10-18');

    expect(attempts[0]).toMatchObject({ url: '/api/v1/permits/7', method: 'GET' });
    expect(attempts[1]!.url).toBe('/api/v1/permits/7?asOf=2026-10-18');
  });

  it('створення й зміна шлють тіло відповідними методами', async () => {
    const attempts = mockFetch();
    const header = {
      number: 'KZ-1',
      location: 'Offshore',
      issueDate: null,
      validFrom: '2026-01-01',
      validTo: '2027-01-01',
      reportingFlags: { form300: false, form301: true, form302: false, tox: false },
    };

    await createPermit({ typeCode: 'AirPermit', ...header });
    await updatePermit(7, { rowVersion: 'rv', actualEnd: null, ...header });

    expect(attempts.map((a) => [a.method, a.url])).toEqual([
      ['POST', '/api/v1/permits'],
      ['PUT', '/api/v1/permits/7'],
    ]);
    expect(attempts[0]!.body).toEqual({ typeCode: 'AirPermit', ...header });
    expect(attempts[1]!.body).toMatchObject({ rowVersion: 'rv', number: 'KZ-1', actualEnd: null });
  });

  it('активація, завершення й поновлення — POST; тіло поновлення необов\'язкове', async () => {
    const attempts = mockFetch();

    await activatePermit(7);
    await endPermit(7, { reason: 'об\'єкт виведено з експлуатації', endDate: '2026-12-01' });
    await renewPermit(7);
    await renewPermit(7, { validTo: '2028-01-01' });

    expect(attempts.map((a) => [a.method, a.url])).toEqual([
      ['POST', '/api/v1/permits/7/activate'],
      ['POST', '/api/v1/permits/7/end'],
      ['POST', '/api/v1/permits/7/renew'],
      ['POST', '/api/v1/permits/7/renew'],
    ]);
    expect(attempts[0]!.body).toBeNull();
    expect(attempts[1]!.body).toEqual({ reason: 'об\'єкт виведено з експлуатації', endDate: '2026-12-01' });
    expect(attempts[2]!.body).toBeNull();
    expect(attempts[3]!.body).toEqual({ validTo: '2028-01-01' });
  });

  it('видалення — DELETE без тіла, відповідь 204 не розбирається', async () => {
    const attempts = mockFetch(204);

    await expect(deletePermit(7)).resolves.toBeUndefined();

    expect(attempts[0]).toMatchObject({ url: '/api/v1/permits/7', method: 'DELETE', body: null });
  });
});
