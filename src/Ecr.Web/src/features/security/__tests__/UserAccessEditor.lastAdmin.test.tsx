import { afterEach, describe, expect, it, vi } from 'vitest';
import { configure, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RoleView, UserView } from '@/api/types';
import { UserAccessEditor } from '@/features/security/UserAccessEditor';
import { testTheme } from '@/test/render';

/**
 * Заміна ролей, що лишила б систему без адміністратора (`409 lastAdministrator`, `ReplaceUserRolesHandler`).
 *
 * ⛔ Заради чого файл: відмова читається не лише тостом, що зникає, а й під полем ролей — поле
 * отримує `aria-invalid` і опис помилки (`aria-describedby`), діалог лишається відкритим, а щойно
 * ролі змінюють, позначка зникає. Інша відмова (500 чи інший 409) поле ролей помилковим не робить.
 *
 * Мутаційні докази (лише локально): без `error={rolesError}` у `MultiSelect` — тести 1–2 червоні;
 * без `setRolesError(null)` в `onChange` — тест 2 червоний; без перевірки `messageKey` — тест 4 червоний.
 */
configure({ asyncUtilTimeout: 10_000 });

const user: UserView = {
  id: 7,
  userName: 'admin2',
  displayName: 'Адміністратор',
  email: null,
  provider: 'Local',
  isActive: true,
  isBootstrapAdmin: false,
  isLockedOut: false,
  mustChangePassword: false,
  receivesAlerts: false,
  lastSignInAt: null,
};

const roles: RoleView[] = [
  { id: 1, code: 'SecurityAdmin', isActive: true, isBuiltIn: true, dangerousPermissions: [], permissions: ['Security.ManageUsers'] },
  { id: 2, code: 'DataEntry', isActive: true, isBuiltIn: false, dangerousPermissions: [], permissions: ['Document.View'] },
];

const LastAdmin = {
  type: 'about:blank',
  title: 'Conflict',
  status: 409,
  detail: null,
  errorCode: 'ECR-SEC-0409',
  correlationId: 'cid-last-admin',
  messageKey: 'err.ECR-SEC-0409.lastAdministrator',
  userName: 'admin2',
};

const Crash = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: null,
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-crash',
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

/** Інший `409` (небезпечна роль без підтвердження) — не про останнього адміністратора. */
const OtherConflict = {
  type: 'about:blank',
  title: 'Conflict',
  status: 409,
  detail: null,
  errorCode: 'ECR-SEC-0409',
  correlationId: 'cid-other',
  messageKey: 'err.ECR-SEC-0409.dangerousRoleNeedsConfirmation',
};

function serve(putAnswer: unknown, putStatus: number): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const path = String(input).split('?')[0] ?? '';
      const method = init?.method ?? 'GET';
      const json = (body: unknown, status = 200): Response =>
        new Response(JSON.stringify(body), {
          status,
          headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
        });

      if (path.endsWith('/api/v1/users/7/roles')) {
        return method === 'PUT' ? json(putAnswer, putStatus) : json(['SecurityAdmin']);
      }

      return json([]);
    }),
  );
}

function show(onClose: () => void = () => {}): void {
  render(
    <MantineProvider theme={testTheme}>
      <Notifications />
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <UserAccessEditor user={user} roles={roles} onClose={onClose} />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

async function rolesInput(): Promise<HTMLInputElement> {
  // Поле наповнене призначеними ролями — форма готова.
  await screen.findByText('SecurityAdmin');
  return screen.getByRole('textbox', { name: /security\.roles/ }) as HTMLInputElement;
}

async function save(): Promise<void> {
  const button = screen.getByRole('button', { name: '⟦common.save⟧' }) as HTMLButtonElement;
  await waitFor(() => expect(button.disabled).toBe(false));
  fireEvent.click(button);
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('UserAccessEditor — останній адміністратор', () => {
  it('409 lastAdministrator — помилка під полем ролей, aria-invalid і опис; діалог відкритий', async () => {
    serve(LastAdmin, 409);
    const onClose = vi.fn();
    show(onClose);

    const input = await rolesInput();
    await save();

    await waitFor(() => expect(input.getAttribute('aria-invalid')).toBe('true'));
    const describedBy = input.getAttribute('aria-describedby') ?? '';
    const described = describedBy
      .split(' ')
      .map((id) => document.getElementById(id)?.textContent ?? '')
      .join(' ');
    // Текст — той самий, що й у тості (`problemText`): у тестовому каталозі ключа немає, тож назва.
    expect(described).toMatch(/Conflict/);
    expect(onClose).not.toHaveBeenCalled();
  });

  it('позначка зникає, щойно ролі змінюють', async () => {
    serve(LastAdmin, 409);
    show();

    const input = await rolesInput();
    await save();
    await waitFor(() => expect(input.getAttribute('aria-invalid')).toBe('true'));

    // Зняти роль-чіп — це зміна набору ролей.
    const remove = document.querySelector('.mantine-Pill-remove, [data-pill-remove], button[aria-hidden][tabindex="-1"]');
    if (remove instanceof HTMLElement) {
      fireEvent.click(remove);
    } else {
      fireEvent.keyDown(input, { key: 'Backspace' });
    }

    await waitFor(() => expect(input.getAttribute('aria-invalid')).not.toBe('true'));
  });

  it('інша відмова (500) поле ролей помилковим не робить', async () => {
    serve(Crash, 500);
    show();

    const input = await rolesInput();
    await save();

    await waitFor(() => expect(document.body.textContent ?? '').toMatch(/ECR-SYS-0500|unexpected/));
    expect(input.getAttribute('aria-invalid')).not.toBe('true');
  });

  it('інший 409 (не останній адміністратор) поле ролей помилковим не робить', async () => {
    serve(OtherConflict, 409);
    show();

    const input = await rolesInput();
    await save();

    await waitFor(() => expect(document.body.textContent ?? '').toMatch(/Conflict|ECR-SEC-0409/));
    expect(input.getAttribute('aria-invalid')).not.toBe('true');
  });
});
