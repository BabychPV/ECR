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
 * Клієнтська політика пароля на екрані зміни пароля (`ФВ-6.18`).
 *
 * ⛔ Мінімальна довжина не доступна клієнту ДО спроби (перевірено окремо:
 * немає в `GET /api/v1/public/bootstrap`, немає в публічній частині каталогу
 * рядків), тож відтворюється лише реактивна частина — той самий патерн, що
 * й `UserAdminActions.tsx` (`isPasswordTooShort`): відмова `tooShort` іде під
 * полем НОВОГО пароля, а не загальним банером `ErrorAlert`.
 */

const Strings: Record<string, string> = {
  'password.title': 'Change password',
  'password.current': 'Current password',
  'password.next': 'New password',
  'password.repeat': 'Repeat password',
  'password.mismatch': 'Passwords do not match',
  'password.submit': 'Change',
  'password.policy': 'At least 12 characters, with upper case, lower case and a digit.',
};

interface Call {
  url: string;
  method: string;
  body: string | null;
}

let calls: Call[] = [];
let refusal: { status: number; errorCode: string; messageKey: string; detail: string } | null = null;

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

beforeEach(async () => {
  calls = [];
  refusal = null;

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      calls.push({ url, method, body: typeof init?.body === 'string' ? init.body : null });

      if (url.includes('/ui-strings/')) {
        return json({ languageCode: 'en', revision: 1, strings: Strings });
      }
      if (/\/api\/v1\/me(\?|$)/.test(url)) {
        return json({ userId: 1, userName: 'jdoe', language: 'en', permissions: [], isSimulation: false });
      }
      if (method === 'POST' && url.includes('/change-password') && refusal !== null) {
        return json(
          {
            title: 'Refused',
            status: refusal.status,
            detail: refusal.detail,
            errorCode: refusal.errorCode,
            correlationId: 'c-1',
            messageKey: refusal.messageKey,
          },
          refusal.status,
        );
      }
      if (method === 'POST') return new Response(null, { status: 204 });

      return json({});
    }),
  );

  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter>
          <ChangePasswordPage />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const posts = (): Call[] => calls.filter((c) => c.method === 'POST' && c.url.includes('/change-password'));

describe('ChangePasswordPage: клієнтська перевірка політики пароля', () => {
  it('tooShort — під полем нового пароля з підставленим minLength, не банером', async () => {
    refusal = {
      status: 422,
      errorCode: 'ECR-PWD-0422',
      messageKey: 'err.ECR-PWD-0422.tooShort',
      detail: 'The new password is shorter than 8 characters.',
    };
    const user = userEvent.setup();
    show();

    await user.type(await screen.findByLabelText('Current password'), 'OldPassword1');
    await user.type(screen.getByLabelText('New password'), 'short');
    await user.type(screen.getByLabelText('Repeat password'), 'short');
    await user.click(screen.getByRole('button', { name: 'Change' }));

    await waitFor(() => expect(posts()).toHaveLength(1));

    const message = await screen.findByText('The new password is shorter than 8 characters.');
    // Під САМИМ полем нового пароля: та сама обгортка поля.
    const field = screen.getByLabelText('New password');
    expect(message.closest('.mantine-InputWrapper-root')?.contains(field)).toBe(true);

    // Не загальним банером, і сирий код клієнту не показаний.
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.queryByText('ECR-PWD-0422')).toBeNull();
  });

  it('інша відмова (наприклад, невірний поточний пароль) — банером, не під полем нового пароля', async () => {
    refusal = {
      status: 422,
      errorCode: 'ECR-PWD-0422',
      messageKey: 'err.ECR-PWD-0422.wrongCurrent',
      detail: 'Current password is wrong.',
    };
    const user = userEvent.setup();
    show();

    await user.type(await screen.findByLabelText('Current password'), 'WrongPassword1');
    await user.type(screen.getByLabelText('New password'), 'NewPassword123');
    await user.type(screen.getByLabelText('Repeat password'), 'NewPassword123');
    await user.click(screen.getByRole('button', { name: 'Change' }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Current password is wrong.');

    const field = screen.getByLabelText('New password');
    expect(field.closest('.mantine-InputWrapper-root')?.textContent).not.toContain('Current password is wrong.');
  });
});
