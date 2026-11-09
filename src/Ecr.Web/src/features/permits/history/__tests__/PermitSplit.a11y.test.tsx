import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { queryKeys } from '@/api/queryKeys';
import { Catalog, Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import { permitHistoryKey } from '../api';
import { CellSplitDialog } from '../CellSplitDialog';
import { HeaderSplitBanner } from '../HeaderSplitBanner';
import { RowPermitSection } from '../RowPermitSection';
import { permitGridMarksOf } from '../splitModel';
import { historyOf, seaWaterRow } from './historyFixture';

/**
 * Feature (j) accessibility in both themes, with the real seeded texts (a key without a seed row would show `⟦key⟧`):
 * the split dialog (labelled part inputs, live total) and the Inspector section with the open «Add permit» form.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

vi.mock('@/features/permits/history/api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/features/permits/history/api')>()),
  getPermitHistory: () => new Promise(() => undefined),
}));

afterEach(cleanup);

function client(): QueryClient {
  const queries = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
  queries.setQueryData(queryKeys.slices.one(41, 202602), {
    columns: [{ code: 'Permit', dataType: 'Lookup', lookupRegistryDefId: 90, header: 'Permit', unitSymbol: null }],
    rows: [{ rowKey: 'R1', label: 'Sea Water Intake', rowVersion: 'v', cells: {} }],
  });
  queries.setQueryData(queryKeys.registries.lookupTargets(), [{ id: 90, code: 'PRM_WATER', kind: 'PermitType', nameL10n: {} }]);
  queries.setQueryData([...queryKeys.registries.entries('PRM_WATER'), 'document', 3], [
    { id: 11, code: 'KZ29VCZ14622425', display: 'KZ29VCZ14622425', validFrom: '2021-02-15', validTo: '2026-02-14' },
    { id: 12, code: 'KZ46VCZ14622410', display: 'KZ46VCZ14622410', validFrom: '2026-02-15', validTo: '2031-02-14' },
  ]);
  queries.setQueryData(permitHistoryKey(3, 202602), historyOf(seaWaterRow()));

  return queries;
}

describe('feature (j): accessibility', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: split dialog — labelled parts, live total, no violations, no ⟦keys⟧`, async () => {
      const mark = permitGridMarksOf(historyOf(seaWaterRow()), 701, null).cells.get('R1:Feb') ?? null;
      render(
        <Shell colorScheme={colorScheme}>
          <QueryClientProvider client={client()}>
            <CellSplitDialog
              target={{ documentId: 3, periodKey: 202602, tableInstanceId: 41, tableDefId: 701, sheetCopyId: null, rowKey: 'R1', columnCode: 'Feb' }}
              mark={mark}
              historyPending={false}
              rowLabel="Sea Water Intake"
              unit="thousand m³"
              baseVersion="v"
              onClose={vi.fn()}
            />
          </QueryClientProvider>
        </Shell>,
      );

      const dialog = await screen.findByRole('dialog');
      expect(dialog.textContent).not.toContain('⟦');
      expect(screen.getByRole('textbox', { name: /Part A · KZ29VCZ14622425/ })).toBeTruthy();
      expect(screen.getByRole('status').getAttribute('aria-live')).toBe('polite');
      const violations = await findViolations(dialog);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });

    it(`${colorScheme}: Inspector permit history + Add permit form — no violations, no ⟦keys⟧`, async () => {
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <QueryClientProvider client={client()}>
            <RowPermitSection
              documentId={3}
              periodKey={202602}
              tableDefId={701}
              sheetCopyId={null}
              cell={{
                tableInstanceId: 41,
                periodKey: 202602,
                rowKey: 'R1',
                rowLabel: 'Sea Water Intake',
                columnCode: 'Permit',
                isCalculated: false,
                isReadOnly: false,
                value: '11',
              }}
            />
          </QueryClientProvider>
        </Shell>,
      );

      fireEvent.click(screen.getByRole('button', { name: 'Add permit from date…' }));
      await screen.findByRole('textbox', { name: 'Permit' });
      expect(container.textContent).not.toContain('⟦');
      const violations = await findViolations(container);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });

    it(`${colorScheme}: jh2 header banner + jh3 «Split all» dialog — no violations, no ⟦keys⟧`, async () => {
      const queries = client();
      const month = seaWaterRow().splitMonths[0]!;
      queries.setQueryData(permitHistoryKey(3, 202602), {
        header: [{ fieldCode: 'PermitNo', permits: seaWaterRow().permits }],
        rows: [seaWaterRow({ tableDefId: 801, tableCode: 'AIR', rowKey: 'NO2', columnCode: 'PermitNo', permitSource: 'header', splitMonths: [month] })],
        cellsNeedingSplit: 1,
      });
      queries.setQueryData(queryKeys.slices.one(51, 202602), {
        columns: [{ code: 'Feb', dataType: 'Decimal', unitSymbol: 't' }],
        rows: [{ rowKey: 'NO2', label: 'Nitrogen dioxide', rowVersion: 'v', cells: {} }],
      });
      const tables = [{ tableDefId: 801, sheetCopyId: null, tableInstanceId: 51, tableCode: 'AIR', tableNameL10n: { values: { en: 'Air emissions' } } }];
      const { container } = render(
        <Shell colorScheme={colorScheme}>
          <QueryClientProvider client={queries}>
            <HeaderSplitBanner documentId={3} periodKey={202602} tables={tables as never} readOnly={false} />
          </QueryClientProvider>
        </Shell>,
      );

      expect(container.textContent).not.toContain('⟦');
      const bannerViolations = await findViolations(container);
      expect(bannerViolations, describeViolations(bannerViolations)).toHaveLength(0);

      fireEvent.click(screen.getByRole('button', { name: 'Split all…' }));
      const dialog = await screen.findByRole('dialog');
      expect(dialog.textContent).not.toContain('⟦');
      const violations = await findViolations(dialog);
      expect(violations, describeViolations(violations)).toHaveLength(0);
    });
  }
});
