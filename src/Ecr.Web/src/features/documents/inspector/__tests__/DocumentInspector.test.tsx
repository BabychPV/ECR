import type { JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter, useLocation } from 'react-router-dom';
import type { DocumentTableDto, ValidationFindingDto } from '@/api/types';
import { testTheme } from '@/test/render';
import { DocumentInspector, type SheetLogTarget } from '../DocumentInspector';
import { clearInspectedCell, inspectedCell, publishInspectedCell, type InspectedCell } from '../inspectedCell';

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

  if (url.includes('/sheets/101/activity')) {
    const base = {
      periodKey: 202609, rowKey: null, rowLabelL10n: null, columnDefId: null, columnCode: null, columnHeaderL10n: null,
      columnDataType: null, tableCode: null, tableNameL10n: null, oldValue: null, newValue: null, origin: null,
      action: null, fromState: null, toState: null, reason: null,
    };
    if (url.includes('cursor=NEXT')) {
      return jsonResponse({
        items: [{ ...base, kind: 'Workflow', at: '2026-10-29T08:00:00Z', byDisplayName: 'author', action: 'Submit', fromState: 'Draft', toState: 'Submitted' }],
        nextCursor: null,
      });
    }

    return jsonResponse({
      items: [
        {
          ...base, kind: 'Workflow', at: '2026-10-29T10:00:00Z', byDisplayName: 'land-owner', action: 'Reject',
          fromState: 'Submitted', toState: 'Rejected', reason: 'Correct <b>the</b> figures',
        },
        {
          ...base, kind: 'CellEdit', at: '2026-10-29T09:00:00Z', byDisplayName: url.includes('includeSystem=true') ? 'system' : 'M. Petrenko',
          origin: url.includes('includeSystem=true') ? 'Recalculation' : 'UserEdit', rowKey: 'R4', columnCode: 'C3', tableCode: 'T1',
          rowLabelL10n: { values: { en: 'Diesel' } }, columnHeaderL10n: { values: { en: 'Volume' } }, tableNameL10n: { values: { en: 'Fuel' } },
          oldValue: '10', newValue: '12.5', columnDefId: 33,
        },
      ],
      nextCursor: 'NEXT',
    });
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
  documentId = 1,
  onOpenCell,
}: {
  documentId?: number;
  entry?: string;
  messages?: ValidationFindingDto[] | null;
  onSelect?: (finding: ValidationFindingDto) => void;
  seq?: number;
  onOpenCell?: (target: SheetLogTarget) => void;
}): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={[entry]}>
          <LocationProbe />
          <button type="button">Validate</button>
          <DocumentInspector
            documentId={documentId}
            periodKey={202609}
            tables={VisibleTables}
            messages={messages}
            onSelectFinding={onSelect}
            validatedSeq={seq}
            sheet={{ sheetDefId: 101, code: 'A', name: 'Sheet A' }}
            onOpenCell={onOpenCell}
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

  it('CL-15: нейтральна кнопка Issues (не перевіряли / немає зауважень) читає токен dimmed, а не gray.6', () => {
    const { unmount } = render(<Harness messages={null} />);
    const idle = document.querySelector<HTMLElement>('[data-inspector-issues="none"]');
    expect(idle?.style.color).toBe('var(--mantine-color-dimmed)');
    unmount();

    render(<Harness messages={[]} />);
    const clean = document.querySelector<HTMLElement>('[data-inspector-issues="0"]');
    expect(clean?.style.color).toBe('var(--mantine-color-dimmed)');
  });

  it('CL-15: кнопка із зауваженнями лишає колір статусу (не dimmed)', () => {
    render(<Harness />);
    const button = document.querySelector<HTMLElement>('[data-inspector-issues="2"]');
    expect(button).not.toBeNull();
    expect(button?.style.color).toBe('');
  });

  it('N3-09: автовідкриття після перевірки не забирає фокус у того, хто працює (редактор комірки)', async () => {
    const { rerender } = render(<Harness seq={0} />);
    const working = screen.getByRole('button', { name: 'Validate' });
    working.focus();

    rerender(<Harness seq={1} />);
    await screen.findByRole('complementary');

    expect(document.activeElement).toBe(working);
  });

  it('не перевіряли — кнопка без числа і вкладка каже «не перевіряли», а не «немає зауважень»', async () => {
    render(<Harness entry="/documents/1?panel=issues" messages={null} />);

    expect(await screen.findByText('⟦inspector.notValidatedTitle⟧')).toBeTruthy();
    expect(screen.queryByText('⟦inspector.noIssuesTitle⟧')).toBeNull();
  });

  it('History без комірки: журнал аркуша (правки й дії процесу), заголовок називає аркуш і період, «This cell» вимкнений', async () => {
    vi.stubGlobal('fetch', fetchMock);
    render(<Harness entry="/documents/1?panel=history" />);

    // Лінія: найновіші перші; причина - текстом, а не розміткою; правка - було → стало.
    const items = await screen.findAllByRole('listitem');
    expect(items).toHaveLength(2);
    expect(items[0]?.textContent).toContain('⟦workflow.eventAction.Reject⟧');
    expect(items[0]?.textContent).toContain('Correct <b>the</b> figures');
    expect(items[0]?.querySelector('b')).toBeNull();
    expect(items[0]?.textContent).toContain('land-owner');
    expect(items[1]?.textContent).toContain('M. Petrenko');
    expect(items[1]?.textContent).toContain('Fuel · Diesel · Volume');
    expect(items[1]?.textContent).toContain('10');
    expect(items[1]?.textContent).toContain('12.5');

    // Режим і область названі явно.
    const sheetRadio = screen.getByRole('radio', { name: '⟦inspector.history.modeSheet⟧' }) as HTMLInputElement;
    const cellRadio = screen.getByRole('radio', { name: '⟦inspector.history.modeCell⟧' }) as HTMLInputElement;
    expect(sheetRadio.checked).toBe(true);
    expect(cellRadio.disabled).toBe(true);
    expect(screen.getByRole('radiogroup', { name: '⟦inspector.history.modeLabel⟧' })).toBeTruthy();
    expect(document.querySelector('[data-inspector-scope]')?.textContent).toContain('inspector.history.scopeSheet');
    expect(document.querySelector('[data-inspector-scope]')?.textContent).toContain('sheet=Sheet A');
    expect(screen.queryByText('⟦inspector.noCellTitle⟧')).toBeNull();

    const url = new URL(String(fetchMock.mock.calls.find(([input]) => String(input).includes('/activity'))?.[0]), 'http://localhost');
    expect(url.searchParams.get('periodKey')).toBe('202609');
    expect(url.searchParams.get('includeSystem')).toBe('false');
    expect(fetchMock.mock.calls.some(([input]) => String(input).includes('/audit/cells'))).toBe(false);
  });

  it('вибір комірки НЕ підміняє журнал: «This cell» вмикається, перемикання показує історію комірки, зняття вибору повертає журнал', async () => {
    vi.stubGlobal('fetch', fetchMock);
    render(<Harness entry="/documents/1?panel=history" />);
    await screen.findAllByRole('listitem');

    act(() => publishInspectedCell(Cell));

    // Вміст лишився журналом аркуша; перемикач лише став доступним.
    const cellRadio = screen.getByRole('radio', { name: '⟦inspector.history.modeCell⟧' }) as HTMLInputElement;
    await waitFor(() => expect(cellRadio.disabled).toBe(false));
    expect((screen.getByRole('radio', { name: '⟦inspector.history.modeSheet⟧' }) as HTMLInputElement).checked).toBe(true);
    expect(screen.getAllByRole('listitem')).toHaveLength(2);
    expect(fetchMock.mock.calls.some(([input]) => String(input).includes('/audit/cells'))).toBe(false);

    fireEvent.click(cellRadio);

    expect(await screen.findByText('M. Petrenko')).toBeTruthy();
    expect(screen.queryByText('Other period')).toBeNull();
    expect(document.querySelector('[data-inspector-scope]')?.textContent).toContain('inspector.history.scopeCell');
    expect(document.querySelector('[data-inspector-scope]')?.textContent).toContain('cell=R4 · C3');
    expect(document.querySelector('[data-inspector-scope]')?.textContent).toContain('column=Volume');
    expect(screen.getByRole('complementary').textContent).toContain('R4 · C3');

    const url = String(fetchMock.mock.calls.find(([input]) => String(input).includes('/audit/cells'))?.[0]);
    expect(url).toContain('documentId=1');
    expect(url).toContain('rowKey=R4');
    expect(url).toContain('columnDefId=33');
    // AN-98: період іде в запит (відсів на сервері до сторінки), а не лишається на клієнті.
    expect(new URL(url, 'http://localhost').searchParams.get('periodKey')).toBe('202609');

    // Зняли вибір у режимі комірки: журнал аркуша, перемикач узгоджений і «This cell» знову вимкнений.
    act(() => clearInspectedCell());
    await waitFor(() => expect((screen.getByRole('radio', { name: '⟦inspector.history.modeSheet⟧' }) as HTMLInputElement).checked).toBe(true));
    expect((screen.getByRole('radio', { name: '⟦inspector.history.modeCell⟧' }) as HTMLInputElement).disabled).toBe(true);

    // ...і наступний вибір комірки режим сам НЕ вмикає.
    act(() => publishInspectedCell(Cell));
    expect((screen.getByRole('radio', { name: '⟦inspector.history.modeSheet⟧' }) as HTMLInputElement).checked).toBe(true);
  });

  it('журнал аркуша: системні зміни за прапорцем, «ще» дочитує за курсором, клік по правці веде в комірку', async () => {
    vi.stubGlobal('fetch', fetchMock);
    const open = vi.fn();
    render(<Harness entry="/documents/1?panel=history" onOpenCell={open} />);
    await screen.findAllByRole('listitem');

    fireEvent.click(screen.getByRole('button', { name: /Fuel · Diesel · Volume/ }));
    expect(open).toHaveBeenCalledWith({ sheetCode: 'A', tableCode: 'T1', rowKey: 'R4', columnCode: 'C3' });

    fireEvent.click(document.querySelector('[data-inspector-load-more]') as HTMLElement);
    await waitFor(() => expect(screen.getAllByRole('listitem')).toHaveLength(3));
    expect(document.querySelector('[data-inspector-load-more]')).toBeNull();
    expect(
      fetchMock.mock.calls.some(([input]) => String(input).includes('cursor=NEXT')),
    ).toBe(true);

    fireEvent.click(document.querySelector('[data-inspector-include-system] input') ?? screen.getByRole('checkbox'));
    await waitFor(() => expect(
      fetchMock.mock.calls.some(([input]) => String(input).includes('includeSystem=true')),
    ).toBe(true));
    expect(await screen.findByText('⟦audit.systemAuthor⟧')).toBeTruthy();
  });

  it('N3-02: зміна документа скидає комірку під курсором', () => {
    vi.stubGlobal('fetch', fetchMock);
    const { rerender } = render(<Harness documentId={1} />);
    act(() => publishInspectedCell(Cell));
    expect(inspectedCell()).not.toBeNull();

    rerender(<Harness documentId={2} />);

    expect(inspectedCell()).toBeNull();
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
