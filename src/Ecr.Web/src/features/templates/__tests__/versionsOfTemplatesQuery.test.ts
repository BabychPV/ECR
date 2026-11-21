import { afterEach, describe, expect, it, vi } from 'vitest';
import { QueryClient } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { VersionsConcurrency, versionsOfTemplatesQuery } from '../versionsOfTemplates';

/**
 * Рецензія L10, L5: діалог створення проєкту читав версії КОЖНОГО шаблону одночасно (`useQueries`) — із переліком
 * на всі сторінки це сотні запитів в одну мить. Тепер — пул із `VersionsConcurrency`, повний перелік версій на
 * шаблон під його власним ключем `versionsOf(id)`.
 *
 * Мутаційний доказ (не в коміті): `VersionsConcurrency` → `templateIds.length` у пулі → червоний (одночасних 30).
 */

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('versionsOfTemplatesQuery', () => {
  it('30 шаблонів — не більше 4 запитів одночасно; відповідь у порядку шаблонів; кеш versionsOf(id) наповнено', async () => {
    let inFlight = 0;
    let maxInFlight = 0;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        inFlight += 1;
        maxInFlight = Math.max(maxInFlight, inFlight);
        const id = Number(/templates\/(\d+)\/versions/.exec(String(input))?.[1]);
        await new Promise((resolve) => setTimeout(resolve, 5));
        inFlight -= 1;

        return new Response(JSON.stringify({ items: [{ id: id * 10, version: `v${String(id)}` }], nextCursor: null, totalCount: null }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }),
    );

    const client = new QueryClient();
    const ids = Array.from({ length: 30 }, (_, i) => i + 1);
    const pages = await client.fetchQuery(versionsOfTemplatesQuery(client, ids));

    expect(maxInFlight).toBe(VersionsConcurrency);
    expect(pages.map((page) => page.items[0]?.id)).toEqual(ids.map((id) => id * 10));
    expect(client.getQueryData(queryKeys.templates.versionsOf(7))).toEqual({
      items: [{ id: 70, version: 'v7' }],
      nextCursor: null,
      totalCount: null,
    });
  });
});
