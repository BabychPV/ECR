import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { CreateRoleModal } from '@/features/security/CreateRoleModal';
import { CreateUserModal } from '@/features/security/CreateUserModal';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';

/**
 * B145b: діалоги «Create user» / «Create role», що вже закрилися, не створюють удруге.
 *
 * ⛔ Діра (зонд на базі d618ea1f6, справжні діалоги): після відповіді сервера власник закриває діалог
 * (`opened = false`), але власний `Modal` ще ~200 мс у DOM (анімація закриття), форма — з тими самими
 * введеними значеннями (нова форма монтується лише наступним ВІДКРИТТЯМ), кнопка «Save» активна. Другий
 * клік подвійного кліку слав другий `POST /api/v1/users` (`/roles`) з тим самим тілом — дубль створення.
 *
 * Захист — у самих діалогах (власний `Modal`; кореневий захист `shared/ui/ConfirmModal` сюди не дістає):
 * кнопка діє лише у ВІДКРИТОМУ діалозі й не під час запиту.
 *
 * Мутаційні докази (прибрати `!opened ||` з умови кнопки «Save»):
 * - `CreateUserModal.tsx` → червоний «Create user»
 *   (`expected [ 'POST /api/v1/users', …(1) ] to deeply equal [ 'POST /api/v1/users' ]`);
 * - `CreateRoleModal.tsx` → червоний «Create role»
 *   (`expected [ 'POST /api/v1/roles', …(1) ] to deeply equal [ 'POST /api/v1/roles' ]`).
 */

function UserHost(): JSX.Element {
  const [opened, setOpened] = useState(true);

  return (
    <CreateUserModal opened={opened} onClose={() => setOpened(false)} roles={[]} rolesError={null} onRetry={() => undefined} />
  );
}

function RoleHost(): JSX.Element {
  const [opened, setOpened] = useState(true);

  return (
    <CreateRoleModal
      opened={opened}
      onClose={() => setOpened(false)}
      permissions={[]}
      permissionCatalogError={null}
      onRetry={() => undefined}
    />
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B145b: діалог створення, що закрився (ще в DOM), не створює вдруге', () => {
  it('Create user: один POST /users; клік по «Save» закритого діалогу нічого не шле', async () => {
    const server = holdWrites({ write: () => ({ body: { userId: 7 } }) });
    renderKeepMounted(<UserHost />);

    fireEvent.change(screen.getByLabelText('⟦security.login⟧'), { target: { value: 'ivanenko' } });
    const save = screen.getByRole('button', { name: '⟦common.save⟧' });
    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual(['POST /api/v1/users']);
    });

    // Сервер створив, перелік перечитано, діалог закрито, зайнятість знято.
    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(save).toHaveProperty('disabled', false);
    });

    // Клік по кнопці діалогу, що закрився: вона ще в DOM і не вимкнена.
    expect(save.isConnected).toBe(true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['POST /api/v1/users']);
  });

  it('Create role: один POST /roles; клік по «Save» закритого діалогу нічого не шле', async () => {
    const server = holdWrites({
      read: (url) =>
        url.endsWith('/api/v1/languages') ? [{ code: 'en', nameNative: 'English', isDefault: true, isActive: true }] : {},
      write: () => ({ body: { roleId: 9 } }),
    });
    renderKeepMounted(<RoleHost />);

    fireEvent.change(screen.getByLabelText('⟦security.roleCode⟧'), { target: { value: 'NightShift' } });
    // Назва ролі — локалізоване поле (по одному на мову з реєстру мов).
    fireEvent.change(await screen.findByLabelText('⟦security.roleName⟧ · English'), { target: { value: 'Night shift' } });

    const save = screen.getByRole('button', { name: '⟦common.save⟧' });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });
    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual(['POST /api/v1/roles']);
    });

    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(save).toHaveProperty('disabled', false);
    });

    expect(save.isConnected).toBe(true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual(['POST /api/v1/roles']);
  });
});
