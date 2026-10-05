import { afterEach, describe, expect, it, vi } from 'vitest';
import type { RegistryRow } from '@/features/registries/rows/api';
import { resolveLookup } from '../data';

/**
 * Зіставлення вставленого тексту з записом цілі `Lookup` (§8.4 «Вставка з Excel»).
 *
 * ⚠ Мутаційний доказ (перевірено руками, 2026-10-04): зупинитися після першої сторінки, без
 * проходу курсором → «точний код за першою сторінкою…» червоний.
 */
const row = (id: number, code: string, display = code): RegistryRow => ({
  id,
  code,
  display,
  parentEntryId: null,
  validFrom: null,
  validTo: null,
  version: `v${String(id)}`,
  values: {},
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('resolveLookup', () => {
  it('точний код за першою сторінкою підрядкових збігів — знайдено (L9-08)', async () => {
    const urls: string[] = [];
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = new URL(String(input), 'http://localhost');
        urls.push(url.search);
        // ⚠ `N2` — підрядок кодів `N20…N519`, які мають менші Id і йдуть першими.
        const body = url.searchParams.get('cursor') === 'c2'
          ? { items: [row(900, 'N2', 'Nitrogen')], nextCursor: null, totalCount: 501 }
          : { items: Array.from({ length: 500 }, (_, i) => row(i + 1, `N${String(i + 20)}`)), nextCursor: 'c2', totalCount: 501 };
        return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
      }),
    );

    expect(await resolveLookup('COMPONENT', 'n2', null)).toEqual({ id: '900', display: 'Nitrogen (N2)' });
    expect(urls).toHaveLength(2);
  });
});
