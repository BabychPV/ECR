import { afterEach, describe, expect, it, vi } from 'vitest';
import { methodologyConstantUsage } from '@/features/methodologies/api';

/**
 * Споживач `GET /api/v1/methodologies/{id}/versions/{vid}/constants/{code}/usage`
 * (ФВ-8.14). Права `Calculation.View`.
 *
 * ⚠ Той самий предмет перевірки, що й `registries/__tests__/registryUsage.test.ts`:
 * сама адреса й форма відповіді, а не екран — сторож
 * `Кожна_адреса_яку_викликає_клієнт_існує_на_сервері` бачить літерал у коді,
 * але не бачить, ЩО саме відправлено.
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

describe('«Де використано» константи версії методики', () => {
  it('питає адресу цієї константи методом GET', async () => {
    const attempts = mockFetch({ total: 0, items: [] });

    await methodologyConstantUsage(1, 2, 'K1');

    expect(attempts).toHaveLength(1);
    expect(attempts[0]!.method).toBe('GET');
    expect(attempts[0]!.url).toBe('/api/v1/methodologies/1/versions/2/constants/K1/usage');
  });

  it('кодує код константи, а не вклеює його в шлях як є', async () => {
    // ⛔ Код константи (як і код довідника) може містити символи, значущі в
    // URL. Без `encodeURIComponent` запит поповз би не туди — мовчки й успішно.
    const attempts = mockFetch({ total: 0, items: [] });

    await methodologyConstantUsage(1, 2, 'A/B?x');

    expect(attempts[0]!.url).toBe('/api/v1/methodologies/1/versions/2/constants/A%2FB%3Fx/usage');
  });

  it('віддає `total` окремо від обрізаного переліку', async () => {
    mockFetch({
      total: 23,
      items: [{ kind: 'templateFormula', id: '7', label: 'F.7', route: '/admin/templates/1' }],
    });

    const usage = await methodologyConstantUsage(1, 2, 'K1');

    expect(usage.total).toBe(23);
    expect(usage.items).toHaveLength(1);
    expect(usage.items[0]!.kind).toBe('templateFormula');
  });
});
