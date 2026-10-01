import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { ChannelsPanel } from '@/features/notifications/ChannelsPanel';
import { testTheme } from '@/test/render';

/**
 * Канали сповіщень: стани «немає права» і «завантаження» (`ФВ-14.22`).
 *
 * ⚠ 500 і порожній перелік уже стереже `ChannelsPanel.test.tsx` (L10). Тут —
 * 403 і запит у дорозі: жоден не має читатися як «каналів не заведено», бо
 * саме за цим текстом адміністратор заводить дублікат каналу.
 */
configure({ asyncUtilTimeout: 10_000 });

function serve(channelsAnswer: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path.endsWith('/api/v1/notifications/channels')) return channelsAnswer();

      return new Response('null', { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ChannelsPanel />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

/** Тексти порожнього стану: закриваюча дужка відрізняє заголовок від підказки. */
function noEmptyTexts(): void {
  expect(screen.queryByText(/notifications\.noChannels⟧/)).toBeNull();
  expect(screen.queryByText(/notifications\.noChannelsHint⟧/)).toBeNull();
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ChannelsPanel — стани', () => {
  it('403: відмова з кодом, а не «каналів не заведено»', async () => {
    serve(() =>
      Promise.resolve(
        new Response(
          JSON.stringify({ title: 'Forbidden', status: 403, errorCode: 'ECR-AUTH-0403', correlationId: 'c', detail: null }),
          { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    );
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    noEmptyTexts();
    expect(screen.queryByRole('table')).toBeNull();
    expect(document.querySelector('[data-channels="pending"]')).toBeNull();
  });

  it('у дорозі: скелет, а не «каналів не заведено» і не порожня таблиця', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    await waitFor(() => expect(document.querySelector('[data-channels="pending"]')).toBeTruthy());
    noEmptyTexts();
    expect(screen.queryByRole('table')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
