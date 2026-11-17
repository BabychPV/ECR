import { afterEach, describe, expect, it } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import type { DocumentTableDto, ValidationFindingDto } from '@/api/types';
import { mantineProviderProps } from '@/shared/theme/provider';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { Themes } from '@/test/__tests__/a11yFixtures';
import { WorkflowHistory } from '@/features/workflow/WorkflowHistory';
import { DocumentInspector } from '../DocumentInspector';
import { clearInspectedCell, publishInspectedCell } from '../inspectedCell';

/**
 * ✎ LAND-YEAR L7a/L7b: інспектор документа «рік» (чипи місяця в Issues, рядок «Period» в Info, журнал аркуша з
 * місяцем) і журнал переходів групами за місяцем — axe без блокуючих порушень в обох темах.
 */
const Anchor = 209901;

const Tables = [
  {
    allowsDynamicRows: false, copyOrdinal: 1, isCopyable: false, maxDynamicRows: null, sheetCode: 'A', sheetDefId: 1,
    sheetNameL10n: { values: { en: 'Sheet A' } }, sheetOrdinal: 0, tableCode: 'T1', tableDefId: 1, tableInstanceId: 10,
    tableNameL10n: { values: { en: 'Land' } }, tableOrdinal: 0,
  } as DocumentTableDto,
];

const Findings: ValidationFindingDto[] = [
  { severity: 'Error', ruleCode: 'REQ', message: 'October is empty', tableDefId: 1, rowKey: 'R1', columnCode: 'M10', blocksSave: false, periodKey: 209910 },
  { severity: 'Warning', ruleCode: 'HRS', message: 'Hours high', tableDefId: 1, rowKey: 'R3', columnCode: 'M09', blocksSave: false, periodKey: 209909 },
];

const blank = {
  rowKey: null, rowLabelL10n: null, columnDefId: null, columnCode: null, columnHeaderL10n: null, columnDataType: null,
  tableCode: null, tableNameL10n: null, oldValue: null, newValue: null, origin: null, action: null, fromState: null,
  toState: null, reason: null,
};

afterEach(() => {
  cleanup();
  act(() => clearInspectedCell());
});

describe('DocumentInspector «рік» — axe', () => {
  it.each(Themes.flatMap((scheme) => (['issues', 'history', 'info'] as const).map((tab) => [scheme, tab] as const)))(
    'тема %s, вкладка %s',
    async (scheme, tab) => {
      const client = new QueryClient({ defaultOptions: { queries: { retry: false, enabled: false } } });
      client.setQueryData(['document', 1, Anchor, 'sheet-activity', 1, false, 'year'], {
        pages: [
          {
            items: [
              { ...blank, kind: 'Workflow', periodKey: 209910, at: '2099-10-29T10:00:00Z', byDisplayName: 'land-owner', action: 'Submit' },
              {
                ...blank, kind: 'CellEdit', periodKey: 209909, at: '2099-10-29T09:00:00Z', byDisplayName: 'M. Petrenko',
                origin: 'UserEdit', rowKey: 'R1', columnCode: 'M09', tableCode: 'T1', oldValue: '1', newValue: '2', columnDefId: 33,
              },
            ],
            nextCursor: null,
          },
        ],
        pageParams: [null],
      });
      act(() =>
        publishInspectedCell({
          tableInstanceId: 10, periodKey: Anchor, ownerPeriodKey: 209910, rowKey: 'R1', rowLabel: 'Gas', columnCode: 'M10',
          columnDefId: 33, columnHeader: 'October', unitSymbol: null, dataType: 'Decimal', scale: 2, isCalculated: false,
          isReadOnly: false, value: '5',
        }),
      );

      const { container } = render(
        <MantineProvider {...mantineProviderProps} forceColorScheme={scheme}>
          <QueryClientProvider client={client}>
            <MemoryRouter initialEntries={[`/documents/1?panel=${tab}`]}>
              <main>
                <DocumentInspector
                  documentId={1}
                  periodKey={Anchor}
                  yearLayout
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

      expect(screen.getByRole('complementary')).toBeTruthy();
      if (tab === 'issues') expect(container.querySelectorAll('[data-inspector-finding-period]')).toHaveLength(2);
      if (tab === 'history') expect(container.querySelectorAll('[data-inspector-log-period]')).toHaveLength(2);
      const violations = await findViolations(container);

      expect(violations, describeViolations(violations)).toHaveLength(0);
    },
  );

  it.each(Themes)('тема %s, журнал переходів року групами за місяцем', async (scheme) => {
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, enabled: false } } });
    const event = (periodKey: number, sheetCode: string) => ({
      action: 'Submit', approvedBySubmitter: false, at: '2099-10-02T10:00:00Z', byDisplayName: 'f13-operator',
      fromState: 'Draft', periodKey, reason: null, sheetCode, sheetCopyId: null, stepOrdinal: null, toState: 'Submitted',
    });
    client.setQueryData(['document', 1, Anchor, 'workflow-history-year', null], [event(209910, 'OCT'), event(209909, 'SEP')]);

    const { container } = render(
      <MantineProvider {...mantineProviderProps} forceColorScheme={scheme}>
        <QueryClientProvider client={client}>
          <main>
            <h1>Document</h1>
            <h2>History</h2>
            <WorkflowHistory embedded year documentId={1} periodKey={Anchor} />
          </main>
        </QueryClientProvider>
      </MantineProvider>,
    );

    expect(container.querySelectorAll('[data-workflow-history-month]')).toHaveLength(2);
    const violations = await findViolations(container);

    expect(violations, describeViolations(violations)).toHaveLength(0);
  });
});
