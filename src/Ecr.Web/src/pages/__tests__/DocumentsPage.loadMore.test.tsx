import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { JSX } from 'react';
import { DocumentsPage } from '@/pages/DocumentsPage';
import { testTheme } from '@/test/render';
import { installIntersection } from '@/features/common/infiniteList/__tests__/fakeIntersection';

/**
 * B36 (аудит W-3, Q8 дефолт A): «Load more» ДОПИСУЄ наступну сторінку під уже показані рядки, а не замінює їх.
 * Курсор у адресу більше не пишеться; стара адреса з `?cursor=` відкриває першу сторінку. Зміна фільтра —
 * перелік знову з першої сторінки.
 * ⛔ Червоний без фіксу: після «Load more» зникав DOC-000001 (сторінку замінено), адреса отримувала `cursor=`.
 */
const SlowEnvTimeout = 400_000;

function doc(id: number): Record<string, unknown> {
  return {
    id,
    businessKey: `DOC-00000${id}`,
    createdAt: '2026-01-01T00:00:00Z',
    projectId: 1,
    sheetCount: 1,
    sheetStates: { S1: 'Draft' },
    hasLateEdits: false,
  };
}

type ListMode = 'plain' | 'overlap' | 'failSecond' | 'emptyFirst';

function mockFetch(mode: ListMode = 'plain'): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      const json = (body: unknown): Response =>
        new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });

      if (url.includes('/api/v1/me')) {
        return json({ denies: [], grants: {}, isSimulation: false, language: 'en', mustChangePassword: false, permissions: [], simulatedForUserId: null, userId: 1, userName: 'tester' });
      }
      if (url.includes('/api/v1/documents/summary')) return json({ draft: 2, submitted: 0, approved: 0, rejected: 0, withIssues: 0 });
      if (url.includes('/api/v1/documents/template-versions')) return json({ versions: [] });
      if (url.includes('/api/v1/documents?')) {
        if (url.includes('cursor=c2')) {
          if (mode === 'failSecond') {
            return new Response(JSON.stringify({ status: 500, title: 'Server error', errorCode: 'ECR-SYS-0500' }), {
              status: 500,
              headers: { 'Content-Type': 'application/json' },
            });
          }

          // `overlap`: документ 1 перейшов на другу сторінку (інша група версії) — клієнт не дублює його.
          return json({ items: mode === 'overlap' ? [doc(1), doc(2)] : [doc(2)], nextCursor: null, totalCount: null });
        }

        // `emptyFirst`: після відсіву видимості перша сторінка порожня, але `nextCursor` є.
        return json({ items: mode === 'emptyFirst' ? [] : [doc(1)], nextCursor: 'c2', totalCount: 2 });
      }
      if (url.includes('/api/v1/projects')) return json({ items: [{ id: 1, code: 'ATR', status: 'Active' }], nextCursor: null, totalCount: 1 });

      return json(null);
    }),
  );
}

function LocationProbe(): JSX.Element {
  const location = useLocation();

  return <span data-testid="location">{location.search}</span>;
}

function show(url: string): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <MemoryRouter initialEntries={[url]}>
        <QueryClientProvider client={client}>
          <DocumentsPage />
          <LocationProbe />
        </QueryClientProvider>
      </MemoryRouter>
    </MantineProvider>,
  );
}

