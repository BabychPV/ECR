import { describe, expect, it, vi } from 'vitest';
import { screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithMantine } from '@/test/render';
import { permitType, table } from '../../dialogs/__tests__/fixtures';
import { TypeChecksTab } from '../TypeChecksTab';

/** Вкладка «Checks» (PM-08c): перелік, область, рівень, порожній стан, кнопка. */

const k = (key: string): string => `⟦permits.editor.checks.${key}⟧`;

const withRules = permitType({
  rules: [
    { code: 'Dates', nameL10n: { en: 'End after start' }, expression: 'ROW.End > ROW.Start', severity: 'Block' },
  ],
  tables: [
    table({
      nameL10n: { en: 'Discharge' },
      rules: [
        { code: 'Months', nameL10n: { en: 'Months add up' }, expression: 'SUM(ROW.M) = ROW.Y', severity: 'Warn' },
      ],
    }),
  ],
});

describe('TypeChecksTab', () => {
  it('рядки: назва, область (весь дозвіл / таблиця), вираз, рівень', () => {
    renderWithMantine(<TypeChecksTab type={withRules} onAddCheck={vi.fn()} />);

    const rows = screen.getAllByRole('row').slice(1);
    expect(rows).toHaveLength(2);

    const first = within(rows[0] as HTMLElement);
    expect(first.getByText('End after start')).toBeDefined();
    expect(first.getByText(k('wholePermit'))).toBeDefined();
    expect(first.getByText('ROW.End > ROW.Start')).toBeDefined();
    expect(first.getByText(k('blocks'))).toBeDefined();

    const second = within(rows[1] as HTMLElement);
    expect(second.getByText('Months add up')).toBeDefined();
    expect(second.getByText('Discharge')).toBeDefined();
    expect(second.getByText(k('warns'))).toBeDefined();
  });

  it('шапка таблиці — чотири колонки мокапу', () => {
    renderWithMantine(<TypeChecksTab type={withRules} onAddCheck={vi.fn()} />);
    const headers = screen.getAllByRole('columnheader').map((h) => h.textContent);
    expect(headers).toEqual([k('colCheck'), k('colAppliesTo'), k('colRule'), k('colIfFails')]);
  });

  it('без перевірок — «No checks yet.» і без таблиці', () => {
    renderWithMantine(<TypeChecksTab type={permitType()} onAddCheck={vi.fn()} />);
    expect(screen.getByText(k('empty'))).toBeDefined();
    expect(screen.queryByRole('table')).toBeNull();
  });

  it('«Add check» викликає onAddCheck; з canEdit=false кнопки немає', async () => {
    const user = userEvent.setup();
    const onAddCheck = vi.fn();
    const view = renderWithMantine(<TypeChecksTab type={permitType()} onAddCheck={onAddCheck} />);

    await user.click(screen.getByRole('button', { name: k('add') }));
    expect(onAddCheck).toHaveBeenCalledTimes(1);
    view.unmount();

    renderWithMantine(<TypeChecksTab type={permitType()} onAddCheck={onAddCheck} canEdit={false} />);
    expect(screen.queryByRole('button', { name: k('add') })).toBeNull();
  });
});
