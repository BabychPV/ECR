import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { cellKey } from '../permissions';
import { DocumentGrid } from '../DocumentGrid';

/**
 * D9 (приймальна Land): читач без гранту на жодну вхідну комірку не може додати рядок - «Add row» вимкнена, причина
 * названа підказкою (`title`) і зв'язана через `aria-describedby` з існуючим елементом.
 *
 * Мутація: прибрати `addRowDenial !== null ||` з `disabled` кнопки в `DocumentGrid.tsx` - перший тест червоніє.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: () => <div data-testid="revogrid-stub" />,
}));

function sliceFixture(permissions: Record<string, string>): TableSliceDto {
  return {
    cellConfirmations: {},
    cellPermissions: permissions,
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
      { cells: { C1: 1 }, isOrphaned: false, label: null, ordinal: 0, rowKey: 'r1', rowKind: 'Item', rowVersion: 'v1' },
    ],
  } as unknown as TableSliceDto;
}

function show(permissions: Record<string, string>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(
      async () =>
        new Response(JSON.stringify(sliceFixture(permissions)), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        }),
    ),
  );
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
          allowsDynamicRows
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

describe('DocumentGrid · «Add row» без гранту (D9)', () => {
  it('вимкнена, з підказкою, aria-describedby вказує на існуючий елемент із тим самим текстом', async () => {
    show({ [cellKey('r1', 'C1')]: 'NoGrant' });

    const button = (await screen.findByText('⟦grid.addRow⟧')).closest('button');
    expect(button).not.toBeNull();
    expect(button?.hasAttribute('disabled')).toBe(true);

    const title = button?.getAttribute('title') ?? '';
    expect(title.length).toBeGreaterThan(0);

    const describedBy = button?.getAttribute('aria-describedby') ?? '';
    expect(describedBy).not.toBe('');
    const target = document.getElementById(describedBy);
    expect(target, 'aria-describedby має вказувати на існуючий елемент').not.toBeNull();
    expect(target?.textContent).toBe(title);
  });

  it('контроль: грант є - кнопка активна, без підказки і без aria-describedby', async () => {
    show({});

    const button = (await screen.findByText('⟦grid.addRow⟧')).closest('button');
    expect(button?.hasAttribute('disabled')).toBe(false);
    expect(button?.getAttribute('title')).toBeNull();
    expect(button?.getAttribute('aria-describedby')).toBeNull();
  });
});