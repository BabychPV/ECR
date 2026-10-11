import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import { mantineProviderProps } from '@/shared/theme/provider';
import { EditAssignmentsDrawer } from '@/features/documents/editAssignments/EditAssignmentsDrawer';
import { Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { DocumentId, mockServer, type Sent } from './fixture';

/** Доступність шторки «Доступ до редагування» (матриця, подання People, майстер) в обох темах (`ФВ-14.16`). */

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('EditAssignmentsDrawer — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: матриця, People і крок майстра — без блокуючих порушень`, async () => {
      mockServer([] as Sent[]);
      render(
        <MantineProvider {...mantineProviderProps} forceColorScheme={colorScheme}>
          <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
            <MemoryRouter initialEntries={['/?panel=edit-assignments']}>
              <EditAssignmentsDrawer documentId={DocumentId} sheets={[{ code: 'AIR', name: 'Air emissions' }]} />
            </MemoryRouter>
          </QueryClientProvider>
        </MantineProvider>,
      );

      await screen.findByTestId('edit-assignments-months');
      const months = await findViolations(screen.getByRole('dialog'));
      expect(months, describeViolations(months)).toHaveLength(0);

      fireEvent.click(screen.getByRole('radio', { name: '⟦editAssign.view.people⟧' }));
      await screen.findByTestId('edit-assignments-people');
      const people = await findViolations(screen.getByRole('dialog'));
      expect(people, describeViolations(people)).toHaveLength(0);

      fireEvent.click(screen.getByRole('button', { name: '⟦editAssign.assign⟧' }));
      await screen.findByTestId('wizard-step-body');
      const wizard = await findViolations(screen.getAllByRole('dialog').at(-1) as HTMLElement);
      expect(wizard, describeViolations(wizard)).toHaveLength(0);
    });
  }
});
