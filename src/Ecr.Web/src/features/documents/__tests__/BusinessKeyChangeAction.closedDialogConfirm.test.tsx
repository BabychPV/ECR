import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { Menu } from '@mantine/core';
import type { DocumentSummary } from '@/api/types';
import { useBusinessKeyChangeAction } from '@/features/documents/BusinessKeyChangeAction';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';

/**
 * B149: «Change case number», що закрився після відмови `rekeyStale`, не змінює ключ удруге.
 *
 * ⛔ Зонд на рівні власника (`useBusinessKeyChangeAction`, база 826f654e4): після першого відкриття власник лишає діалог
 * змонтованим (`everOpened`), а `rekeyStale` закриває його (`opened = false`). Закритий `Modal` ще ~200 мс у DOM, поля
 * (новий ключ і причина) на місці, `canSubmit` істинний — клік слав другий
 * `POST /api/v1/documents/42/business-key` з тим самим тілом уже після закриття.
 *
 * Захист — у самому діалозі: «Change» діє лише у відкритому діалозі й не під час запиту.
 *
 * Мутаційний доказ: прибрати `!opened ||` у `BusinessKeyChangeDialog.tsx` → червоний
 * (`expected [ …(2) ] to deeply equal [ Array(1) ]`).
 */

const DocumentId = 42;

const Doc: DocumentSummary = {
  businessKey: 'DOC-0042',
  createdAt: '2026-01-01T00:00:00Z',
  id: DocumentId,
  nameL10n: null,
  projectId: 7,
  sheetCount: 1,
  sheetStates: { GEN: 'Draft' },
  hasLateEdits: false,
  stepApproverNamesTruncated: false,
};

const Me = {
  denies: [],
  grants: { 'Project:7': 'Write' },
  isSimulation: false,
  language: 'en',
  mustChangePassword: false,
  permissions: ['Document.ChangeKey'],
  simulatedForUserId: null,
  userId: 9,
  userName: 'tester',
};

// ⚠ Код і ключ відмови — з каталогу (`CATALOG-en-base.sql`).
const Stale = {
  status: 409,
  body: {
    title: 'Conflict',
    status: 409,
    errorCode: 'ECR-DOC-0409',
    correlationId: 'cid-stale',
    detail: 'The document key has changed since it was read; it is now "DOC-9000".',
    messageKey: 'err.ECR-DOC-0409.rekeyStale',
    businessKey: 'DOC-9000',
  },
};

function Harness(): JSX.Element {
  const action = useBusinessKeyChangeAction({ documentId: DocumentId, document: Doc, periodKey: 202601 });

  return (
    <div>
      <Menu opened withinPortal={false}>
        <Menu.Target>
          <span>menu</span>
        </Menu.Target>
        <Menu.Dropdown>{action.menuItem}</Menu.Dropdown>
      </Menu>
      {action.dialog}
    </div>
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

async function openAndFill(): Promise<HTMLElement> {
  const item = await waitFor(() => {
    const found = document.querySelector<HTMLElement>('[data-change-business-key]');
    if (found === null) throw new Error('пункту ще немає');
    return found;
  });
  fireEvent.click(item);
  fireEvent.change(await screen.findByLabelText(/documents\.newBusinessKey/), { target: { value: 'DOC-NEW' } });
  fireEvent.change(screen.getByLabelText(/workflow\.reason/), { target: { value: 'Typo in the case number' } });

  return screen.getByTestId('business-key-confirm');
}

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

const Rekey = `POST /api/v1/documents/${String(DocumentId)}/business-key`;

describe('B149: Change case number — подвійний клік, поки запис у дорозі', () => {
  /*
   * ⚠ Спінер тут — НЕ `usePendingLoading` (100 мс): `loading={isPending}` і `canSubmit` (з `!change.isPending`) вимикають
   * кнопку в тому ж рендері, що й приносить `isPending` у захист обробника. Тому захист `isPending` в обробнику —
   * другий шар (F13, мутації зі збіркою OK): без нього — зелено; без нього й без `loading` — зелено (тримає
   * `canSubmit`); без нього й без `disabled` — зелено (тримає `loading`); без усіх трьох — червоно (два POST).
   */
  it('другий клік, поки запит у дорозі, не шле другий POST', async () => {
    const server = holdWrites({ read: (url) => (url.endsWith('/api/v1/me') ? Me : {}), write: () => ({ body: {} }) });
    renderKeepMounted(<Harness />);
    const confirm = await openAndFill();

    fireEvent.click(confirm);
    await waitFor(() => {
      expect(server.sent).toEqual([Rekey]);
    });
    await nextTick();
    fireEvent.click(confirm);
    await settle();
    expect(server.sent).toEqual([Rekey]);
    server.release();
  });
});

describe('B149: Change case number, що закрився (ще в DOM), не шле запит удруге', () => {
  it('після rekeyStale діалог закрито; клік по його кнопці нічого не шле', async () => {
    const server = holdWrites({ read: (url) => (url.endsWith('/api/v1/me') ? Me : {}), write: () => Stale });
    renderKeepMounted(<Harness />);

    const item = await waitFor(() => {
      const found = document.querySelector<HTMLElement>('[data-change-business-key]');
      if (found === null) throw new Error('пункту ще немає');
      return found;
    });
    fireEvent.click(item);
    fireEvent.change(await screen.findByLabelText(/documents\.newBusinessKey/), { target: { value: 'DOC-NEW' } });
    fireEvent.change(screen.getByLabelText(/workflow\.reason/), { target: { value: 'Typo in the case number' } });

    const confirm = screen.getByTestId('business-key-confirm');
    fireEvent.click(confirm);
    await waitFor(() => {
      expect(server.sent).toEqual([`POST /api/v1/documents/${String(DocumentId)}/business-key`]);
    });

    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(confirm).toHaveProperty('disabled', false);
    });

    expect(confirm.isConnected).toBe(true);
    fireEvent.click(confirm);
    await settle();
    expect(server.sent).toEqual([`POST /api/v1/documents/${String(DocumentId)}/business-key`]);
  });
});
