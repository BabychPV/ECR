import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RulesMatrixPanel } from '@/features/notifications/RulesMatrixPanel';
import { testTheme } from '@/test/render';

/**
 * Матриця правил сповіщень: стан «завантаження» (`ФВ-14.22`).
 *
 * ⚠ 500, 403 і «каналів нуль» уже стереже `RulesMatrixPanel.test.tsx`. Тут —
 * запит у дорозі: порожня матриця чи «правил немає, бо каналів немає» до
 * відповіді — це твердження про те, чого ще не знаємо, а кнопка «зберегти»
 * над недочитаною матрицею стерла б наявні правила.
 */
configure({ asyncUtilTimeout: 10_000 });

const json = (body: unknown): Promise<Response> =>
  Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }));

const never = (): Promise<Response> => new Promise<Response>(() => undefined);

function serve(answers: { rules: () => Promise<Response>; channels: () => Promise<Response> }): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      const path = String(input).split('?')[0] ?? '';

      if (path === '/api/v1/notifications/rules') return answers.rules();
      if (path === '/api/v1/notifications/channels') return answers.channels();

      throw new Error(`Непередбачена адреса: ${path}`);
    }),
  );
}

function show(): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <RulesMatrixPanel />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

function busy(): Element | null {
  return document.querySelector('[aria-busy="true"]');
}

/** Жодного стану, що видає себе за відповідь сервера. */
function nothingClaimed(): void {
  expect(screen.queryByText('⟦notifications.rulesNoChannels⟧')).toBeNull();
  expect(screen.queryByRole('table')).toBeNull();
  expect(screen.queryByRole('button', { name: '⟦notifications.saveRules⟧' })).toBeNull();
  expect(screen.queryByRole('alert')).toBeNull();
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('RulesMatrixPanel — стани', () => {
  it('обидва запити в дорозі: скелет, без матриці й без кнопки збереження', async () => {
    serve({ rules: never, channels: never });
    show();

    await waitFor(() => expect(busy()).toBeTruthy());
    nothingClaimed();
  });

  it('канали вже порожні, правила ще в дорозі: скелет, а не «каналів немає»', async () => {
    serve({ rules: never, channels: () => json([]) });
    const client = show();

    // Спершу — що канали СПРАВДІ прийшли: інакше випадок збігався б із попереднім.
    await waitFor(() => expect(client.getQueryState(['notifications', 'channels'])?.status).toBe('success'));

    expect(busy()).toBeTruthy();
    nothingClaimed();
  });
});
