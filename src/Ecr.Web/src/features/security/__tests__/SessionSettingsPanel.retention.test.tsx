import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { SessionSettingsView } from '@/api/types';
import { SessionSettingsPanel } from '@/features/security/SessionSettingsPanel';
import { testTheme } from '@/test/render';

/**
 * PSS-24: строк зберігання спроб входу — поле на екрані «Безпека → Сеанс»: межі приходять із сервера, поза межами
 * кнопка збереження вимкнена, збереження шле обидва значення одним PUT.
 */
configure({ asyncUtilTimeout: 10_000 });

const view: SessionSettingsView = {
  idleTimeoutMinutes: 30,
  minIdleTimeoutMinutes: 5,
  maxIdleTimeoutMinutes: 480,
  defaultIdleTimeoutMinutes: 30,
  loginAttemptRetentionDays: 400,
  minLoginAttemptRetentionDays: 30,
  maxLoginAttemptRetentionDays: 3650,
  defaultLoginAttemptRetentionDays: 400,
};

function serve(puts: string[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'PUT') {
        puts.push(String(init.body));
        const sent = JSON.parse(String(init.body)) as { idleTimeoutMinutes: number; loginAttemptRetentionDays: number };
        return new Response(
          JSON.stringify({ ...view, idleTimeoutMinutes: sent.idleTimeoutMinutes, loginAttemptRetentionDays: sent.loginAttemptRetentionDays }),
          { status: 200, headers: { 'Content-Type': 'application/json' } },
        );
      }
      void input;
      return new Response(JSON.stringify(view), { status: 200, headers: { 'Content-Type': 'application/json' } });
    }),
  );
}

function show(canManage: boolean): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <SessionSettingsPanel canManage={canManage} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const submit = (): HTMLButtonElement => document.querySelector('button[type="submit"]') as HTMLButtonElement;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('SessionSettingsPanel — строк зберігання спроб входу', () => {
  it('показує значення з сервера; без змін зберегти не можна', async () => {
    serve([]);
    show(true);

    const input = (await screen.findByTestId('login-attempt-retention')) as HTMLInputElement;
    expect(input.value).toContain('400');
    expect(submit().disabled).toBe(true);
  });

  it('поза межами сервера (29 < 30) — збереження вимкнене', async () => {
    serve([]);
    show(true);

    const input = await screen.findByTestId('login-attempt-retention');
    await userEvent.clear(input);
    await userEvent.type(input, '29');

    await waitFor(() => expect(submit().disabled).toBe(true));
  });

  it('120 — PUT несе обидва значення', async () => {
    const puts: string[] = [];
    serve(puts);
    show(true);

    const input = await screen.findByTestId('login-attempt-retention');
    await userEvent.clear(input);
    await userEvent.type(input, '120');
    await waitFor(() => expect(submit().disabled).toBe(false));
    await userEvent.click(submit());

    await waitFor(() => expect(puts).toHaveLength(1));
    expect(JSON.parse(puts[0] ?? '{}')).toEqual({ idleTimeoutMinutes: 30, loginAttemptRetentionDays: 120 });
  });

  it('без права керування поле вимкнене і кнопки немає', async () => {
    serve([]);
    show(false);

    const input = (await screen.findByTestId('login-attempt-retention')) as HTMLInputElement;
    expect(input.disabled).toBe(true);
    expect(document.querySelector('button[type="submit"]')).toBeNull();
  });
});
