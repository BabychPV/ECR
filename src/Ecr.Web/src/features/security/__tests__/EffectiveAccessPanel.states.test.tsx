import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { EffectiveAccessPanel } from '@/features/security/EffectiveAccessPanel';
import { testTheme } from '@/test/render';

/**
 * Розріз ефективного доступу (`ФВ-6.16`): стани «помилка», «немає права»,
 * «завантаження» (`ФВ-14.22`).
 *
 * ⚠ Порожній розріз (жодного внеску) уже стереже `EffectiveAccessPanel.test.tsx`;
 * тут — те, що його не має: відмова чи запит у дорозі НЕ виглядають як
 * «жодного внеску / гранту немає».
 */
configure({ asyncUtilTimeout: 10_000 });

const problem = (status: number, errorCode: string): Promise<Response> =>
  Promise.resolve(
    new Response(
      JSON.stringify({
        title: status === 403 ? 'Forbidden' : 'Server error',
        status,
        errorCode,
        correlationId: 'corr-effective',
        detail: null,
      }),
      { status, headers: { 'Content-Type': 'application/problem+json' } },
    ),
  );

function serve(answer: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => answer()),
  );
}

/** Рендер і «Пояснити» для `Registry:5`: доти запиту немає зовсім. */
function showAndAsk(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <EffectiveAccessPanel userId={7} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  fireEvent.change(screen.getByLabelText(/effectiveAccess\.resourceId/), { target: { value: '5' } });
  fireEvent.click(screen.getByRole('button', { name: /effectiveAccess\.explain/ }));
}

/** Тексти стану «даних немає»: жоден не має з'явитися при відмові чи в дорозі. */
function noEmptyTexts(): void {
  expect(screen.queryByText(/effectiveAccess\.noContributions/)).toBeNull();
  expect(screen.queryByText(/effectiveAccess\.noGrant/)).toBeNull();
  expect(screen.queryByText(/effectiveAccess\.level/)).toBeNull();
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('EffectiveAccessPanel — стани', () => {
  it('500: помилка з кодом, а не «внесків немає»', async () => {
    serve(() => problem(500, 'ECR-SYS-0500'));
    showAndAsk();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-SYS-0500');
    noEmptyTexts();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('403: стан «немає права» з кодом, а не «гранту немає»', async () => {
    serve(() => problem(403, 'ECR-AUTH-0403'));
    showAndAsk();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    noEmptyTexts();
  });

  it('у дорозі: «завантаження», а не порожній розріз', async () => {
    serve(() => new Promise<Response>(() => undefined));
    showAndAsk();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    noEmptyTexts();
    expect(screen.queryByRole('alert')).toBeNull();
  });
});
