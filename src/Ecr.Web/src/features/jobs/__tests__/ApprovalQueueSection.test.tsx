import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { ApprovalQueueSection } from '@/features/jobs/ApprovalQueueSection';
import { approvalQueueHref, type ApprovalQueueItem } from '@/features/jobs/useApprovalQueue';

/**
 * Блок «До погодження» (ФВ-12.5): мок `GET /api/v1/me/approval-queue` за схемою контракту.
 * Мутація: приберіть `sheet=`/`periodKey=` з `approvalQueueHref` або курсор у запиті наступної сторінки - тести червоні.
 */

const calls: string[] = [];

function item(over: Partial<ApprovalQueueItem>): ApprovalQueueItem {
  return {
    documentId: 11,
    documentKey: 'AIR-2026-09',
    projectCode: 'AIR',
    templateCode: 'AIR',
    sheetDefId: 3,
    sheetCode: 'Stationary',
    periodKey: 202609,
    submittedAt: '2026-10-07T09:30:00Z',
    submittedByDisplayName: 'Ivan <b>P</b>',
    stepOrdinal: 1,
    stepCount: 2,
    stepRoleCode: 'Approver',
    ...over,
  } as ApprovalQueueItem;
}

function show(respond: (url: string) => { status: number; body: unknown }, onNavigate?: () => void): void {
  vi.stubGlobal(
    'fetch',
    vi.fn((input: RequestInfo | URL) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.pathname + input.search : input.url;
      calls.push(url);
      const { status, body } = respond(url);

      return Promise.resolve(
        new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }),
      );
    }),
  );
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <ApprovalQueueSection opened {...(onNavigate === undefined ? {} : { onNavigate })} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cleanup();
  calls.length = 0;
  vi.unstubAllGlobals();
});

describe('ApprovalQueueSection', () => {
  it('показує рядки, посилання несе період і аркуш, ім’я - текстом', async () => {
    show(() => ({ status: 200, body: { items: [item({})], nextCursor: null } }));

    const card = await waitFor(() => {
      const el = document.querySelector('[data-approval-document="11"]');
      expect(el).not.toBeNull();

      return el as HTMLElement;
    });
    expect(card.textContent).toContain('AIR-2026-09 · Stationary');
    expect(card.textContent).toContain('Ivan <b>P</b>');
    expect(card.querySelector('b')).toBeNull();
    expect(card.querySelector('a')?.getAttribute('href')).toBe('/documents/11?periodKey=202609&sheet=Stationary');
    expect(screen.queryByTestId('my-tasks-approval-more')).toBeNull();
    expect(calls[0]).toBe('/api/v1/me/approval-queue?limit=50');
  });

  it('період - людською назвою, а не сирим ключем', async () => {
    show(() => ({ status: 200, body: { items: [item({})], nextCursor: null } }));
    const card = await waitFor(() => {
      const el = document.querySelector('[data-approval-document="11"]');
      expect(el).not.toBeNull();

      return el as HTMLElement;
    });
    expect(card.textContent).toContain('September 2026');
    expect(card.textContent).not.toContain('202609');
  });

  it('клік «Review» повідомляє про перехід (шухляда закривається)', async () => {
    const onNavigate = vi.fn();
    show(() => ({ status: 200, body: { items: [item({})], nextCursor: null } }), onNavigate);
    const card = await waitFor(() => {
      const el = document.querySelector('[data-approval-document="11"]');
      expect(el).not.toBeNull();

      return el as HTMLElement;
    });
    expect(onNavigate).not.toHaveBeenCalled();
    fireEvent.click(card.querySelector('[data-action="review"]') as HTMLElement);
    expect(onNavigate).toHaveBeenCalledTimes(1);
  });

  it('порожня черга мовчить', async () => {
    show(() => ({ status: 200, body: { items: [], nextCursor: null } }));
    await waitFor(() => expect(calls.length).toBe(1));
    expect(screen.queryByTestId('my-tasks-approval')).toBeNull();
    expect(screen.queryByTestId('my-tasks-approval-error')).toBeNull();
  });

  it('коротка сторінка з курсором: «Показати ще» бере наступну за курсором', async () => {
    show((url) =>
      url.includes('cursor=')
        ? { status: 200, body: { items: [item({ documentId: 12, documentKey: 'AIR-2026-10' })], nextCursor: null } }
        : { status: 200, body: { items: [item({})], nextCursor: 'a/b+c=' } },
    );
    fireEvent.click(await screen.findByTestId('my-tasks-approval-more'));
    await waitFor(() => expect(document.querySelector('[data-approval-document="12"]')).not.toBeNull());
    expect(calls[1]).toBe('/api/v1/me/approval-queue?limit=50&cursor=a%2Fb%2Bc%3D');
    expect(screen.queryByTestId('my-tasks-approval-more')).toBeNull();
  });

  it('відмова видима, з повтором', async () => {
    show(() => ({ status: 500, body: { title: 'x', status: 500 } }));
    expect(await screen.findByTestId('my-tasks-approval-error')).toBeTruthy();
  });

  it('без маршруту й без імені: рядок без кроку й автора не ламається', async () => {
    show(() => ({
      status: 200,
      body: { items: [item({ submittedByDisplayName: null, stepOrdinal: null, stepCount: null, stepRoleCode: null })], nextCursor: null },
    }));
    const card = await waitFor(() => {
      const el = document.querySelector('[data-approval-document="11"]');
      expect(el).not.toBeNull();

      return el as HTMLElement;
    });
    expect(card.textContent).not.toContain('Ivan');
    expect(card.textContent).not.toContain('null');
  });
});

describe('approvalQueueHref', () => {
  it('кодує код аркуша', () => {
    expect(approvalQueueHref(item({ sheetCode: 'A B' }))).toBe('/documents/11?periodKey=202609&sheet=A%20B');
  });
});
