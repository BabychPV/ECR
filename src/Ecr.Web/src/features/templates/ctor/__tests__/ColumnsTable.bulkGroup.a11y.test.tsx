import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { Shell, Themes } from '@/test/__tests__/a11yFixtures';
import { describe as describeViolations, findViolations } from '@/test/a11y';
import type { TemplateTable } from '../ctorModel';
import { ColumnsTable } from '../ColumnsTable';

/**
 * Вкладка Columns (PERMITES §3) в обох темах: прапорці вибору з іменами, панель «N selected» (`role=toolbar`,
 * лічильник `role=status`), колонка «Group» і відкритий діалог «Edit selected…» — без блокуючих порушень axe.
 */
function column(id: number, code: string, headerGroupId: number | null = null) {
  return {
    id,
    code,
    dataType: 'Decimal',
    headerL10n: { values: { en: code } },
    isHidden: false,
    isReadOnly: false,
    unitSymbol: null,
    formulaExpression: null,
    formulaDialect: null,
    displayFormat: null,
    headerGroupId,
  };
}

const table = {
  id: 41,
  code: 'T',
  nameL10n: { values: { en: 'T' } },
  ordinal: 1,
  rowMode: 'Fixed',
  maxDynamicRows: null,
  rows: [],
  headerGroups: [{ id: 7, parentId: null, code: 'JAN', ordinal: 1, labelL10n: { values: { en: 'January' } } }],
  columns: [column(1, 'C1'), column(2, 'C2'), column(3, 'C3', 7)],
} as unknown as TemplateTable;

describe('ColumnsTable: вибір і «Edit selected…» — доступність', { timeout: 120_000 }, () => {
  for (const colorScheme of Themes) {
    it(`${colorScheme}: панель вибору й діалог групи без порушень`, async () => {
      render(
        <Shell colorScheme={colorScheme}>
          <ColumnsTable
            table={table}
            templateVersionId={9}
            presentationRevision={2}
            canEditPresentation
            canEditStructure={false}
            reorderPending={false}
            deletingCode={null}
            onReorder={vi.fn()}
            onUsage={vi.fn()}
            onPresentation={vi.fn()}
            onEdit={vi.fn()}
            onDelete={vi.fn()}
            onFormula={vi.fn()}
          />
        </Shell>,
      );

      for (const code of ['C1', 'C2']) {
        const row = screen.getByText(`(${code})`).closest('tr') as HTMLElement;
        fireEvent.click(within(row).getByRole('checkbox'));
      }

      const page = await findViolations(document.body);
      expect(page, describeViolations(page)).toHaveLength(0);

      fireEvent.click(screen.getByRole('button', { name: /columns\.editSelected|Edit selected/ }));
      await screen.findByRole('dialog');

      const dialog = await findViolations(document.body);
      expect(dialog, describeViolations(dialog)).toHaveLength(0);
    });
  }
});
