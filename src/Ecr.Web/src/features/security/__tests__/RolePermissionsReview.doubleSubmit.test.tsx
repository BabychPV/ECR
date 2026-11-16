import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import type { RolePermissionsPreview, RoleView } from '@/api/types';
import { RolePermissionsReview } from '@/features/security/RolePermissionsReview';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';

/**
 * B149: «Review and save» прав ролі — один `PUT /roles/{id}/permissions` на збереження.
 *
 * Зонд (база 826f654e4):
 * - закриття — ЗАКРИТО: власник (`RoleBrowser`) монтує огляд лише за `{draft !== null && reviewing && …}` і знімає його
 *   в `onSaved`; сам огляд — `<Modal opened>` без закритого стану;
 * - подвійний клік — ВІДКРИТО: «Save» кликав `save.mutate` без перевірки `isPending`, а спінер — лише через 100 мс
 *   (`usePendingLoading`): другий клік (наступний тік) слав другий `PUT /api/v1/roles/11/permissions` з тим самим
 *   `If-Match` (сервер відмовив би 409 «набір змінили» — після власного успішного запису).
 *
 * Мутаційний доказ: прибрати `if (save.isPending) return;` у `RolePermissionsReview.tsx` → червоний
 * (`expected [ …(3) ] to deeply equal [ …(2) ]`).
 */

const Preview = 'POST /api/v1/roles/11/permissions/preview';
const Save = 'PUT /api/v1/roles/11/permissions';

const Role: RoleView = {
  id: 11,
  code: 'LandRestricted',
  nameL10n: { values: { en: 'Land restricted' } },
  isActive: true,
  isBuiltIn: false,
  permissions: ['Document.View'],
  dangerousPermissions: [],
};

const PreviewBody: RolePermissionsPreview = {
  roleId: 11,
  roleCode: 'LandRestricted',
  version: 'V1',
  current: ['Document.View'],
  added: ['Document.Edit'],
  removed: [],
  addedDangerous: [],
  users: [],
  groups: [],
};

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: RolePermissionsReview — один PUT на збереження', () => {
  it('подвійний клік шле один PUT; після успіху огляд знято з дерева', async () => {
    const server = holdWrites({ write: (_method, url) => (url.endsWith('/preview') ? { body: PreviewBody } : { status: 204 }) });

    // Власник (`RoleBrowser`) знімає огляд з дерева в `onSaved`.
    function Host(): JSX.Element | null {
      const [open, setOpen] = useState(true);

      return open ? (
        <RolePermissionsReview role={Role} base={['Document.View']} next={['Document.View', 'Document.Edit']} onClose={() => setOpen(false)} onSaved={() => setOpen(false)} />
      ) : null;
    }

    renderKeepMounted(<Host />);
    await waitFor(() => {
      expect(server.sent).toEqual([Preview]);
    });
    server.release();

    const save = await screen.findByRole('button', { name: /security\.savePermissions/ });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });

    fireEvent.click(save);
    await waitFor(() => {
      expect(server.sent).toEqual([Preview, Save]);
    });
    await nextTick();
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual([Preview, Save]);

    server.release();
    await waitFor(() => {
      expect(save.isConnected).toBe(false);
    });
    expect(server.sent).toEqual([Preview, Save]);
  });
});
