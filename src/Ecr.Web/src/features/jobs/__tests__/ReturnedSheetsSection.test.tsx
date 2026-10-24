import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { ReturnedSheetsSection } from '@/features/jobs/ReturnedSheetsSection';
import type { ReturnedSheetItem } from '@/features/jobs/useReturnedSheets';

/**
 * ✎ D-449: блок «Повернуто на доопрацювання» - мок `GET /api/v1/me/returned-sheets` за схемою контракту.
 * Мутації: приберіть `sheet=`/`periodKey=` з посилання, курсор у запиті наступної сторінки або згортання довгої
 * причини - тести червоні; підставте причину через `dangerouslySetInnerHTML` - червоніє екранування.
 */

const calls: string[] = [];

function item(over: Partial<ReturnedSheetItem>): ReturnedSheetItem {
  return {
    documentId: 11,
    documentKey: 'AIR-2026-09',
    documentNameL10n: { values: { en: 'Air emissions' } },
    projectCode: 'AIR',
    sheetDefId: 3,
    sheetCode: 'Stationary',
    sheetNameL10n: { values: { en: 'Stationary sources' } },
    periodKey: 202609,
    returnedAt: '2026-10-07T09:30:00Z',
    returnedByDisplayName: 'Olena <i>K</i>',
    reason: 'Row 3 <b>totals</b> do not match.\nCheck units.',
    ...over,
  } as ReturnedSheetItem;
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
          <ReturnedSheetsSection opened {...(onNavigate === undefined ? {} : { onNavigate })} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function card(documentId = 11): Promise<HTMLElement> {
  return waitFor(() => {
    const el = document.querySelector(`[data-returned-document="${String(documentId)}"]`);
    expect(el).not.toBeNull();

    return el as HTMLElement;
  });
}

afterEach(() => {
  cleanup();
  calls.length = 0;
  vi.unstubAllGlobals();
});

describe('ReturnedSheetsSection', () => {
  it('документ, аркуш і період; хто повернув і причина - текстом; посилання на аркуш і період', async () => {
    show(() => ({ status: 200, body: { items: [item({})], nextCursor: null } }));

    const el = await card();
    expect(el.textContent).toContain('Air emissions · Stationary sources');
    expect(el.textContent).toContain('September 2026');
    expect(el.textContent).toContain('Olena <i>K</i>');
    expect(el.querySelector('i')).toBeNull();
    const reason = el.querySelector('[data-returned-reason]') as HTMLElement;
    expect(reason.textContent).toBe('Row 3 <b>totals</b> do not match.\nCheck units.');
    expect(reason.querySelector('b')).toBeNull();
    expect(el.querySelector('a[data-action="open-returned"]')?.getAttribute('href')).toBe(
      '/documents/11?periodKey=202609&sheet=Stationary',
    );
    expect(screen.queryByTestId('my-tasks-returned-more')).toBeNull();
    expect(calls[0]).toBe('/api/v1/me/returned-sheets?limit=50');
  });

  it('без назви документа - ключ; без імені того, хто повернув, - лише дата', async () => {
    show(() => ({
      status: 200,
      body: { items: [item({ documentNameL10n: null, returnedByDisplayName: null })], nextCursor: null },
    }));

    const el = await card();
    expect(el.textContent).toContain('AIR-2026-09 · Stationary sources');
    expect(el.textContent).toContain('⟦myTasks.returned.returnedAt');
    expect(el.textContent).not.toContain('null');
  });

  it('коротка причина не згортається; довга - згорнута з «показати повністю»', async () => {
    const long = Array.from({ length: 6 }, (_, i) => `Line ${String(i + 1)}`).join('\n');
    show(() => ({
      status: 200,
      body: { items: [item({}), item({ documentId: 12, reason: long })], nextCursor: null },
    }));

    const short = await card(11);
    expect(short.querySelector('[data-action="toggle-reason"]')).toBeNull();

    const longCard = await card(12);
    const toggle = longCard.querySelector('[data-action="toggle-reason"]') as HTMLElement;
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(toggle.textContent).toBe('⟦myTasks.returned.reasonMore⟧');
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(toggle.textContent).toBe('⟦myTasks.returned.reasonLess⟧');
  });

  it('клік «Відкрити аркуш» повідомляє про перехід (шухляда закривається)', async () => {
    const onNavigate = vi.fn();
    show(() => ({ status: 200, body: { items: [item({})], nextCursor: null } }), onNavigate);
    const el = await card();
    fireEvent.click(el.querySelector('[data-action="open-returned"]') as HTMLElement);
    expect(onNavigate).toHaveBeenCalledTimes(1);
  });

  it('порожньо - мовчить', async () => {
    show(() => ({ status: 200, body: { items: [], nextCursor: null } }));
    await waitFor(() => expect(calls.length).toBe(1));
    expect(screen.queryByTestId('my-tasks-returned')).toBeNull();
    expect(screen.queryByTestId('my-tasks-returned-error')).toBeNull();
  });

  it('«Показати ще» бере наступну сторінку за курсором', async () => {
    show((url) =>
      url.includes('cursor=')
        ? { status: 200, body: { items: [item({ documentId: 12 })], nextCursor: null } }
        : { status: 200, body: { items: [item({})], nextCursor: '1.2' } },
    );
    fireEvent.click(await screen.findByTestId('my-tasks-returned-more'));
    await card(12);
    expect(calls[1]).toBe('/api/v1/me/returned-sheets?limit=50&cursor=1.2');
  });

  it('відмова видима, з повтором', async () => {
    show(() => ({ status: 500, body: { title: 'x', status: 500 } }));
    expect(await screen.findByTestId('my-tasks-returned-error')).toBeTruthy();
  });
});
