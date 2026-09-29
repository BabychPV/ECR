import { afterEach, describe, expect, it, vi } from 'vitest';
import { getRegistryRows, registryRowsQuery, saveBatch } from '@/features/registries/rows/api';

/**
 * Споживач `GET /api/v1/registries/{code}/rows` (RT-13, FEATURE-REGISTRY-TABLES §7.1).
 *
 * ⚠ Предмет — адреса, параметри й форма відповіді; сітка — RT-30.
 */

/** Замінює мережу і запам'ятовує адреси. */
function mockFetch(body: unknown): string[] {
  const urls: string[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      urls.push(String(input));
      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );

  return urls;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('рядки довідника', () => {
  it('читає сторінку з адреси довідника й лишає число рядком', async () => {
    // Тіло — сирий JSON: 26 значущих цифр у double не вміщаються.
    const raw =
      '{"items":[{"id":7,"code":"E1","display":"1D-2","parentEntryId":null,"validFrom":null,"validTo":null,' +
      '"version":"CN8d9YFjIFA=","values":{"T":{"value":"1234567890.1234567890123456","display":null,"unit":"degC"}}}],' +
      '"nextCursor":"Nw==","totalCount":3}';
    const urls: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        urls.push(String(input));
        return new Response(raw, { status: 200, headers: { 'Content-Type': 'application/json' } });
      }),
    );

    const page = await getRegistryRows('GAS/COMP', { asOf: '2026-09-30', limit: 2 });

    // ⛔ Код довідника закодований: «/» інакше звернувся б до чужої адреси.
    expect(urls).toEqual(['/api/v1/registries/GAS%2FCOMP/rows?asOf=2026-09-30&limit=2']);
    expect(page.items[0]!.values['T']!.value).toBe('1234567890.1234567890123456');
    expect(page.items[0]!.version).toBe('CN8d9YFjIFA=');
    expect(page.nextCursor).toBe('Nw==');
    expect(page.totalCount).toBe(3);
  });

  it('передає курсор, батька, момент історії й фільтри полів', async () => {
    const urls = mockFetch({ items: [], nextCursor: null, totalCount: 0 });

    await getRegistryRows('STREAM', {
      asOfUtc: '2026-09-27T14:03:00.000Z',
      parentEntryId: 4411,
      q: ' winter ',
      fields: { GROUP: 'North', T: '12.4', EMPTY: '  ' },
      cursor: 'Nw==',
    });

    const query = new URLSearchParams(urls[0]!.split('?')[1]);
    expect(urls[0]!.startsWith('/api/v1/registries/STREAM/rows?')).toBe(true);
    expect(query.get('asOfUtc')).toBe('2026-09-27T14:03:00.000Z');
    expect(query.get('parentEntryId')).toBe('4411');
    expect(query.get('q')).toBe('winter');
    expect(query.get('field.GROUP')).toBe('North');
    expect(query.get('field.T')).toBe('12.4');
    // Очищений фільтр не надсилається: сервер прочитав би його як «порожнє значення».
    expect(query.has('field.EMPTY')).toBe(false);
    expect(query.get('cursor')).toBe('Nw==');
    expect(query.get('limit')).toBe('100');
  });

  it('не надсилає порожніх параметрів', () => {
    expect(registryRowsQuery({ q: '   ', cursor: null })).toBe('limit=100');
  });
});

describe('пакет рядків довідника', () => {
  it('надсилає пакет POST-ом на адресу довідника з dryRun і повертає звіт з помилкою рядка', async () => {
    const report = {
      applied: false,
      dryRun: true,
      added: 0,
      updated: 1,
      deleted: 0,
      unchanged: 0,
      rows: [
        {
          clientRowId: 'r1',
          status: 'error',
          entryId: 9001,
          version: null,
          errors: [
            {
              field: null,
              errorCode: 'ECR-REG-4093',
              messageKey: 'err.ECR-REG-4093.entryChanged',
              params: { entryCode: 'E000009001' },
            },
          ],
        },
      ],
    };
    const calls: { url: string; init: RequestInit | undefined }[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        calls.push({ url: String(input), init });
        return new Response(JSON.stringify(report), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );

    const items = [
      {
        clientRowId: 'r1',
        op: 'upsert',
        id: 9001,
        code: null,
        baseVersion: 'AAABkWmN3kM=',
        values: { MOL_PCT: '12.4246690' },
      },
      { clientRowId: 'r3', op: 'delete', id: 9005, code: null, baseVersion: null, values: null },
    ];

    const result = await saveBatch('GAS/COMP', items, true);

    expect(calls).toHaveLength(1);
    expect(calls[0]!.url).toBe('/api/v1/registries/GAS%2FCOMP/entries/batch?dryRun=true');
    expect(calls[0]!.init?.method).toBe('POST');
    // Число лишається рядком: сервер читає його без втрати знаків.
    expect(JSON.parse(String(calls[0]!.init?.body))).toEqual({ items });
    // Помилка рядка — дані звіту, а не відмова.
    expect(result.applied).toBe(false);
    expect(result.rows[0]!.errors[0]!.messageKey).toBe('err.ECR-REG-4093.entryChanged');
  });

  it('без dryRun записує', async () => {
    const urls = mockFetch({ applied: true, dryRun: false, added: 0, updated: 0, deleted: 0, unchanged: 0, rows: [] });

    await saveBatch('STREAM', [], false);

    expect(urls).toEqual(['/api/v1/registries/STREAM/entries/batch?dryRun=false']);
  });
});
