import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import { ChangePasswordPage } from '@/pages/ChangePasswordPage';

/**
 * T3-08: після відмови «пароль закороткий» (422) червоне повідомлення лишалось під полем, коли
 * людина вже виправила значення, — до наступного відправлення.
 */
const Strings: Record<string, string> = {
  'password.title': 'Change password',
  'password.current': 'Current password',
  'password.next': 'New password',
  'password.repeat': 'Repeat password',
  'password.mismatch': 'Passwords do not match',
  'password.submit': 'Change',
  'password.policy': 'At least 12 characters.',
};

let posts = 0;

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

beforeEach(async () => {
  posts = 0;
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (init?.method === 'POST' && url.includes('/change-password')) {
        posts += 1;
        return json(
          {
            title: 'Refused',
            status: 422,
            detail: 'The new password is shorter than 12 characters.',
            errorCode: 'ECR-PWD-0422',
            correlationId: 'c-1',
            messageKey: 'err.ECR-PWD-0422.tooShort',
          },
          422,
        );
      }
      return json({ userId: 1, userName: 'jdoe', language: 'en', permissions: [], isSimulation: false });
    }),
  );
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ChangePasswordPage: застаріла помилка під полем', () => {
  it('після виправлення нового пароля повідомлення «закороткий» зникає', async () => {
    const user = userEvent.setup();
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <MemoryRouter>
            <ChangePasswordPage />
          </MemoryRouter>
        </QueryClientProvider>
      </MantineProvider>,
    );

    await user.type(await screen.findByLabelText('Current password'), 'OldPassword1');
    await user.type(screen.getByLabelText('New password'), 'short');
    await user.type(screen.getByLabelText('Repeat password'), 'short');
    await user.click(screen.getByRole('button', { name: 'Change' }));

    await screen.findByText('The new password is shorter than 12 characters.');
    expect(posts).toBe(1);

    // ⛔ Мутація «прибрати `setError(null)` з onChange» лишає текст — тест червоний.
    await user.type(screen.getByLabelText('New password'), 'LongerPassword1');
    await waitFor(() => expect(screen.queryByText('The new password is shorter than 12 characters.')).toBeNull());
  });
});
