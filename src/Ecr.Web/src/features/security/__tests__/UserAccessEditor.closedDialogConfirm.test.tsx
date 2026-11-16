import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import type { RoleView, UserView } from '@/api/types';
import { UserAccessEditor } from '@/features/security/UserAccessEditor';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';

/**
 * B145b: діалог «Access», що вже закрився, не шле запит на користувача `0`.
 *
 * ⚠ Зонд (справжній діалог, власник тримає його в дереві завжди — як `SecurityPage`): ЗАКРИТО, виправлення немає.
 * Після закриття `user = null`, а шлях запиту читається в мить кліку (`user?.id ?? 0`) — тобто незахищена кнопка
 * слала б `PUT /users/0/roles` і `PUT /users/0/email`. Не шле, бо «Save» вимкнена: запит призначених ролей без
 * користувача не виконується (`assigned.isPending`), а до того кнопку тримає `loading` (перечитування в `onSettled`).
 *
 * Тест тримає саме цю залежність. Мутаційний доказ: прибрати `assigned.isPending ||` з `disabled` кнопки «Save» →
 * червоний (`expected <button …> to have property "disabled" with value true`): кнопка закритого діалогу активна, а її
 * клік читає `user?.id ?? 0`, тобто цілить у `PUT /api/v1/users/0/roles`.
 */

const User = {
  id: 7,
  userName: 'ivanov',
  displayName: 'Ivanov',
  email: null,
  provider: 'Local',
  isActive: true,
  isBootstrapAdmin: false,
  isLockedOut: false,
  mustChangePassword: false,
  receivesAlerts: false,
  lastSignInAt: null,
} as unknown as UserView;

const Roles = [
  { id: 1, code: 'DataEntry', isActive: true, isBuiltIn: false, dangerousPermissions: [], permissions: ['Document.View'] },
] as unknown as RoleView[];

const Original = ['PUT /api/v1/users/7/roles', 'PUT /api/v1/users/7/email'];

function Host(): JSX.Element {
  const [user, setUser] = useState<UserView | null>(User);

  return <UserAccessEditor user={user} roles={Roles} onClose={() => setUser(null)} />;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B145b: Access, що закрився (ще в DOM), не шле запит на користувача 0', () => {
  it('рівно два запити на відкритого користувача; «Save» закритого діалогу вимкнена й нічого не шле', async () => {
    const server = holdWrites({
      read: (url) => (url.endsWith('/role-assignments') ? [] : url.endsWith('/roles') ? ['DataEntry'] : []),
      write: (_method, url) => (url.endsWith('/roles') ? { body: { roles: 1 } } : { status: 204 }),
    });
    renderKeepMounted(<Host />);

    const save = screen.getByRole('button', { name: '⟦common.save⟧' });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });
    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual(Original.slice(0, 1));
    });
    server.release();
    await waitFor(() => {
      expect(server.sent).toEqual(Original);
    });
    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(save.getAttribute('data-loading')).toBeNull();
    });

    // Кнопка діалогу, що закрився, ще в DOM — і вимкнена: користувача вже немає.
    expect(save.isConnected).toBe(true);
    expect(save).toHaveProperty('disabled', true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(Original);
  });
});
