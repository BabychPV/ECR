import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { UserView } from '@/api/types';
import { loadCatalog } from '@/shared/i18n';
import { testTheme } from '@/test/render';
import { UserAdminActions } from '@/features/security/UserAdminActions';

/**
 * Дії адміністратора над обліковим записом (`BE-12`): хто бачить кнопки, що
 * йде на сервер і куди потрапляє відмова.
 */

const Strings: Record<string, string> = {
  'security.lockUser': 'Lock',
  'security.unlockUser': 'Unlock',
  'security.resetPassword': 'Reset password',
  'security.newPassword': 'New password',
  'security.resetPasswordHint': 'Hint',
  'security.lockReasonHint': 'Up to {max} characters',
  'security.reasonTooLong': 'Longer than {max} characters',
  'security.userLocked': 'Locked',
  'security.userUnlocked': 'Unlocked',
  'security.passwordResetDone': 'Password set',
  'workflow.reason': 'Reason',
  'common.cancel': 'Cancel',
};

const SelfId = 7;

function userView(overrides: Partial<UserView>): UserView {
  return {
    id: 42,
    userName: 'jdoe',
    displayName: 'Jane Doe',
    provider: 'Local',
    email: null,
    isActive: true,
    isBootstrapAdmin: false,
    isLockedOut: false,
    mustChangePassword: false,
    receivesAlerts: false,
    lastSignInAt: null,
    ...overrides,
  };
}

interface Call {
  url: string;
  method: string;
  body: string | null;
}

let calls: Call[] = [];
let permissions: string[] = ['Security.ManageUsers'];
let refusal: { status: number; errorCode: string; messageKey: string; detail: string } | null = null;

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

beforeEach(async () => {
  calls = [];
  permissions = ['Security.ManageUsers'];
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
        return json({ userId: SelfId, userName: 'admin', language: 'en', permissions, isSimulation: false });
      }
      if (method === 'POST' && refusal !== null) {
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

      return json({ items: [], nextCursor: null, totalCount: 0 });
    }),
  );

  await loadCatalog('en', 'public');
  await loadCatalog('en', 'private');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderActions(user: UserView): QueryClient {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <UserAdminActions user={user} />
      </QueryClientProvider>
    </MantineProvider>,
  );

  return client;
}

const posts = (): Call[] => calls.filter((c) => c.method === 'POST');

describe('UserAdminActions: хто бачить дії', () => {
  it('чужий локальний незаблокований запис: «Заблокувати» і «Скинути пароль» є, «Розблокувати» немає', async () => {
    renderActions(userView({}));

    expect(await screen.findByRole('button', { name: 'Lock' })).not.toBeNull();
    expect(screen.getByRole('button', { name: 'Reset password' })).not.toBeNull();
    expect(screen.queryByRole('button', { name: 'Unlock' })).toBeNull();
  });

  it('заблокований запис: «Розблокувати» замість «Заблокувати»', async () => {
    renderActions(userView({ isLockedOut: true }));

    expect(await screen.findByRole('button', { name: 'Unlock' })).not.toBeNull();
    expect(screen.queryByRole('button', { name: 'Lock' })).toBeNull();
  });

  it('власний рядок — жодної дії', async () => {
    renderActions(userView({ id: SelfId }));

    await waitFor(() => expect(calls.some((c) => c.url.includes('/me'))).toBe(true));
    // Сесія приїхала — і кнопок однаково немає.
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByRole('button', { name: 'Lock' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Reset password' })).toBeNull();
  });

  it('без Security.ManageUsers — жодної дії', async () => {
    permissions = [];
    renderActions(userView({}));

    await waitFor(() => expect(calls.some((c) => c.url.includes('/me'))).toBe(true));
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.queryByRole('button', { name: 'Lock' })).toBeNull();
  });

  it('доменний запис: «Скинути пароль» немає, «Заблокувати» є', async () => {
    renderActions(userView({ provider: 'Windows' }));

    expect(await screen.findByRole('button', { name: 'Lock' })).not.toBeNull();
    expect(screen.queryByRole('button', { name: 'Reset password' })).toBeNull();
  });
});

describe('UserAdminActions: блокування', () => {
  it('без причини запит не йде; з причиною — POST /lock із причиною', async () => {
    const user = userEvent.setup();
    renderActions(userView({}));

    await user.click(await screen.findByRole('button', { name: 'Lock' }));
    const dialog = await screen.findByRole('dialog');
    const confirm = within(dialog).getByRole('button', { name: 'Lock' });

    expect((confirm as HTMLButtonElement).disabled).toBe(true);
    await user.click(confirm);
    expect(posts()).toHaveLength(0);

    await user.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'left the company');
    await user.click(confirm);

    await waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0]?.url).toMatch(/\/api\/v1\/users\/42\/lock$/);
    expect(JSON.parse(posts()[0]?.body ?? '{}')).toEqual({ reason: 'left the company' });
  });

  it('причина довша за 400 символів не відправляється, діалог каже чому', async () => {
    const user = userEvent.setup();
    renderActions(userView({}));

    await user.click(await screen.findByRole('button', { name: 'Lock' }));
    const dialog = await screen.findByRole('dialog');
    const field = within(dialog).getByRole('textbox', { name: /Reason/ });

    await user.click(field);
    await user.paste('x'.repeat(401));
    await user.click(within(dialog).getByRole('button', { name: 'Lock' }));

    expect(await within(dialog).findByText('Longer than 400 characters')).not.toBeNull();
    expect(posts()).toHaveLength(0);
  });

  it('lastAdministrator — банер ErrorAlert у рядку', async () => {
    refusal = {
      status: 409,
      errorCode: 'ECR-SEC-0409',
      messageKey: 'err.ECR-SEC-0409.lastAdministrator',
      detail: 'jdoe is the last administrator',
    };
    const user = userEvent.setup();
    renderActions(userView({}));

    await user.click(await screen.findByRole('button', { name: 'Lock' }));
    const dialog = await screen.findByRole('dialog');
    await user.type(within(dialog).getByRole('textbox', { name: /Reason/ }), 'why');
    await user.click(within(dialog).getByRole('button', { name: 'Lock' }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('jdoe is the last administrator');
    expect(alert.textContent).toContain('ECR-SEC-0409');
  });
});

