import type { JSX } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { DocumentTableDto, ValidationFindingDto } from '@/api/types';
import { testTheme } from '@/test/render';
import { DocumentInspector, IssuesPageSize } from '../DocumentInspector';
import { clearInspectedCell, publishInspectedCell, type InspectedCell } from '../inspectedCell';

/**
 * C1-02: вкладка Issues при тисячах зауважень.
 *
 * ⛔ Два механізми, обидва тут тримаються:
 *   1. перелік малюється порціями (`IssuesPageSize` на групу), а не весь одразу;
 *   2. рух курсора в сітці (`publishInspectedCell` → `useInspectedCell` у корені інспектора) НЕ
 *      перемальовує перелік: `IssuesTab` під `memo`, колбек стабільний.
 *
 * ⚠ `StatusBadge` підмінений лічильником рендерів — він є в кожному рядку зауваження, тож число його
 * рендерів і є число перемальованих рядків.
 */
const badgeRenders = { count: 0 };

vi.mock('@/shared/ui/StatusBadge', () => ({
  StatusBadge: (): JSX.Element => {
    badgeRenders.count += 1;
    return <span data-testid="badge" />;
  },
}));

const Tables = [
  {
    allowsDynamicRows: false,
    maxDynamicRows: null,
    sheetCode: 'A',
    sheetDefId: 1,
    sheetNameL10n: { values: { en: 'Sheet A' } },
    sheetOrdinal: 0,
    tableCode: 'T1',
    tableDefId: 1,
    tableInstanceId: 10,
    tableNameL10n: { values: { en: 'Table 1' } },
    tableOrdinal: 0,
  } as DocumentTableDto,
];

const Total = IssuesPageSize * 2 + 50;

const Findings: ValidationFindingDto[] = Array.from({ length: Total }, (_, index) => ({
  severity: 'Error',
  ruleCode: 'REQ',
  message: `Required value is missing ${String(index)}`,
  tableDefId: 1,
  rowKey: `R${String(index)}`,
  columnCode: 'C1',
  blocksSave: true,
}));

function cell(rowKey: string): InspectedCell {
  return {
    tableInstanceId: 10,
    periodKey: 202609,
    rowKey,
    rowLabel: rowKey,
    columnCode: 'C1',
    columnDefId: 33,
    columnHeader: 'Volume',
    unitSymbol: null,
    dataType: 'Decimal',
    scale: 2,
    isCalculated: false,
    isReadOnly: false,
    value: '1',
  } as InspectedCell;
}

function Harness(): JSX.Element {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return (
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <MemoryRouter initialEntries={['/documents/1?panel=issues']}>
          <DocumentInspector
            documentId={1}
            periodKey={202609}
            tables={Tables}
            messages={Findings}
            onSelectFinding={() => undefined}
          />
        </MemoryRouter>
      </QueryClientProvider>
    </MantineProvider>
  );
}

const rows = (): number => document.querySelectorAll('.ecr-insp-issue').length;

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 404 })));
  badgeRenders.count = 0;
});

afterEach(() => {
  cleanup();
  act(() => clearInspectedCell());
  vi.unstubAllGlobals();
});

describe('C1-02 · Issues інспектора під навантаженням', () => {
  it('малює зауваження порціями; «ще» додає наступну порцію, поки не покаже всі', () => {
    render(<Harness />);

    expect(rows()).toBe(IssuesPageSize);

    const more = (): HTMLElement | null => document.querySelector<HTMLElement>('[data-inspector-more]');

    fireEvent.click(more() as HTMLElement);
    expect(rows()).toBe(IssuesPageSize * 2);

    fireEvent.click(more() as HTMLElement);
    expect(rows()).toBe(Total);
    expect(more()).toBeNull();
  });

  it('рух курсора в сітці не перемальовує перелік зауважень', () => {
    render(<Harness />);
    act(() => publishInspectedCell(cell('R1')));

    const before = badgeRenders.count;

    act(() => publishInspectedCell(cell('R2')));
    act(() => publishInspectedCell(cell('R3')));

    // Корінь інспектора перемалювався (чип адреси в шапці), а перелік — ні.
    expect(document.querySelector('[data-inspector-address]')?.textContent).toContain('R3');
    expect(badgeRenders.count).toBe(before);
  });
});
