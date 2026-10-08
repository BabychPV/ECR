import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { DocumentTableDto, ValidationFindingDto } from '@/api/types';
import { testTheme } from '@/test/render';
import { clearHeaderIssue, setHeaderIssue } from '@/features/documents/headerIssue';
import { DocumentInspector } from '../DocumentInspector';
import { clearInspectedCell, publishInspectedCell, type InspectedCell } from '../inspectedCell';

/**
 * Інспектор документа (`UI-25`): закритий за замовчуванням, «K issues»
 * відкриває Issues за таблицями, клік веде в клітинку, `Esc` повертає фокус,
 * History — історія комірки (`BE-03`, `D15-16`), Info — метадані.
 *
 * ⛔ Окремий блок «Scope»: роль бачить ОДИН аркуш (`tables` — лише його
 * таблиці), а сервер віддає зауваження й статуси ще й прихованого аркуша з
 * `null`-лічильниками. Інспектор не має показати ні назви, ні тексту, ні
 * лічильників прихованого аркуша, а `null` — як «—», не 0.
 */

function table(sheetCode: string, sheetOrdinal: number, tableDefId: number): DocumentTableDto {
  return {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode,
    sheetDefId: sheetOrdinal + 1,
    sheetNameL10n: { values: { en: `Sheet ${sheetCode}` } },
    sheetOrdinal,
    tableCode: `T${String(tableDefId)}`,
    tableDefId,
    tableInstanceId: tableDefId * 10,
    tableNameL10n: { values: { en: `Visible table ${String(tableDefId)}` } },
    tableOrdinal: 0,
  } as DocumentTableDto;
}

const VisibleTables = [table('A', 0, 1)];

const Findings: ValidationFindingDto[] = [
  { severity: 'Error', ruleCode: 'CAP', message: 'Fuel is over the cap', tableDefId: 1, rowKey: 'R4', columnCode: 'C3', blocksSave: true },
  { severity: 'Warning', ruleCode: 'HRS', message: 'Hours exceed the period', tableDefId: 1, rowKey: 'R5', columnCode: 'C1', blocksSave: false },
  // ⛔ Прихований аркуш: сервер до фіксу міг віддати.
  { severity: 'Error', ruleCode: 'SECRET', message: 'Secret sheet finding', tableDefId: 99, rowKey: 'R1', columnCode: 'C1', blocksSave: true },
];

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
  const url = String(input);

  if (url.includes('/tables/status')) {
    return jsonResponse([
      { tableDefId: 1, sheetCode: 'A', inputCells: 4, filledCells: 2, errorCount: null, warningCount: null },
      { tableDefId: 99, sheetCode: 'HIDDEN', inputCells: 9, filledCells: 9, errorCount: 7, warningCount: 3 },
    ]);
  }

  if (url.includes('/api/v1/audit/cells')) {
    return jsonResponse({
      items: [
        {
          changedAt: '2026-09-17T08:12:00Z',
          changedByUserId: 5,
          changedByDisplayName: 'M. Petrenko',
          columnDefId: 33,
          documentId: 1,
          isLateEdit: false,
          isOutOfWindow: false,
          newValue: '12.5',
          oldValue: '10',
          origin: 'UserEdit',
          periodKey: 202609,
          rowKey: 'R4',
        },
        {
          changedAt: '2025-09-17T08:12:00Z',
          changedByUserId: 6,
          changedByDisplayName: 'Other period',
          columnDefId: 33,
          documentId: 1,
          isLateEdit: false,
          isOutOfWindow: false,
          newValue: '1',
          oldValue: null,
          origin: 'Import',
          periodKey: 202509,
          rowKey: 'R4',
        },
      ],
      nextCursor: null,
    });
  }

  return new Response(null, { status: 404 });
});

let location = '';
function LocationProbe(): null {
  location = useLocation().search;
  return null;
}

