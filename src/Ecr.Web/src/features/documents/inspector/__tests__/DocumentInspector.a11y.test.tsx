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
});
