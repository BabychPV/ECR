import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { createQueryClient } from '@/app/queryClient';
import { EditAssignmentsDrawer } from '@/features/editAssignments/EditAssignmentsDrawer';
import { testTheme } from '@/test/render';
import { assignment, January, mockServer, type Sent, type ServerOptions } from './fixture';

/**
 * D-443 (AN-130): учасники документа в шторці — рівень і область учасника, рівень у матриці, передача власника.
 * Клієнт прав не вирішує: «не вище власного рівня» й заборони ролей — відповіді сервера, тут лише форма запитів.
 *
 * Мутації: не передавати `level` в `upsertEditAssignment` (EditAssignmentsDrawer `grant`) — червоніє «шле рівень і
 * область»; показувати Validate для учасника не-Write (`AssignmentsMatrix`, `handoff &&`) — червоніє «учасник Read
 * без циклу заповнення»; прибрати `transferDocumentOwner` з `transfer` — червоніє «передача власника».
 */

const sent: Sent[] = [];
const Sheets = [
  { code: 'AIR', name: 'Air emissions', tables: [{ tableCode: 'T1', tableNameL10n: { values: { en: 'Boilers' } } }] },
  { code: 'WAT', name: 'Water', tables: [{ tableCode: 'T2', tableNameL10n: { values: { en: 'Wells' } } }] },
];

function show(options: ServerOptions = {}): void {
  sent.length = 0;
  mockServer(sent, options);
  const client = createQueryClient();
  client.setDefaultOptions({ queries: { retry: false } });

  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/?panel=edit-assignments']}>
          <EditAssignmentsDrawer documentId={42} sheets={Sheets} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

beforeEach(() => {
  cleanup();
});
afterEach(() => {
  vi.unstubAllGlobals();
});

async function pickKoval(container: HTMLElement): Promise<void> {
  fireEvent.change(within(container).getByLabelText('⟦editAssign.participant.who⟧'), { target: { value: 'Ko' } });
  fireEvent.click(await within(container).findByRole('button', { name: 'Koval' }));
}

const isUpsert = (s: Sent): boolean => s.method === 'POST' && s.url.endsWith('/edit-assignments');

describe('EditAssignmentsDrawer — учасники документа (D-443)', () => {
  it('«Додати учасника» шле рівень і область без місяця, неексклюзивно', async () => {
    show();
    fireEvent.click(await screen.findByTestId('add-participant'));
    const modal = await screen.findByTestId('participant-modal');

    // Без користувача видати нічого.
    expect((within(modal).getByRole('button', { name: '⟦editAssign.participant.grant⟧' }) as HTMLButtonElement).disabled).toBe(true);

    await pickKoval(modal);
    fireEvent.click(within(modal).getByRole('radio', { name: '⟦editAssign.level.Approve⟧' }));
    fireEvent.click(within(modal).getByRole('button', { name: '⟦editAssign.participant.grant⟧' }));

    await waitFor(() => expect(sent.some(isUpsert)).toBe(true));
    expect(sent.find(isUpsert)?.body).toEqual({
      slice: { sheetCode: null, tableCode: null, monthNumber: null, periodKey: null },
      recipientUserIds: [11],
      isExclusive: false,
      level: 'Approve',
    });
  });

  it('відмову сервера показує в діалозі, діалог лишається', async () => {
    show({ putStatus: 422 });
    fireEvent.click(await screen.findByTestId('add-participant'));
    const modal = await screen.findByTestId('participant-modal');
    await pickKoval(modal);
    fireEvent.click(within(modal).getByRole('button', { name: '⟦editAssign.participant.grant⟧' }));

    expect(await within(modal).findByRole('alert')).toBeDefined();
    expect(screen.getByTestId('participant-modal')).toBeDefined();
  });

  it('у матриці рівень названо словом; учасник Read без циклу заповнення (лише «Зняти»)', async () => {
    const reader = assignment({
      id: 5,
      slice: { sheetCode: 'AIR' },
      status: 'Filled',
      isExclusive: false,
      level: 'Read',
      scope: 'Sheet',
    });
    show({ list: { ownerDisplayName: 'Owner Olena', canManage: true, assignments: [January, reader] } });

    const levels = await screen.findAllByTestId('assignment-level');
    expect(levels.map((l) => l.getAttribute('data-level')).sort()).toEqual(['Read', 'Write']);
    const row = levels.find((l) => l.getAttribute('data-level') === 'Read')?.closest('tr') as HTMLElement;

    fireEvent.click(within(row).getByRole('button', { name: /editAssign\.rowActions/ }));
    await screen.findByRole('menuitem', { name: '⟦editAssign.remove⟧' });
    expect(screen.queryByRole('menuitem', { name: '⟦editAssign.validate⟧' })).toBeNull();
    expect(screen.queryByRole('menuitem', { name: '⟦editAssign.edit⟧' })).toBeNull();
    expect(screen.queryByRole('menuitem', { name: '⟦editAssign.return⟧' })).toBeNull();
  });

  it('передача власника шле нового власника й називає його в результаті', async () => {
    show();
    fireEvent.click(await screen.findByTestId('transfer-owner'));
    const modal = await screen.findByTestId('owner-transfer-modal');
    await pickKoval(modal);
    fireEvent.click(within(modal).getByRole('button', { name: '⟦editAssign.owner.confirm⟧' }));

    await waitFor(() => expect(sent.some((s) => s.url.endsWith('/owner'))).toBe(true));
    expect(sent.find((s) => s.url.endsWith('/owner'))?.body).toEqual({ newOwnerUserId: 11 });
    await waitFor(() =>
      expect(screen.getByTestId('edit-assignments-live').textContent).toBe('⟦editAssign.done.ownerTransferred (name=Koval)⟧'),
    );
  });

  it('у симуляції — ні «Додати учасника», ні «Передати власність»', async () => {
    show({ simulation: true });
    await screen.findAllByTestId('assignment-level');
    expect(screen.queryByTestId('add-participant')).toBeNull();
    expect(screen.queryByTestId('transfer-owner')).toBeNull();
  });
});
