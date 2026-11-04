import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider, Menu } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createMemoryRouter } from 'react-router-dom';
import type { DocumentSummary } from '@/api/types';
import { useEditAssignmentsAction } from '@/features/editAssignments/EditAssignmentsAction';
import { testTheme } from '@/test/render';
import { DocumentId, mockServer, type Sent } from './fixture';

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

function Harness(): JSX.Element {
  const action = useEditAssignmentsAction({ documentId: DocumentId, document: Document, sheets: [] });

  return (
    <div>
      <Menu opened withinPortal={false}>
        <Menu.Target>
          <span>menu</span>
        </Menu.Target>
        <Menu.Dropdown>{action.menuItem}</Menu.Dropdown>
      </Menu>
      {action.drawer}
    </div>
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
});

/**
 * ⛔ e2e `editAssignments.spec.ts` («майстер закривається через Cancel, X і Esc; шторка лишається»): Esc у
 * майстрі ловлять ОБИДВА Mantine-вікна (`Modal` майстра і `Drawer` шторки слухають `keydown` на `window`), тож
 * одна клавіша кликала і `setDialog(null)`, і `setPanel(null)`. Доки сеттери `useUrlState` будували адресу зі
 * знімка останнього рендеру, другий виклик відкочував перший і шторка «випадково» лишалась; щойно сеттери
 * почали компонуватися, Esc закривав і шторку. Корінь — шторка не мала слухати Esc під вкладеним вікном.
 */
describe('шторка «Edit access» під дата-роутером (як у застосунку)', () => {
  it('Esc у майстрі закриває лише майстер, шторка й `?panel=` лишаються', async () => {
    mockServer([] as Sent[], { grant: 'Write' });
    const router = createMemoryRouter([{ path: '/documents/:id', element: <Harness /> }], {
      initialEntries: ['/documents/42?periodKey=202601'],
    });
    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <RouterProvider router={router} />
        </QueryClientProvider>
      </MantineProvider>,
    );

    fireEvent.click(await screen.findByRole('menuitem', { name: '⟦editAssign.menu⟧' }));

    for (let round = 1; round <= 3; round += 1) {
      fireEvent.click(await screen.findByRole('button', { name: '⟦editAssign.assign⟧' }));
      const body = await screen.findByTestId('wizard-step-body');
      fireEvent.keyDown(body, { key: 'Escape', code: 'Escape' });

      await waitFor(() => expect(screen.queryByTestId('wizard-step-body')).toBeNull());
      await act(async () => {
        await new Promise((resolve) => setTimeout(resolve, 50));
      });
      const search = new URLSearchParams(router.state.location.search);
      expect(search.get('dialog'), `коло ${String(round)}`).toBeNull();
      expect(search.get('panel'), `коло ${String(round)}: шторка лишилась`).toBe('edit-assignments');
      expect(search.get('periodKey')).toBe('202601');
    }
  });
});
