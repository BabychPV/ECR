import { afterEach, describe, expect, it, vi } from 'vitest';
import { searchData } from '@/features/search/api';

/** Пошук даних палітри (BE-19): адреса, кодування запиту й мінімум довжини. */

const original = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = original;
});

function capture(body: unknown = []): { calls: () => string[] } {
  const urls: string[] = [];

  globalThis.fetch = vi.fn(async (input: RequestInfo | URL) => {
    urls.push(String(input));

    return new Response(JSON.stringify(body), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    });
  }) as typeof globalThis.fetch;

  return { calls: () => urls };
}

describe('пошук даних палітри', () => {
  it('обрізає пробіли й кодує запит; ліміт — окремим параметром', async () => {
    const sent = capture();

    await searchData('  Дозвіл 50% & A ', 5);

    const url = new URL(sent.calls()[0] ?? '', 'http://x');
    expect(url.pathname).toBe('/api/v1/search');
    expect(url.searchParams.get('q')).toBe('Дозвіл 50% & A');
    expect(url.searchParams.get('limit')).toBe('5');
  });

  it('запит коротший за 2 символи не йде в мережу', async () => {
    const sent = capture();

    await expect(searchData(' a ')).resolves.toEqual([]);
    expect(sent.calls()).toEqual([]);
  });

  it('повертає збіги сервера як є', async () => {
    const hit = { kind: 'document', id: 7, code: 'DOC7', title: 'Permit' };
    capture([hit]);

    await expect(searchData('DOC')).resolves.toEqual([hit]);
  });
});
