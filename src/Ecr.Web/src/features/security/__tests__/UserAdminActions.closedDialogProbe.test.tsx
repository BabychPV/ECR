import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, within } from '@testing-library/react';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { UserView } from '@/api/types';
import { UserAdminActions } from '@/features/security/UserAdminActions';
import { holdWrites, renderKeepMounted } from '@/test/closedDialogProbe';
import { gateReads, probeThreeWindows } from '@/test/stillOpenProbe';

/**
 * B149, група «скидання після успіху → кнопка вимкнена»: «Reset password» і «Correct SID» на рядку
 * користувача, що закрилися (ще в DOM) або ще чекають перечитування переліку, не шлють запит удруге.
 *
 * Зонд (справжній компонент, `keepMountedModalsTheme`, запис тримає тест, перечитування `['users']` — ворота):
 * - Reset password — вікно 1: `loading={reset.isPending}` (вимкнена; неявне подання форми з вимкненою кнопкою
 *   браузер теж не робить); вікно 2: `closeReset()` стоїть ДО `await refresh()` — діалог уже закритий, поле
 *   пароля порожнє; вікно 3: `password.length === 0` → `disabled`, і обробник `onSubmit` теж нічого не шле.
 *   ЗАКРИТО, один `POST /users/42/reset-password`.
 * - Correct SID — вікно 1: спінер `usePendingLoading` ще вимкнений, стримує `!fixSid.isPending` в обробнику;
 *   вікна 2–3: `closeSid()` до очікування, SID порожній → `disabled`. ЗАКРИТО, один `PUT /users/42/windows-sid`.
 */

function UsersList(): null {
  useQuery({ queryKey: ['users'], queryFn: () => apiFetch<unknown>('/api/v1/users') });
  return null;
}

function user(overrides: Partial<UserView>): UserView {
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

function read(url: string): unknown {
  if (url.endsWith('/api/v1/me')) {
    return {
      userId: 7,
      userName: 'admin',
      language: 'en',
      permissions: ['Security.ManageUsers'],
      isSimulation: false,
      denies: [],
      grants: {},
      mustChangePassword: false,
      simulatedForUserId: null,
    };
  }

  return [];
}

const isOpen = (): boolean => screen.queryByRole('dialog') !== null;

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: UserAdminActions — один запит на відкриття діалогу', () => {
  it('Reset password: один POST …/reset-password у всіх трьох вікнах', async () => {
    const server = holdWrites({ read, write: () => ({ status: 204 }) });
    const gate = gateReads();
    renderKeepMounted(
      <>
        <UsersList />
        <UserAdminActions user={user({})} />
      </>,
    );

    fireEvent.click(await screen.findByRole('button', { name: /security\.resetPasswordNamed/ }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('⟦security.newPassword⟧'), { target: { value: 'Probe-only-1' } });
    const submit = within(dialog).getByRole('button', { name: '⟦security.resetPassword⟧' });

    await probeThreeWindows({
      server,
      gate,
      button: () => submit,
      isOpen,
      expected: ['POST /api/v1/users/42/reset-password'],
      awaitedRead: '/api/v1/users',
      openWhileAwaiting: false,
    });
  });

  it('Correct SID: один PUT …/windows-sid у всіх трьох вікнах', async () => {
    const server = holdWrites({ read, write: () => ({ status: 204 }) });
    const gate = gateReads();
    renderKeepMounted(
      <>
        <UsersList />
        <UserAdminActions user={user({ provider: 'Windows' })} />
      </>,
    );

    fireEvent.click(await screen.findByRole('button', { name: /security\.correctSidNamed/ }));
    const dialog = await screen.findByRole('dialog');
    fireEvent.change(within(dialog).getByLabelText('⟦security.sidLabel⟧'), { target: { value: 'S-1-5-21-1-2-3-1001' } });
    const submit = within(dialog).getByRole('button', { name: '⟦security.correctSid⟧' });

    await probeThreeWindows({
      server,
      gate,
      button: () => submit,
      isOpen,
      expected: ['PUT /api/v1/users/42/windows-sid'],
      awaitedRead: '/api/v1/users',
      openWhileAwaiting: false,
    });
    expect(server.bodies).toEqual([{ sid: 'S-1-5-21-1-2-3-1001' }]);
  });
});
