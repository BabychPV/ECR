import { describe, it, expect, vi, afterEach } from 'vitest';
import { recalculateImpacted, registryImpact } from '@/features/registries/impact/api';

/**
 * Споживач `GET /api/v1/registries/{code}/impact` (RT-25). Предмет — адреса й форма відповіді:
 * помилка в сегменті шляху чи незакодований код лишилися б непоміченими до першого клацання.
 */

interface Attempt {
  url: string;
  method: string;
  body?: string | undefined;
}

function mockFetch(body: unknown): Attempt[] {
  const attempts: Attempt[] = [];

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      attempts.push({
        url: String(input),
        method: (init?.method ?? 'GET').toUpperCase(),
        body: typeof init?.body === 'string' ? init.body : undefined,
      });

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

describe('Зачеплені документи довідника', () => {
  it('питає адресу цього довідника методом GET', async () => {
    const attempts = mockFetch({ items: [], total: 0, truncated: false });

    await registryImpact('COMPONENT');

    expect(attempts).toHaveLength(1);
    expect(attempts[0]!.method).toBe('GET');
    expect(attempts[0]!.url).toBe('/api/v1/registries/COMPONENT/impact');
  });

  it('кодує код довідника, а не вклеює його в шлях як є', async () => {
    const attempts = mockFetch({ items: [], total: 0, truncated: false });

    await registryImpact('A/B?x');

    expect(attempts[0]!.url).toBe('/api/v1/registries/A%2FB%3Fx/impact');
  });

  it('віддає перелік із методологіями й ознакою обрізання', async () => {
    mockFetch({
      items: [
        {
          documentId: 5,
          businessKey: 'DOC5',
          periodKey: 202601,
          periodState: 'Open',
          via: ['methodology:HSE301'],
        },
      ],
      total: 1200,
      truncated: true,
    });

    const result = await registryImpact('COMPONENT');

    expect(result.items[0]!.via).toEqual(['methodology:HSE301']);
    expect(result.total).toBe(1200);
    expect(result.truncated).toBe(true);
  });
});

describe('Перерахунок зачеплених документів', () => {
  it('шле POST на адресу довідника з причиною і переліком документів', async () => {
    const attempts = mockFetch({ jobId: 'job-1' });

    const result = await recalculateImpacted('A/B', { documentIds: [1, 2], reason: 'правка складу' });

    expect(attempts[0]!.method).toBe('POST');
    expect(attempts[0]!.url).toBe('/api/v1/registries/A%2FB/recalculate-impacted');
    expect(JSON.parse(attempts[0]!.body!)).toEqual({ documentIds: [1, 2], reason: 'правка складу' });
    expect(result.jobId).toBe('job-1');
  });
});