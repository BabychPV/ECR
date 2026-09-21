import { describe, it, expect, vi, afterEach } from 'vitest';
import { registryUsage } from '@/features/registries/api';

/**
 * Споживач `GET /api/v1/registries/{code}/usage` (директива №15, `BE-24`).
 *
 * ⚠ Предмет тут — сама адреса й форма відповіді, а не екран: сторож
 * `Кожна_адреса_яку_викликає_клієнт_існує_на_сервері` бачить літерал у коді,
 * але не бачить, ЩО саме відправлено. Помилка в сегменті шляху (`/usages`,
 * незакодований код довідника) лишилася б непоміченою до першого клацання.
 */

interface Attempt {
  url: string;
  method: string;
}

/** Замінює мережу і запам'ятовує запити. */
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

describe('«Де використано» визначення довідника', () => {
  it('питає адресу цього довідника методом GET', async () => {
    const attempts = mockFetch({ total: 0, items: [] });

    await registryUsage('UNITS');

    expect(attempts).toHaveLength(1);
    expect(attempts[0]!.method).toBe('GET');
    expect(attempts[0]!.url).toBe('/api/v1/registries/UNITS/usage');
  });

  it('кодує код довідника, а не вклеює його в шлях як є', async () => {
    // ⛔ Код довідника приходить із маршруту й може містити що завгодно.
    // Без `encodeURIComponent` значення з «/» або «?» перетворило б запит на
    // звернення до чужої адреси — мовчки й успішно.
    const attempts = mockFetch({ total: 0, items: [] });

    await registryUsage('A/B?x');

    expect(attempts[0]!.url).toBe('/api/v1/registries/A%2FB%3Fx/usage');
  });

  it('віддає `total` окремо від обрізаного переліку', async () => {
    // ⚠ Числа різні НАВМИСНО: перелік обмежений сторінкою, лічильник чесний.
    // Клієнт, який показав би `items.length`, сказав би «одне посилання» там,
    // де їх двадцять три.
    mockFetch({
      total: 23,
      items: [{ kind: 'templateColumn', id: '7', label: 'TBL.COL', route: '/admin/templates/1' }],
    });

    const usage = await registryUsage('UNITS');

    expect(usage.total).toBe(23);
    expect(usage.items).toHaveLength(1);
    expect(usage.items[0]!.kind).toBe('templateColumn');
  });
});