describe('UserAdminActions: скидання пароля', () => {
  it('поле без автозаповнення й очищається після закриття', async () => {
    const user = userEvent.setup();
    renderActions(userView({}));

    await user.click(await screen.findByRole('button', { name: 'Reset password' }));
    let field = await screen.findByLabelText('New password');
    expect(field.getAttribute('autocomplete')).toBe('new-password');

    await user.type(field, 'Secret#123');
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

    await user.click(screen.getByRole('button', { name: 'Reset password' }));
    field = await screen.findByLabelText('New password');
    expect((field as HTMLInputElement).value).toBe('');
  });

  it('пароль іде лише тілом POST — не в адресі й не в кеші мутацій', async () => {
    const user = userEvent.setup();
    const client = renderActions(userView({}));

    await user.click(await screen.findByRole('button', { name: 'Reset password' }));
    await user.type(await screen.findByLabelText('New password'), 'Secret#123');
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Reset password' }));

    await waitFor(() => expect(posts()).toHaveLength(1));
    expect(posts()[0]?.url).toMatch(/\/api\/v1\/users\/42\/reset-password$/);
    expect(JSON.parse(posts()[0]?.body ?? '{}')).toEqual({ newPassword: 'Secret#123' });
    expect(calls.some((c) => c.url.includes('Secret'))).toBe(false);

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    const cached = JSON.stringify(client.getMutationCache().getAll().map((m) => m.state));
    expect(cached).not.toContain('Secret');
  });

  it('tooShort — під полем, не банером', async () => {
    refusal = {
      status: 422,
      errorCode: 'ECR-PWD-0422',
      messageKey: 'err.ECR-PWD-0422.tooShort',
      detail: 'At least 12 characters',
    };
    const user = userEvent.setup();
    renderActions(userView({}));

    await user.click(await screen.findByRole('button', { name: 'Reset password' }));
    const field = await screen.findByLabelText('New password');
    await user.type(field, 'short');
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Reset password' }));

    const message = await screen.findByText('At least 12 characters');
    // Під САМИМ полем: та сама обгортка поля, і поле позначене невалідним.
    expect(message.closest('.mantine-InputWrapper-root')?.contains(field)).toBe(true);
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.queryByText('ECR-PWD-0422')).toBeNull();
  });

  it('domainPasswordReset — банером у діалозі, не під полем', async () => {
    refusal = {
      status: 422,
      errorCode: 'ECR-USR-0422',
      messageKey: 'err.ECR-USR-0422.domainPasswordReset',
      detail: 'Domain account',
    };
    const user = userEvent.setup();
    renderActions(userView({}));

    await user.click(await screen.findByRole('button', { name: 'Reset password' }));
    const field = await screen.findByLabelText('New password');
    await user.type(field, 'whatever');
    await user.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Reset password' }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('Domain account');
    expect(field.closest('.mantine-InputWrapper-root')?.textContent).not.toContain('Domain account');
  });
});
