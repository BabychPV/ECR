import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider, useInfiniteQuery } from '@tanstack/react-query';
import type { JSX } from 'react';
import { testTheme } from '@/test/render';
import { InfiniteListFooter } from '../InfiniteListFooter';
import { mergePages, nextCursorOf } from '../mergePages';
import { MaxAutoFillChain } from '../useInfiniteScroll';
import { installIntersection } from './fakeIntersection';

/**
 * W-3 (рішення PO 11.11): підвал нескінченної прокрутки. Кожен кейс стереже свій запобіжник:
 * 1 — без дублів запитів (від'єднання ДО виклику, `!isFetching`); 3 — ланцюг `MaxAutoFillChain`;
 * 4 — відмова не зациклює автоповтор (`!isFetchNextPageError`); 5 — середовище без IO.
 */
interface Page {
  readonly items: string[];
  readonly nextCursor: string | null;
  readonly totalCount: number | null;
}

interface Fetcher {
  readonly requested: Array<string | null>;
  readonly fetchPage: (cursor: string | null) => Promise<Page>;
}

function makeFetcher(pages: number, failOnce?: string): Fetcher {
  const requested: Array<string | null> = [];
  let failed = false;

  return {
    requested,
    fetchPage: async (cursor) => {
      requested.push(cursor);
      // Мережа не відповідає в тому ж такті: інакше `isFetching` не встигає стати видимим між сторінками.
      await new Promise((resolve) => setTimeout(resolve, 10));
      if (cursor !== null && cursor === failOnce && !failed) {
        failed = true;
        throw new Error('boom');
      }
      const i = cursor === null ? 0 : Number(cursor.slice(1));

      return { items: [`row-${String(i)}`], nextCursor: i + 1 < pages ? `p${String(i + 1)}` : null, totalCount: null };
    },
  };
}

function Harness({ fetcher }: { fetcher: Fetcher }): JSX.Element {
  const query = useInfiniteQuery({
    queryKey: ['infinite-harness'],
    queryFn: ({ pageParam }) => fetcher.fetchPage(pageParam),
    initialPageParam: null as string | null,
    getNextPageParam: (last, _pages, lastParam) => nextCursorOf(last, lastParam),
  });
  const page = mergePages<Page>(query.data?.pages);

  return (
    <div>
      {page?.items.map((item) => (
        <p key={item}>{item}</p>
      ))}
      <InfiniteListFooter pager={query} shown={page?.items.length ?? 0} total={page?.totalCount} />
    </div>
  );
}

function show(fetcher: Fetcher): { unmount: () => void } {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <Harness fetcher={fetcher} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Кілька тактів: дає змогу побачити, чи не виросла довжина `requested`. */
async function settle(): Promise<void> {
  for (let i = 0; i < 5; i += 1) {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('useInfiniteScroll + InfiniteListFooter (W-3)', () => {
  it('сторож у вікні: сторінки довантажуються самі, кожна рівно один раз', async () => {
    installIntersection(true);
    const fetcher = makeFetcher(3);
    show(fetcher);

    await screen.findByText('row-2');
    await settle();

    expect(fetcher.requested).toEqual([null, 'p1', 'p2']);
    expect(document.querySelector('[data-infinite-status]')?.textContent).toContain('⟦list.allShown');
  });

  it('сторож поза вікном — лише перша сторінка; перетин — наступна', async () => {
    const probe = installIntersection(false);
    const fetcher = makeFetcher(3);
    show(fetcher);

    await screen.findByText('row-0');
    await settle();
    expect(fetcher.requested).toEqual([null]);

    probe.setVisible(true);
    await screen.findByText('row-1');
    await settle();

    expect(fetcher.requested).toContain('p1');
    expect(new Set(fetcher.requested).size).toBe(fetcher.requested.length);
  });

  it('ланцюг обмежено MaxAutoFillChain; кнопка продовжує', async () => {
    installIntersection(true);
    const fetcher = makeFetcher(20);
    const user = userEvent.setup();
    show(fetcher);

    await waitFor(() => expect(fetcher.requested.length).toBe(1 + MaxAutoFillChain));
    await settle();
    expect(fetcher.requested.length).toBe(1 + MaxAutoFillChain);
    expect(document.querySelector('[data-infinite-paused]')).not.toBeNull();

    await user.click(screen.getByRole('button', { name: '⟦documents.more⟧' }));
    await waitFor(() => expect(fetcher.requested.length).toBeGreaterThan(1 + MaxAutoFillChain));
  });

  it('відмова наступної сторінки: рядки лишаються, автоповтору немає, «Повторити» дочитує', async () => {
    installIntersection(true);
    const fetcher = makeFetcher(2, 'p1');
    const user = userEvent.setup();
    show(fetcher);

    await screen.findByRole('button', { name: '⟦common.retry⟧' });
    await settle();

    expect(screen.getByText('row-0')).toBeTruthy();
    expect(fetcher.requested).toEqual([null, 'p1']);

    await user.click(screen.getByRole('button', { name: '⟦common.retry⟧' }));
    await screen.findByText('row-1');
  });

  it('без IntersectionObserver — лише кнопка', async () => {
    vi.stubGlobal('IntersectionObserver', undefined);
    const fetcher = makeFetcher(2);
    const user = userEvent.setup();
    show(fetcher);

    await screen.findByText('row-0');
    await settle();

    expect(fetcher.requested).toEqual([null]);
    expect(document.querySelector('[data-auto-pause="unsupported"]')).not.toBeNull();
    expect(document.querySelector('[data-infinite-paused]')).toBeNull();

    await user.click(screen.getByRole('button', { name: '⟦documents.more⟧' }));
    await screen.findByText('row-1');
  });

  it('клавіатура: остання сторінка переводить фокус із кнопки на рядок стану', async () => {
    installIntersection(false);
    const fetcher = makeFetcher(2);
    const user = userEvent.setup();
    show(fetcher);

    const button = await screen.findByRole('button', { name: '⟦documents.more⟧' });
    button.focus();
    await user.click(button);
    await screen.findByText('row-1');

    await waitFor(() => expect(document.activeElement).toBe(document.querySelector('[data-infinite-status]')));
  });

  it("розмонтування від'єднує спостерігача", async () => {
    const probe = installIntersection(false);
    const fetcher = makeFetcher(3);
    const { unmount } = show(fetcher);

    await screen.findByText('row-0');
    await settle();
    expect(probe.live()).toBeGreaterThan(0);

    unmount();
    expect(probe.live()).toBe(0);
  });
});

describe('mergePages / nextCursorOf (W-3)', () => {
  it('дубль за ключем — один рядок; totalCount першої, nextCursor останньої', () => {
    const merged = mergePages<{ items: Array<{ id: number }>; nextCursor: string | null; totalCount: number | null }>(
      [
        { items: [{ id: 1 }, { id: 2 }], nextCursor: 'c2', totalCount: 5 },
        { items: [{ id: 2 }, { id: 3 }], nextCursor: null, totalCount: null },
      ],
      (item) => item.id,
    );

    expect(merged?.items.map((i) => i.id)).toEqual([1, 2, 3]);
    expect(merged?.totalCount).toBe(5);
    expect(merged?.nextCursor).toBeNull();
  });

  it('незмінний курсор — кінець видачі', () => {
    expect(nextCursorOf({ nextCursor: 'c2' }, 'c2')).toBeNull();
    expect(nextCursorOf({ nextCursor: 'c3' }, 'c2')).toBe('c3');
    expect(nextCursorOf(null, null)).toBeNull();
  });
});
