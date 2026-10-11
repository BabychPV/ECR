import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import { ChangePasswordPage } from '@/pages/ChangePasswordPage';

const done = vi.hoisted(() => vi.fn());

vi.mock('@/shared/ui/notify', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/ui/notify')>()),
  showDone: done,
}));

/**
 * FP-7 (прохід «від першого обличчя» 2026-10-11): після успішної зміни пароля сеанс анулюється, і людину
 * кидало на вхід без жодного слова «пароль змінено».
 *
 * ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати `showDone(t('password.changed'))` з `submit()` — тест червоніє.
 */
const Strings: Record<string, string> = {
  'password.title': 'Change password',
  'password.current': 'Current password',
  'password.next': 'New password',
  'password.repeat': 'Repeat password',
  'password.submit': 'Change',
  'password.changed': 'Password changed.',
};

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

beforeEach(async () => {
  done.mockClear();
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      if (url.includes('/ui-strings/')) return json({ languageCode: 'en', revision: 1, strings: Strings });
      if (init?.method === 'POST' && url.includes('/change-password')) return new Response(null, { status: 204 });
      return json({ userId: 1, userName: 'jdoe', language: 'en', permissions: [], isSimulation: false });
    }),
  );
  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('ChangePasswordPage: повідомлення про успішну зміну', () => {
  it('після успішної відправки показується «пароль змінено»', async () => {
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
    await user.type(screen.getByLabelText('New password'), 'NewPassword1234');
    await user.type(screen.getByLabelText('Repeat password'), 'NewPassword1234{Enter}');

    await waitFor(() => expect(done).toHaveBeenCalledWith('Password changed.'));
  });
});
