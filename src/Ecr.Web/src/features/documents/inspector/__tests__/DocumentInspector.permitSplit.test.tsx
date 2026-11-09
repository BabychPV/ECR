import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { DocumentTableDto, ValidationFindingDto } from '@/api/types';
import { cellSplitRequest, closeCellSplit } from '@/features/permits/history/splitRequest';
import { testTheme } from '@/test/render';
import { DocumentInspector } from '../DocumentInspector';

/**
 * Feature (j) j3: the `PERMIT-SPLIT` finding (Error, blocks Submit) carries «Split <month>» next to it (a sibling of
 * the issue button, not nested in it), which asks to split exactly that row's month cell of that table instance.
 * Other findings get no such action.
 */
vi.stubGlobal('fetch', vi.fn(async () => new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } })));

const Tables = [
  { tableDefId: 701, tableInstanceId: 597, sheetCopyId: null, tableCode: 'T7_00', sheetCode: 'WR', tableNameL10n: { values: { en: 'Water Report' } } },
] as unknown as DocumentTableDto[];

const Split: ValidationFindingDto = {
  tableDefId: 701,
  rowKey: 'R7001000',
  columnCode: 'February',
  ruleCode: 'PERMIT-SPLIT',
  severity: 'Error',
  blocksSave: false,
  message: 'February (Water): the month is shared by permits …',
};

afterEach(() => {
  cleanup();
  act(() => closeCellSplit());
});

function show(messages: ValidationFindingDto[]): void {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/documents/2?panel=issues']}>
          <DocumentInspector documentId={2} periodKey={202602} tables={Tables} messages={messages} onSelectFinding={() => undefined} />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('Issues: PERMIT-SPLIT → Split <month> (j3)', () => {
  it('opens the split of that month cell', () => {
    show([Split]);

    const action = document.querySelector<HTMLButtonElement>('[data-inspector-split="February"]');
    expect(action).not.toBeNull();
    expect(action?.closest('button[data-severity]')).toBeNull();
    fireEvent.click(action!);

    expect(cellSplitRequest()).toEqual({
      documentId: 2,
      periodKey: 202602,
      tableInstanceId: 597,
      tableDefId: 701,
      sheetCopyId: null,
      rowKey: 'R7001000',
      columnCode: 'February',
    });
  });

  it('other findings have no split action', () => {
    show([{ ...Split, ruleCode: 'PERMIT-VALIDITY' }]);

    expect(screen.queryByText(/permits\.split\.action/)).toBeNull();
    expect(document.querySelector('[data-inspector-split]')).toBeNull();
  });
});
