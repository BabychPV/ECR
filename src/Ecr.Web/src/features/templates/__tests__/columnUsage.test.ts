import { afterEach, describe, expect, it, vi } from 'vitest';
import { columnUsage } from '@/features/templates/columnApi';

/**
 * Споживач `GET /api/v1/column-defs/{id}/usage` (ФВ-8.14). Право `Template.View`.
 *
 * ⚠ Той самий предмет перевірки, що й `registries/__tests__/registryUsage.test.ts`:
 * сама адреса й форма відповіді, а не екран.
 */

interface Attempt {
  url: string;
  method: string;
}

function mockFetch(body: unknown): Attempt[] {
  const attempts: Attempt[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      attempts.push({ url: String(input), method: (init?.method ?? 'GET').toUpperCase() });

      return new Response(JSON.stringify(body), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );

  return attempts;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('«Де використано» колонки шаблону', () => {
  it('питає адресу цієї колонки методом GET, за числовим id', async () => {
    const attempts = mockFetch({ total: 0, items: [] });

    await columnUsage(42);

    expect(attempts).toHaveLength(1);
    expect(attempts[0]!.method).toBe('GET');
    expect(attempts[0]!.url).toBe('/api/v1/column-defs/42/usage');
  });

  it('віддає `total` окремо від обрізаного переліку', async () => {
    mockFetch({
      total: 12,
      items: [{ kind: 'calculationBinding', id: '3', label: 'BIND.3', route: null }],
    });

    const usage = await columnUsage(42);

    expect(usage.total).toBe(12);
    expect(usage.items).toHaveLength(1);
    expect(usage.items[0]!.kind).toBe('calculationBinding');
  });
});
