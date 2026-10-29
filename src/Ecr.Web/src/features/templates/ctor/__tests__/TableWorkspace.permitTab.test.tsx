import { describe, expect, it, vi } from 'vitest';
import { fireEvent, screen } from '@testing-library/react';
import { renderWithQuery } from '@/test/render';
import type { TemplateTable } from '../ctorModel';
import { parseTab } from '../ctorModel';
import { TableWorkspace } from '../TableWorkspace';

/**
 * Вкладка «Permit binding» у робочій області таблиці (PM-11): адреса `?tab=permit`, право передається панелі.
 *
 * Мутаційний доказ (виконано, відновлено): у `TableWorkspace` `canEdit={canEditStructure}` → `canEdit` завжди
 * `true` валить «право на правку»; прибрати `'permit'` з `CtorTabs` валить «адреса».
 */
vi.mock('@/features/permits/bindings/PermitBindingPanel', () => ({
  PermitBindingPanel: ({ table, canEdit, templateVersionId }: { table: { id: number }; canEdit: boolean; templateVersionId: number }) => (
    <div data-testid="panel-stub" data-can-edit={String(canEdit)} data-table={table.id} data-version={templateVersionId} />
  ),
}));

const table = {
  id: 41,
  code: 'Emissions',
  nameL10n: { values: { en: 'Emission sources' } },
  ordinal: 1,
  rowMode: 'Dynamic',
  maxDynamicRows: null,
  rows: [],
  columns: [],
} as unknown as TemplateTable;

function mount(canEditStructure: boolean, onTab = vi.fn(), tab: 'columns' | 'permit' = 'permit') {
  renderWithQuery(
    <TableWorkspace
      templateVersionId={7}
      located={{ sheet: { code: 'S', nameL10n: { values: { en: 'Sheet' } } } as never, sheetNo: 1, table, no: '1.1' }}
      tab={tab}
      onTab={onTab}
      treeToggle={null}
      columns={null}
      rows={null}
      canEditStructure={canEditStructure}
      deleting={false}
      deletingRuleCode={null}
      onProperties={vi.fn()}
      onConditionalFormat={vi.fn()}
      onDelete={vi.fn()}
      onAddColumn={vi.fn()}
      onFormula={vi.fn()}
      onAddRule={vi.fn()}
      onManageGroups={vi.fn()}
      canEditPresentation={false}
      onDeleteRule={vi.fn()}
    />,
  );

  return { onTab };
}

describe('TableWorkspace — вкладка «Permit binding»', () => {
  it('адреса ?tab=permit розпізнається, невідома вкладка — «columns»', () => {
    expect(parseTab('permit')).toBe('permit');
    expect(parseTab('unknown')).toBe('columns');
  });

  it('вкладка є в смузі, і її вибір просить перемкнути на permit', () => {
    const { onTab } = mount(true, vi.fn(), 'columns');

    fireEvent.click(screen.getByRole('tab', { name: '⟦ctor.tab.permit⟧' }));
    expect(onTab).toHaveBeenCalledWith('permit');
  });

  it('на вкладці панель отримує таблицю, версію й право на правку', async () => {
    mount(true);

    const panel = await screen.findByTestId('panel-stub');
    expect(panel.getAttribute('data-table')).toBe('41');
    expect(panel.getAttribute('data-version')).toBe('7');
    expect(panel.getAttribute('data-can-edit')).toBe('true');
  });

  it('без права на правку структури панель отримує canEdit=false', async () => {
    mount(false);

    expect((await screen.findByTestId('panel-stub')).getAttribute('data-can-edit')).toBe('false');
  });
});
