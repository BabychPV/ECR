import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { PeriodPolicyManager } from '@/features/projects/PeriodPolicyManager';
import { testTheme } from '@/test/render';

/**
 * Перелік політик періодів: стани, яких бракувало `period-policy-manager.test`
 * (там — `503` і «порожньо»): «завантаження» і `403` (`ФВ-14.22`).
 *
 * ⚠ Перелік — основний вміст вікна, і на «порожньо» людина заводить нову
 * політику; запит у дорозі чи відмова в праві не можуть виглядати так само.
 */
configure({ asyncUtilTimeout: 10_000 });

function serve(policies: () => Promise<Response>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => policies()),
  );
}

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <PeriodPolicyManager />
      </QueryClientProvider>
    </MantineProvider>,
  );

  fireEvent.click(screen.getByRole('button', { name: /periods\.managePolicies/ }));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('PeriodPolicyManager — стани', () => {
  it('у дорозі: скелет «завантаження», а не «політик немає»', async () => {
    serve(() => new Promise<Response>(() => undefined));
    show();

    expect((await screen.findByRole('status')).getAttribute('aria-busy')).toBe('true');
    expect(screen.queryByText('⟦state.emptyTitle⟧')).toBeNull();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('403: стан «немає права» з кодом, а не «політик немає»; створення заблоковане', async () => {
    serve(() =>
      Promise.resolve(
        new Response(
          JSON.stringify({
            title: 'Forbidden',
            status: 403,
            errorCode: 'ECR-AUTH-0403',
            correlationId: 'corr-policies',
            detail: null,
          }),
          { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
        ),
      ),
    );
    show();

    expect((await screen.findByRole('alert')).textContent).toContain('ECR-AUTH-0403');
    expect(screen.queryByText('⟦state.emptyTitle⟧')).toBeNull();

    // ⚠ Код заповнено, щоб кнопку тримала саме відмова, а не порожнє поле.
    fireEvent.change(screen.getByLabelText(/periods\.policyCode/), { target: { value: 'NEW_2026' } });
    expect(screen.getByRole('button', { name: /periods\.policyCreate/ }).hasAttribute('disabled')).toBe(true);
  });
});
