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
 * T3-04: у формі «Change password» не було `<form>`, тож Enter у полі не відправляв запит
 * (на формі входу — відправляє). Працював лише клік по кнопці.
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

let posts: string[] = [];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

beforeEach(async () => {
  posts = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (init?.method === 'POST' && url.includes('/change-password')) {
        posts.push(typeof init.body === 'string' ? init.body : '');
        return new Response(null, { status: 204 });
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

function show(): void {
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <ChangePasswordPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('ChangePasswordPage: Enter відправляє форму', () => {
  it('Enter у полі «Repeat password» — один POST /auth/change-password', async () => {
    const user = userEvent.setup();
    show();

    await user.type(await screen.findByLabelText('Current password'), 'OldPassword1');
    await user.type(screen.getByLabelText('New password'), 'NewPassword1234');
    await user.type(screen.getByLabelText('Repeat password'), 'NewPassword1234{Enter}');

    // ⛔ Мутація «прибрати `component="form"`/`type="submit"`» — запиту немає, тест червоний.
    await waitFor(() => expect(posts).toHaveLength(1));
    expect(JSON.parse(posts[0] ?? '{}')).toEqual({ currentPassword: 'OldPassword1', newPassword: 'NewPassword1234' });
  });

  it('Enter при розбіжності паролів — запиту немає', async () => {
    const user = userEvent.setup();
    show();

    await user.type(await screen.findByLabelText('Current password'), 'OldPassword1');
    await user.type(screen.getByLabelText('New password'), 'NewPassword1234');
    await user.type(screen.getByLabelText('Repeat password'), 'Different1234{Enter}');

    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(posts).toHaveLength(0);
  });
});
