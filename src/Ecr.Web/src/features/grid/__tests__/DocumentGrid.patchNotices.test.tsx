import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * Зауваження `Warning`/`Info` УСПІШНОГО патчу (`PatchCellsResponse.validation`)
 * доходять до оператора. До фіксу сітка брала звідти лише `ECR-CALC-0437`, а
 * решту відкидала мовчки. Заглушка RevoGrid — та сама, що й у
 * `DocumentGrid.saveError.test.tsx`.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: (props: { onAfteredit?: (event: { detail: unknown }) => void }) => (
    <div data-testid="revogrid-stub">
      <button
        type="button"
        onClick={() =>
          props.onAfteredit?.({
            detail: { prop: 'C1', model: { __rowKey: 'r1' }, val: 'abc' },
          })
        }
      >
        simulate-edit
      </button>
    </div>
  ),
}));

function sliceFixture(): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: {},
    periodKey: 202609,
    tableInstanceId: 1,
    columns: [
      {
        code: 'C1',
        dataType: 'Decimal',
        defaultValue: null,
        displayFormat: null,
        header: 'Колонка 1',
        id: 1,
        isReadOnly: false,
        isRequired: false,
        isRequiredByMethodology: false,
        lookupRegistryDefId: null,
        ordinal: 0,
        unitId: null,
        unitSymbol: null,
      },
    ],
    rows: [
      {
        cells: { C1: 321 },
        isOrphaned: false,
        label: null,
        ordinal: 0,
        rowKey: 'r1',
        rowKind: 'Item',
        rowVersion: 'v1',
      },
    ],
  };
}

const WARNING = 'Значення C1 вище за минулий рік на 40 %.';
const INFO = 'Таблиця ще не перевірена цілком.';
const REQUIRED = 'Колонка «Години роботи» обов\'язкова для методології.';

function mockServer(validation: unknown[]): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'PATCH') {
        return new Response(JSON.stringify({ appliedCells: 1, rowVersions: { r1: 'v2' }, validation }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        });
      }

      return new Response(JSON.stringify(sliceFixture()), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      });
    }),
  );
}

function show(): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  render(
    <MantineProvider>
      <QueryClientProvider client={client}>
        <DocumentGrid
          documentId={1}
          tableInstanceId={1}
          tableDefId={1}
          periodKey={202609}
          readOnly={false}
          allowsDynamicRows={false}
          maxDynamicRows={null}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

afterEach(() => {
  cancelAutosave();
  resetPending();
  vi.unstubAllGlobals();
});

describe('DocumentGrid: Warning/Info успішного патчу видно', () => {
  it('показує Warning та Info, а 0437 — лише власним блоком', async () => {
    mockServer([
      { ruleCode: 'R-TREND', severity: 'Warning', message: WARNING, rowKey: 'r1', columnCode: 'C1' },
      { ruleCode: 'R-INFO', severity: 'Info', message: INFO, rowKey: null, columnCode: null },
      { ruleCode: 'ECR-CALC-0437', severity: 'Warning', message: REQUIRED, rowKey: 'r1', columnCode: 'C1' },
    ]);
    show();
    await screen.findByTestId('revogrid-stub');

    const user = userEvent.setup();
    await user.click(screen.getByRole('button', { name: 'simulate-edit' }));

    const notices = await waitFor(
      () => {
        const node = document.querySelector<HTMLElement>('[data-patch-notices]');
        expect(node).not.toBeNull();
        return node as HTMLElement;
      },
      { timeout: 5_000 },
    );
    expect(notices.getAttribute('role')).toBe('alert');
    expect(notices.getAttribute('data-patch-notices')).toBe('Warning');
    expect(within(notices).getByText(WARNING)).toBeTruthy();
    expect(within(notices).getByText(INFO)).toBeTruthy();
    // 0437 має власний блок «обов'язкові вхідні» — не дублюється тут.
    expect(within(notices).queryByText(REQUIRED)).toBeNull();
    expect(screen.getAllByText(REQUIRED)).toHaveLength(1);

    // Закрити можна; більше нічого не з'являється без нового патчу.
    await user.click(within(notices).getByRole('button'));
    expect(document.querySelector('[data-patch-notices]')).toBeNull();
  });

  it('патч без зауважень нічого не показує', async () => {
    mockServer([]);
    show();
    await screen.findByTestId('revogrid-stub');

    await userEvent.setup().click(screen.getByRole('button', { name: 'simulate-edit' }));
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([, init]) => init?.method === 'PATCH')).toBe(true), {
      timeout: 5_000,
    });

    expect(document.querySelector('[data-patch-notices]')).toBeNull();
  });
});