function documentCalls(): string[] {
  return vi
    .mocked(fetch)
    .mock.calls.map(([input]) => String(input))
    .filter((url) => url.includes('/api/v1/documents?'));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

function cursorsRequested(): string[] {
  return documentCalls().map((url) => new URL(url, 'http://x').searchParams.get('cursor') ?? '');
}

describe('DocumentsPage: «Load more» дописує сторінку (B36)', () => {
  it(
    'клік «Load more» — рядки першої І другої сторінки; курсора в адресі немає; кнопка зникає на останній',
    async () => {
      mockFetch();
      show('/?year=2026');
      const user = userEvent.setup();

      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });
      await user.click(await screen.findByRole('button', { name: '⟦documents.more⟧' }, { timeout: SlowEnvTimeout }));

      await screen.findByText('DOC-000002', {}, { timeout: SlowEnvTimeout });
      expect(screen.getByText('DOC-000001')).toBeTruthy();
      expect(screen.getByTestId('location').textContent).not.toContain('cursor');
      expect(screen.queryByRole('button', { name: '⟦documents.more⟧' })).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'зміна фільтра після «Load more» — знову лише перша сторінка',
    async () => {
      mockFetch();
      show('/?year=2026');
      const user = userEvent.setup();

      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });
      await user.click(await screen.findByRole('button', { name: '⟦documents.more⟧' }, { timeout: SlowEnvTimeout }));
      await screen.findByText('DOC-000002', {}, { timeout: SlowEnvTimeout });

      await user.click(screen.getByRole('switch', { name: '⟦documents.filterReopenedMonths⟧' }));

      await waitFor(
        () => expect(documentCalls().some((url) => url.includes('hasReopenedMonths=true') && !url.includes('cursor='))).toBe(true),
        { timeout: SlowEnvTimeout },
      );
      await waitFor(() => expect(screen.queryByText('DOC-000002')).toBeNull(), { timeout: SlowEnvTimeout });
      expect(screen.getByText('DOC-000001')).toBeTruthy();
      expect(screen.getByRole('button', { name: '⟦documents.more⟧' })).toBeTruthy();
    },
    SlowEnvTimeout,
  );

  it(
    'стара адреса з `?cursor=` відкриває першу сторінку, а не помилку чи середину переліку',
    async () => {
      mockFetch();
      show('/?year=2026&cursor=c2');

      // Чекаємо БУДЬ-ЯКИЙ рядок: без фіксу тут з'являлась друга сторінка, і падіння має бути одразу, а не таймаутом.
      await screen.findByText(/^DOC-00000[12]$/, {}, { timeout: SlowEnvTimeout });
      expect(screen.queryByText('DOC-000002')).toBeNull();
      expect(screen.getByText('DOC-000001')).toBeTruthy();
      expect(documentCalls().every((url) => !url.includes('cursor='))).toBe(true);
    },
    SlowEnvTimeout,
  );

  it(
    'W-3a: сторож у вікні — друга сторінка довантажується сама, кожен курсор рівно один раз, рядок стану',
    async () => {
      installIntersection(true);
      mockFetch();
      show('/?year=2026');

      await screen.findByText('DOC-000002', {}, { timeout: SlowEnvTimeout });
      expect(screen.getByText('DOC-000001')).toBeTruthy();
      await waitFor(() => expect(document.querySelector('[data-infinite-status]')?.textContent).toContain('⟦list.allShown'));
      expect(cursorsRequested()).toEqual(['', 'c2']);
      expect(screen.queryByRole('button', { name: '⟦documents.more⟧' })).toBeNull();
    },
    SlowEnvTimeout,
  );

  it(
    'W-3a: сторож поза вікном — лише перша сторінка, рядок «показано N з M» і кнопка',
    async () => {
      installIntersection(false);
      mockFetch();
      show('/?year=2026');

      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });
      expect(screen.queryByText('DOC-000002')).toBeNull();
      expect(cursorsRequested()).toEqual(['']);
      expect(document.querySelector('[data-infinite-status]')?.textContent).toContain('⟦common.shownOf');
      expect(screen.getByRole('button', { name: '⟦documents.more⟧' })).toBeTruthy();
    },
    SlowEnvTimeout,
  );

  it(
    'W-3a: документ, що перейшов на другу сторінку, — один рядок (дубль за id)',
    async () => {
      mockFetch('overlap');
      show('/?year=2026');
      const user = userEvent.setup();

      await screen.findByText('DOC-000001', {}, { timeout: SlowEnvTimeout });
      await user.click(await screen.findByRole('button', { name: '⟦documents.more⟧' }, { timeout: SlowEnvTimeout }));

      await screen.findByText('DOC-000002', {}, { timeout: SlowEnvTimeout });
      expect(screen.getAllByText('DOC-000001')).toHaveLength(1);
    },
    SlowEnvTimeout,
  );

  it(
    'W-3a: відмова другої сторінки — перша лишається, «Повторити» замість автоповтору',
    async () => {
      installIntersection(true);
      mockFetch('failSecond');
      show('/?year=2026');

      await screen.findByRole('button', { name: '⟦common.retry⟧' }, { timeout: SlowEnvTimeout });
      expect(screen.getByText('DOC-000001')).toBeTruthy();
      await new Promise((resolve) => setTimeout(resolve, 50));
      expect(cursorsRequested().filter((cursor) => cursor === 'c2')).toHaveLength(1);
    },
    SlowEnvTimeout,
  );

  it(
    'W-3a: порожня перша сторінка при nextCursor — не «документів немає», підвал добирає наступну',
    async () => {
      installIntersection(true);
      mockFetch('emptyFirst');
      show('/?year=2026');

      await screen.findByText('DOC-000002', {}, { timeout: SlowEnvTimeout });
      expect(screen.queryByText('⟦documents.emptyYear⟧')).toBeNull();
    },
    SlowEnvTimeout,
  );
});
