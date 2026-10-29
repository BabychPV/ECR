import { afterEach, describe, expect, it } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { DocumentTableDto, ValidationFindingDto } from '@/api/types';
import { mantineProviderProps } from '@/shared/theme/provider';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Themes } from '@/test/__tests__/a11yFixtures';
import { DocumentInspector } from '../DocumentInspector';
import { clearInspectedCell, publishInspectedCell } from '../inspectedCell';

/** Інспектор документа (`UI-25`): axe без блокуючих порушень у обох темах і на кожній вкладці. */

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
    tableNameL10n: { values: { en: 'Fuel' } },
    tableOrdinal: 0,
  } as DocumentTableDto,
];

const Findings: ValidationFindingDto[] = [
  { severity: 'Error', ruleCode: 'CAP', message: 'Fuel is over the cap', tableDefId: 1, rowKey: 'R4', columnCode: 'C3', blocksSave: true },
  { severity: 'Warning', ruleCode: 'HRS', message: 'Hours exceed the period', tableDefId: 1, rowKey: 'R5', columnCode: null, blocksSave: false },
];

afterEach(() => {
  cleanup();
  act(() => clearInspectedCell());
});

describe('DocumentInspector — axe', () => {
  it.each(Themes.flatMap((scheme) => (['issues', 'history', 'info'] as const).map((tab) => [scheme, tab] as const)))(
    'тема %s, вкладка %s',
    async (scheme, tab) => {
      const client = new QueryClient({ defaultOptions: { queries: { retry: false, enabled: false } } });
      act(() =>
        publishInspectedCell({
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
        }),
      );

      const { container } = render(
        <MantineProvider {...mantineProviderProps} forceColorScheme={scheme}>
          <QueryClientProvider client={client}>
            <MemoryRouter initialEntries={[`/documents/1?panel=${tab}`]}>
              <main>
                <DocumentInspector
                  documentId={1}
                  periodKey={202609}
                  tables={Tables}
                  messages={Findings}
                  onSelectFinding={() => {}}
                  sheet={{ sheetDefId: 1, code: 'A', name: 'Sheet A' }}
                />
              </main>
            </MemoryRouter>
          </QueryClientProvider>
        </MantineProvider>,
      );

      expect(screen.getByRole('complementary')).toBeTruthy();
      const violations = await findViolations(container);

      expect(violations, describeViolations(violations)).toHaveLength(0);
    },
  );

  // History з даними журналу аркуша: перемикач режимів (radiogroup), заголовок області, рядки правок/дій, прапорець, «ще».
  it.each(Themes.flatMap((scheme) => [true, false].map((withCell) => [scheme, withCell] as const)))(
    'тема %s, журнал аркуша з рядками, комірка вибрана: %s',
    async (scheme, withCell) => {
      const client = new QueryClient({ defaultOptions: { queries: { retry: false, enabled: false } } });
      const blank = {
        periodKey: 202609, rowKey: null, rowLabelL10n: null, columnDefId: null, columnCode: null, columnHeaderL10n: null,
        columnDataType: null, tableCode: null, tableNameL10n: null, oldValue: null, newValue: null, origin: null,
        action: null, fromState: null, toState: null, reason: null,
      };
      client.setQueryData(['document', 1, 202609, 'sheet-activity', 1, false], {
        pages: [
          {
            items: [
              { ...blank, kind: 'Workflow', at: '2026-10-29T10:00:00Z', byDisplayName: 'land-owner', action: 'Reject', reason: 'Correct the figures' },
              {
                ...blank, kind: 'CellEdit', at: '2026-10-29T09:00:00Z', byDisplayName: 'M. Petrenko', origin: 'UserEdit', rowKey: 'R4',
                columnCode: 'C3', tableCode: 'T1', oldValue: '10', newValue: '12.5', columnDefId: 33,
              },
            ],
            nextCursor: 'NEXT',
          },
        ],
        pageParams: [null],
      });
      if (withCell) {
        act(() =>
          publishInspectedCell({
            tableInstanceId: 10, periodKey: 202609, rowKey: 'R4', rowLabel: 'Diesel', columnCode: 'C3', columnDefId: 33,
            columnHeader: 'Volume', unitSymbol: 'm³', dataType: 'Decimal', scale: 4, isCalculated: false, isReadOnly: false, value: '12.5',
          }),
        );
      }

      const { container } = render(
        <MantineProvider {...mantineProviderProps} forceColorScheme={scheme}>
          <QueryClientProvider client={client}>
            <MemoryRouter initialEntries={['/documents/1?panel=history']}>
              <main>
                <DocumentInspector
                  documentId={1}
                  periodKey={202609}
                  tables={Tables}
                  messages={Findings}
                  onSelectFinding={() => {}}
                  sheet={{ sheetDefId: 1, code: 'A', name: 'Sheet A' }}
                  onOpenCell={() => {}}
                />
              </main>
            </MemoryRouter>
          </QueryClientProvider>
        </MantineProvider>,
      );

      expect(screen.getByRole('radiogroup')).toBeTruthy();
      expect(screen.getAllByRole('listitem')).toHaveLength(2);
      const violations = await findViolations(container);

      expect(violations, describeViolations(violations)).toHaveLength(0);
    },
  );
});
