import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor, within } from '@testing-library/react';
import { EditAssignmentsDrawer } from '@/features/editAssignments/EditAssignmentsDrawer';
import { OwnerTransferModal, ParticipantModal } from '@/features/editAssignments/ParticipantDialogs';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { January, February } from './fixture';

/**
 * B149: «Add participant» і «Transfer ownership» — один запит на відкриття.
 *
 * Зонд на рівні власника (`EditAssignmentsDrawer`, база 826f654e4):
 * - закриття — ЗАКРИТО: власник знімає обидва діалоги з дерева (`{participantOpen ? … : null}`), кнопка від'єднана;
 * - подвійний клік — ВІДКРИТО: обробник кнопки не дивився на `isPending`, а спінер вмикається лише через 100 мс
 *   (`usePendingLoading`), тож другий клік (наступний тік) слав другий `POST …/edit-assignments` / `POST …/owner`.
 *
 * Захист — у діалогах (`if (!opened || isPending …) return;`).
 *
 * Другий шар — `!opened` у тих самих обробниках: третій тест тримає діалоги закритими в DOM (мутація: прибрати
 * `!opened ||` з обох → червоний `expected "vi.fn()" to not be called at all, but actually been called 1 times`).
 *
 * Мутаційні докази: прибрати `|| isPending` з обробника «Grant» / «Transfer» у `ParticipantDialogs.tsx` → червоний
 * відповідний тест (`expected [ …(2) ] to deeply equal [ Array(1) ]` /
 * `… to deeply equal [ 'POST /api/v1/documents/42/owner' ]`).
 */

const Upsert = 'POST /api/v1/documents/42/edit-assignments';
const Transfer = 'POST /api/v1/documents/42/owner';

function read(url: string): unknown {
  if (url.endsWith('/api/v1/me')) {
    return {
      denies: [], grants: { 'Project:7': 'Write' }, isSimulation: false, language: 'en', mustChangePassword: false,
      permissions: ['Document.View'], simulatedForUserId: null, userId: 1, userName: 'owner',
    };
  }
  if (url.includes('/edit-assignments/candidates')) return [{ userId: 11, displayName: 'Koval' }];
  if (url.endsWith('/edit-assignments')) return { ownerDisplayName: 'Owner Olena', canManage: true, assignments: [January, February] };

  return [];
}

function write(_method: string, url: string): { body: unknown } {
  return url.endsWith('/owner')
    ? { body: { documentId: 42, ownerUserId: 11, ownerDisplayName: 'Koval', previousOwnerUserId: 1 } }
    : { body: { ...January, id: 99 } };
}

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

async function pickKoval(container: HTMLElement): Promise<void> {
  fireEvent.change(within(container).getByLabelText('⟦editAssign.participant.who⟧'), { target: { value: 'Ko' } });
  fireEvent.click(await within(container).findByRole('button', { name: 'Koval' }));
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: діалоги учасника й власника — один запит на відкриття', () => {
  it('Add participant: подвійний клік шле один POST; після успіху діалог знято з дерева', async () => {
    const server = holdWrites({ read, write });
    renderKeepMounted(<EditAssignmentsDrawer documentId={42} sheets={[]} />, '/?panel=edit-assignments');

    fireEvent.click(await screen.findByTestId('add-participant'));
    const modal = await screen.findByTestId('participant-modal');
    await pickKoval(modal);
    const grant = within(modal).getByRole('button', { name: '⟦editAssign.participant.grant⟧' });

    fireEvent.click(grant);
    await waitFor(() => {
      expect(server.sent).toEqual([Upsert]);
    });
    await nextTick();
    fireEvent.click(grant);
    await settle();
    expect(server.sent).toEqual([Upsert]);

    server.release();
    await waitFor(() => {
      expect(grant.isConnected).toBe(false);
    });
    expect(server.sent).toEqual([Upsert]);
  });

  it('Transfer ownership: подвійний клік шле один POST; після успіху діалог знято з дерева', async () => {
    const server = holdWrites({ read, write });
    renderKeepMounted(<EditAssignmentsDrawer documentId={42} sheets={[]} />, '/?panel=edit-assignments');

    fireEvent.click(await screen.findByTestId('transfer-owner'));
    const modal = await screen.findByTestId('owner-transfer-modal');
    await pickKoval(modal);
    const confirm = within(modal).getByRole('button', { name: '⟦editAssign.owner.confirm⟧' });

    fireEvent.click(confirm);
    await waitFor(() => {
      expect(server.sent).toEqual([Transfer]);
    });
    await nextTick();
    fireEvent.click(confirm);
    await settle();
    expect(server.sent).toEqual([Transfer]);

    server.release();
    await waitFor(() => {
      expect(confirm.isConnected).toBe(false);
    });
    expect(server.sent).toEqual([Transfer]);
  });

  // Другий шар (не залежить від власника): діалог, лишений у дереві закритим, нічого не подає.
  it('закриті ParticipantModal / OwnerTransferModal (ще в DOM) не викликають onSubmit', async () => {
    holdWrites({ read });
    const grant = vi.fn();
    const transfer = vi.fn();

    function Host(): JSX.Element {
      const [opened, setOpened] = useState(true);

      return (
        <>
          <ParticipantModal opened={opened} documentId={42} sheets={[]} isPending={false} error={null} onSubmit={grant} onClose={() => setOpened(false)} />
          <OwnerTransferModal opened={opened} documentId={42} isPending={false} error={null} onSubmit={transfer} onClose={() => setOpened(false)} />
          <button type="button" onClick={() => setOpened(false)}>
            close both
          </button>
        </>
      );
    }

    renderKeepMounted(<Host />);
    await pickKoval(await screen.findByTestId('participant-modal'));
    await pickKoval(await screen.findByTestId('owner-transfer-modal'));
    const grantButton = within(screen.getByTestId('participant-modal')).getByRole('button', { name: '⟦editAssign.participant.grant⟧' });
    const transferButton = within(screen.getByTestId('owner-transfer-modal')).getByRole('button', { name: '⟦editAssign.owner.confirm⟧' });

    fireEvent.click(screen.getByRole('button', { name: 'close both' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });

    expect(grantButton.isConnected && transferButton.isConnected).toBe(true);
    fireEvent.click(grantButton);
    fireEvent.click(transferButton);
    await settle();
    expect(grant).not.toHaveBeenCalled();
    expect(transfer).not.toHaveBeenCalled();
  });
});
