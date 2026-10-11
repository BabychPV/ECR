import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider, Menu } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { DocumentSummary } from '@/api/types';
import { useEditAssignmentsAction } from '@/features/editAssignments/EditAssignmentsAction';
import { testTheme } from '@/test/render';
import { DocumentId, mockServer, type Sent } from './fixture';

/** Пункт «More → Edit access»: за грантом Write на проєкт; клік відкриває шторку через `?panel=`. */

const Document: DocumentSummary = {
  businessKey: 'DOC-0042',
  createdAt: '2026-01-01T00:00:00Z',
  id: DocumentId,
  nameL10n: null,
  projectId: 7,
  sheetCount: 1,
  sheetStates: { GEN: 'Draft' },
  hasLateEdits: false,
  ownerDisplayName: 'Owner Olena',
  canManageEditAccess: true,
};

function Search(): JSX.Element {
  return <span data-testid="search">{useLocation().search}</span>;
}

function Harness({ document = Document }: { document?: DocumentSummary }): JSX.Element {
  const action = useEditAssignmentsAction({ documentId: DocumentId, document, sheets: [] });

  return (
    <div>
      <Menu opened withinPortal={false}>
        <Menu.Target>
          <span>menu</span>
        </Menu.Target>
        <Menu.Dropdown>{action.menuItem}</Menu.Dropdown>
      </Menu>
      {action.drawer}
      <Search />
    </div>
  );
}

function show(grant: string, options: { simulation?: boolean; document?: DocumentSummary } = {}): void {
  mockServer([] as Sent[], { grant, ...(options.simulation === undefined ? {} : { simulation: options.simulation }) });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <MemoryRouter>
          <Harness {...(options.document === undefined ? {} : { document: options.document })} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('useEditAssignmentsAction', () => {
  it('сервер каже «не керує» (canManageEditAccess=false) - пункту немає, хоч би грант був Manage', async () => {
    show('Manage', { document: { ...Document, canManageEditAccess: false } });
    await none();
  });

  it('поле відсутнє (старий сервер) - пункту немає', async () => {
    const { canManageEditAccess: _omit, ...legacy } = Document;
    show('Manage', { document: legacy });
    await none();
  });

  const none = async (): Promise<void> => {
    await waitFor(() => expect(vi.mocked(fetch)).toHaveBeenCalled());
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(screen.queryByRole('menuitem', { name: '⟦editAssign.menu⟧' })).toBeNull();
    cleanup();
  };

  it('у симуляції пункту немає, навіть із грантом Manage', async () => {
    show('Manage', { simulation: true });
    await none();
  });

  it('canManageEditAccess=true: пункт є; клік пише ?panel=edit-assignments і відкриває шторку з даними', async () => {
    show('Write');

    fireEvent.click(await screen.findByRole('menuitem', { name: '⟦editAssign.menu⟧' }));

    await waitFor(() => expect(screen.getByTestId('search').textContent).toBe('?panel=edit-assignments'));
    expect(await screen.findByRole('rowheader', { name: 'January' })).toBeDefined();
  });
});