function Harness({
  entry = '/documents/1',
  messages = Findings,
  onSelect = () => {},
  seq,
}: {
  entry?: string;
  messages?: ValidationFindingDto[] | null;
  onSelect?: (finding: ValidationFindingDto) => void;
  seq?: number;
}): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <LocationProbe />
          <button type="button">Validate</button>
          <DocumentInspector
            documentId={1}
            periodKey={202609}
            tables={VisibleTables}
            messages={messages}
            onSelectFinding={onSelect}
            validatedSeq={seq}
          />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

const Cell: InspectedCell = {
  tableInstanceId: 10,
  periodKey: 202609,
  rowKey: 'R4',
  rowLabel: 'Diesel',
  columnCode: 'C3',
  columnDefId: 33,
  columnHeader: 'Volume',
  unitSymbol: 'm³',
  dataType: 'Decimal',
  scale: 4,
  isCalculated: false,
  isReadOnly: false,
  value: '12.5',
};

afterEach(() => {
  cleanup();
  act(() => clearInspectedCell());
  fetchMock.mockClear();
  vi.unstubAllGlobals();
});

describe('інспектор документа (UI-25)', () => {
  it('за замовчуванням закритий: complementary немає (L2)', () => {
    render(<Harness />);

    expect(screen.queryByRole('complementary')).toBeNull();
    expect(location).toBe('');
  });

  it('«K issues» відкриває Issues, згруповані за таблицями, фокус на вкладці', async () => {
    render(<Harness />);

    const trigger = screen.getByRole('button', { name: /document\.issuesCount\.other \(count=2\)/ });
    trigger.focus();
    fireEvent.click(trigger);

    const aside = await screen.findByRole('complementary');
    expect(location).toBe('?panel=issues');
    await waitFor(() => expect(document.activeElement).toBe(within(aside).getByRole('tab', { selected: true })));

    const group = within(aside).getByRole('region', { name: 'T1 Visible table 1' });
    expect(within(group).getAllByRole('button')).toHaveLength(2);
    // Помилка — першою.
    expect(within(group).getAllByRole('button')[0]?.textContent).toContain('Fuel is over the cap');
    expect(within(group).getAllByRole('button')[0]?.textContent).toContain('R4 · C3');
  });

  it('клік по зауваженню передає адресу сторінці (перехід у клітинку)', async () => {
    const onSelect = vi.fn();
    render(<Harness entry="/documents/1?panel=issues" onSelect={onSelect} />);

    fireEvent.click(await screen.findByRole('button', { name: /Hours exceed the period/ }));

    expect(onSelect).toHaveBeenCalledWith(expect.objectContaining({ rowKey: 'R5', columnCode: 'C1', tableDefId: 1 }));
  });

  it('Esc закриває інспектор і повертає фокус тому, хто відкривав', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      render(<Harness />);
      const trigger = screen.getByRole('button', { name: /document\.issuesCount/ });
      trigger.focus();
      fireEvent.click(trigger);

      const aside = await screen.findByRole('complementary');
      fireEvent.keyDown(within(aside).getByRole('tab', { selected: true }), { key: 'Escape' });

      await waitFor(() => expect(screen.queryByRole('complementary')).toBeNull());
      act(() => {
        vi.runOnlyPendingTimers();
      });
      expect(document.activeElement).toBe(trigger);
      expect(location).toBe('');
    } finally {
      vi.useRealTimers();
    }
  });

  it('свіжа перевірка з зауваженнями відкриває Issues сама', async () => {
    const { rerender } = render(<Harness seq={0} />);
    expect(screen.queryByRole('complementary')).toBeNull();

    rerender(<Harness seq={1} />);

    expect(await screen.findByRole('complementary')).toBeTruthy();
    expect(location).toBe('?panel=issues');
  });

  it('не перевіряли — кнопка без числа і вкладка каже «не перевіряли», а не «немає зауважень»', async () => {
    render(<Harness entry="/documents/1?panel=issues" messages={null} />);

    expect(await screen.findByText('⟦inspector.notValidatedTitle⟧')).toBeTruthy();
    expect(screen.queryByText('⟦inspector.noIssuesTitle⟧')).toBeNull();
  });

  it('History: без вибраної комірки — пояснення, з коміркою — її зміни за цей період (D15-16)', async () => {
    vi.stubGlobal('fetch', fetchMock);
    render(<Harness entry="/documents/1?panel=history" />);

    expect(await screen.findByText('⟦inspector.noCellTitle⟧')).toBeTruthy();
    expect(fetchMock).not.toHaveBeenCalled();

    act(() => publishInspectedCell(Cell));

    expect(await screen.findByText('M. Petrenko')).toBeTruthy();
    expect(screen.queryByText('Other period')).toBeNull();
    // Чип адреси в шапці.
    expect(screen.getByRole('complementary').textContent).toContain('R4 · C3');

    const url = String(fetchMock.mock.calls.find(([input]) => String(input).includes('/audit/cells'))?.[0]);
    expect(url).toContain('documentId=1');
    expect(url).toContain('rowKey=R4');
    expect(url).toContain('columnDefId=33');
  });

  it('комірка іншого документа/періоду не показується', async () => {
    render(<Harness entry="/documents/1?panel=info" />);
    act(() => publishInspectedCell({ ...Cell, periodKey: 202608 }));

    expect(await screen.findByText('⟦inspector.noCellTitle⟧')).toBeTruthy();
  });
});

