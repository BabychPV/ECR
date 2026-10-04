import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { TableSliceDto } from '@/api/types';
import { cancelAutosave } from '../autosave';
import { resetPending } from '../pendingStore';
import { DocumentGrid } from '../DocumentGrid';

/**
 * AN-39 / L8-11 для сітки: фоновий збій перезапиту зрізу (дані вже є) підміняв усю
 * сітку на ErrorAlert - розмонтовувались редактор, Undo і панель конфлікту. Перезапити
 * зрізів після L8-02 (Recall/Return/Reopen) роблять цей шлях частішим.
 */

vi.mock('@revolist/react-datagrid', () => ({
  RevoGrid: () => <div data-testid="revogrid-stub" />,
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
        header: 'C1',
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
  };
}

const Refusal = {
  type: 'about:blank',
  title: 'Internal Server Error',
  status: 500,
  detail: 'зріз прочитати не вдалося',
  errorCode: 'ECR-SYS-0500',
  correlationId: 'cid-slice-1',
  messageKey: 'err.ECR-SYS-0500.unexpected',
};

let failSlice = false;

afterEach(() => {
  cancelAutosave();
  resetPending();
  failSlice = false;
  vi.unstubAllGlobals();
});

describe('L8-11: фоновий збій перезапиту зрізу', () => {
  it('сітка лишається на місці, збій - банером із повтором', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        failSlice
          ? new Response(JSON.stringify(Refusal), { status: 500, headers: { 'Content-Type': 'application/problem+json' } })
          : new Response(JSON.stringify(sliceFixture()), { status: 200, headers: { 'Content-Type': 'application/json' } }),
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
            allowsDynamicRows={false}
            maxDynamicRows={null}
          />
        </QueryClientProvider>
      </MantineProvider>,
    );

    await screen.findByTestId('revogrid-stub');

    failSlice = true;
    await act(async () => {
      await client.refetchQueries({ queryKey: ['table-slice', 1, 202609] });
    });

    const alert = await waitFor(() => screen.getByRole('alert'));
    expect(alert.textContent ?? '').toContain('ECR-SYS-0500');
    expect(screen.getByTestId('revogrid-stub')).toBeTruthy();
    expect(screen.getByRole('button', { name: /grid\.undo/ })).toBeTruthy();
  });
});
