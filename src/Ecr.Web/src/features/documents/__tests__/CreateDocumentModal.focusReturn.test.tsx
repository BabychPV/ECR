import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CreateDocumentModal } from '@/features/documents/CreateDocumentModal';
import { testTheme } from '@/test/render';

/**
 * T2-06: після ПЕРШОГО закриття діалогу «New document» клавішею Esc фокус падав на `BODY`; друге — повертав
 * на кнопку. Діалог на сторінці монтується вже відкритим (`creatingRequested && <CreateDocumentModal opened>`),
 * тож Mantine `useFocusReturn` не бачив переходу `opened` false → true і не запам'ятовував елемент.
 *
 * Харнес повторює сторінку: кнопка одним кліком і монтує діалог, і відкриває його.
 */

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function Host(): JSX.Element {
  const [requested, setRequested] = useState(false);
  const [open, setOpen] = useState(false);

  return (
    <>
      <button
        type="button"
        onClick={() => {
          setRequested(true);
          setOpen(true);
        }}
      >
        open-create
      </button>
      {requested && <CreateDocumentModal opened={open} onClose={() => setOpen(false)} />}
    </>
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('CreateDocumentModal: фокус повертається на кнопку вже після першого закриття', () => {
  it('Esc після першого відкриття — фокус на кнопці, а не на BODY', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) =>
        String(input).split('?')[0]?.endsWith('/api/v1/languages')
          ? json([{ code: 'en', nameNative: 'English', isDefault: true }])
          : json({ items: [], nextCursor: null, totalCount: 0 }),
      ),
    );

    const user = userEvent.setup();
    render(
      <MantineProvider theme={testTheme}>
        <MemoryRouter>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <Host />
          </QueryClientProvider>
        </MemoryRouter>
      </MantineProvider>,
    );

    const trigger = screen.getByRole('button', { name: 'open-create' });
    await user.click(trigger);
    await screen.findByRole('dialog');

    await user.keyboard('{Escape}');

    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(document.activeElement).toBe(trigger);
    });
  });
});