describe('Scope: роль бачить один аркуш', () => {
  it('⛔ прихований аркуш: ні тексту, ні назви, ні лічильника; null → «—»', async () => {
    vi.stubGlobal('fetch', fetchMock);
    render(<Harness entry="/documents/1?panel=issues" />);

    const aside = await screen.findByRole('complementary');
    expect(aside.textContent).not.toContain('Secret sheet finding');
    expect(aside.textContent).not.toContain('SECRET');
    expect(aside.textContent).not.toContain('HIDDEN');
    // Лічильник рахує лише видиме: 2, не 3.
    expect(screen.getByRole('button', { name: /document\.issuesCount\.other \(count=2\)/ })).toBeTruthy();
    expect(within(aside).getByRole('tab', { name: /inspector\.tabIssues/ }).textContent).toContain('2');

    act(() => publishInspectedCell(Cell));
    fireEvent.click(within(aside).getByRole('tab', { name: /inspector\.tabInfo/ }));

    // ⛔ errorCount/warningCount = null → «—», а не 0; лічильники прихованої таблиці (7/3) не протікають.
    expect(await screen.findByText('⟦inspector.info.tableIssuesValue (errors=—, warnings=—)⟧')).toBeTruthy();
    expect(screen.getByRole('complementary').textContent).not.toMatch(/errors=7|warnings=3/);
  });
});

describe('DocumentInspector: RC14-A — відмова поля шапки', () => {
  afterEach(() => clearHeaderIssue(1));

  it('відкриває Issues, показує групу «Шапка» і веде фокус у поле шапки', async () => {
    const field = document.createElement('input');
    field.setAttribute('data-header-field', 'QTY');
    document.body.appendChild(field);

    render(<Harness messages={null} />);
    expect(screen.queryByRole('complementary')).toBeNull();

    act(() => {
      setHeaderIssue({
        documentId: 1,
        fieldCode: 'QTY',
        title: 'Header value rejected',
        detail: 'Entry is not valid in the project window.',
        errorCode: 'ECR-HDR-4223',
      });
    });

    const aside = await screen.findByRole('complementary');
    const item = within(aside).getByText('Entry is not valid in the project window.');
    // ⛔ Мутаційний доказ: прибрати `focusHeaderField` з `onSelectHeader` — фокус не перейде.
    fireEvent.click(item);
    expect(document.activeElement).toBe(field);
    field.remove();
  });

  it('зауваження чужого документа не показується', () => {
    render(<Harness messages={null} />);
    act(() => {
      setHeaderIssue({ documentId: 2, fieldCode: 'QTY', title: 't', detail: null, errorCode: 'ECR-HDR-0422' });
    });
    expect(screen.queryByRole('complementary')).toBeNull();
    act(() => setHeaderIssue(null));
  });
});
