import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen } from '@testing-library/react';
import { renderWithMantine } from '@/test/render';
import { DataTable } from '@/shared/ui/DataTable/DataTable';
import type { DataTableColumn } from '@/shared/ui/DataTable/types';

/**
 * PUI-1: рядок із `onRowClick` відкривається з клавіатури, лише коли екран просить `rowFocusable`.
 * Мутаційний доказ (вручну, відновлено): прибрати перевірку `event.target !== event.currentTarget` - тест про вкладену
 * кнопку червоний; прибрати `tabIndex` - тест про Tab червоний.
 */
afterEach(() => cleanup());

interface Item {
  readonly id: string;
}

const columns: DataTableColumn<Item>[] = [
  { key: 'id', label: 'Id' },
  { key: 'act', label: 'Act', render: (row) => <button type="button" data-testid={`inner-${row.id}`}>Do</button> },
];

function mount(onRowClick: ((row: Item) => void) | undefined, rowFocusable: boolean | undefined): void {
  renderWithMantine(
    <DataTable<Item>
      columns={columns}
      rows={[{ id: 'a' }, { id: 'b' }]}
      rowKey={(row) => row.id}
      rowLabel={(row) => `Row ${row.id}`}
      onRowClick={onRowClick}
      rowFocusable={rowFocusable}
    />,
  );
}

describe('DataTable: рядок з клавіатури', () => {
  it('rowFocusable + onRowClick: tabindex=0, Enter і Space викликають onRowClick, Space без прокрутки', () => {
    const click = vi.fn();
    mount(click, true);
    const row = screen.getByRole('row', { name: 'Row a' });

    expect(row.getAttribute('tabindex')).toBe('0');
    row.focus();
    expect(document.activeElement).toBe(row);

    expect(fireEvent.keyDown(row, { key: 'Enter' })).toBe(false);
    expect(fireEvent.keyDown(row, { key: ' ' })).toBe(false);
    expect(click.mock.calls).toEqual([[{ id: 'a' }], [{ id: 'a' }]]);

    fireEvent.keyDown(row, { key: 'ArrowDown' });
    expect(click).toHaveBeenCalledTimes(2);
  });

  it('Enter/Space на вкладеній кнопці рядок не відкривають', () => {
    const click = vi.fn();
    mount(click, true);
    const inner = screen.getByTestId('inner-a');

    fireEvent.keyDown(inner, { key: 'Enter' });
    fireEvent.keyDown(inner, { key: ' ' });

    expect(click).not.toHaveBeenCalled();
  });

  it('без rowFocusable (типово) і без onRowClick рядок не фокусується', () => {
    mount(vi.fn(), undefined);
    expect(screen.getByRole('row', { name: 'Row a' }).hasAttribute('tabindex')).toBe(false);
    cleanup();

    mount(undefined, true);
    expect(screen.getByRole('row', { name: 'Row a' }).hasAttribute('tabindex')).toBe(false);
  });
});
