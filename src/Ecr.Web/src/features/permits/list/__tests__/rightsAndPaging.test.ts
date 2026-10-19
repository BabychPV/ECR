import { afterEach, describe, expect, it, vi } from 'vitest';
import type { CurrentUserDto } from '@/api/types';
import { PermitWritePermission, mayWritePermits } from '../permitListRights';
import { fetchAllPermits } from '../usePermits';
import { makePermit } from './permitFixture';

/**
 * Права запису й читання сторінками (PM-07). Мутаційний доказ (вручну, відновлено): без `isSimulation !== true`
 * симуляція отримує кнопки; без `seen.has(next)` курсор, що не рухається, крутить цикл до `MaxPages`.
 */
function me(permissions: string[], isSimulation = false): CurrentUserDto {
  return { permissions, isSimulation } as unknown as CurrentUserDto;
}

describe('mayWritePermits', () => {
  it('потрібне право Registry.EditData: сервер вимагає саме його для всіх дій', () => {
    expect(PermitWritePermission).toBe('Registry.EditData');
    expect(mayWritePermits(me(['Registry.EditData']))).toBe(true);
    expect(mayWritePermits(me(['Registry.View']))).toBe(false);
  });

  it('симуляція лише читає, сеанс ще не завантажено — не показуємо', () => {
    expect(mayWritePermits(me(['Registry.EditData'], true))).toBe(false);
    expect(mayWritePermits(undefined)).toBe(false);
  });
});

describe('fetchAllPermits', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function stub(pages: Record<string, { items: unknown[]; nextCursor: string | null }>): string[] {
    const urls: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input);
        urls.push(url);
        const cursor = new URL(url, 'http://x').searchParams.get('cursor') ?? '';

        return new Response(JSON.stringify({ ...pages[cursor], totalCount: null }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );

    return urls;
  }

  it('читає сторінки по курсору, доки він не закінчиться', async () => {
    const urls = stub({
      '': { items: [makePermit(1), makePermit(2)], nextCursor: '2' },
      '2': { items: [makePermit(3)], nextCursor: null },
    });

    const all = await fetchAllPermits();

    expect(all.map((p) => p.id)).toEqual([1, 2, 3]);
    expect(urls).toEqual(['/api/v1/permits?limit=500', '/api/v1/permits?limit=500&cursor=2']);
  });

  it('курсор, що повторюється, не крутить цикл', async () => {
    const urls = stub({
      '': { items: [makePermit(1)], nextCursor: '1' },
      '1': { items: [makePermit(2)], nextCursor: '1' },
    });

    const all = await fetchAllPermits();

    expect(all.map((p) => p.id)).toEqual([1, 2]);
    expect(urls).toHaveLength(2);
  });
});
