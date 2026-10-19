import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { TestProviders } from '@/test/render';
import { Catalog } from '@/test/__tests__/a11yFixtures';
import { ColumnsTable } from '../ColumnsTable';
import { addTable, withListScope, newColumn, addColumn } from '../draftModel';
import { HeadNode } from '../editorActions';
import { columnTypeLabel, dataTypeLabel, secondaryTextOf, textOf } from '../labels';
import { StructureTree } from '../StructureTree';
import { UnderTheHood } from '../UnderTheHood';
import { column, draftFixture, permitType } from './fixtures';

/**
 * Дерево, таблиця стовпців і «Under the hood» (PM-08b). Тексти — справжні, із `09-seed.sql`.
 *
 * Мутаційний доказ (виконано вручну, відновлено): `aria-current={selected === code}` → `true` для всіх
 * валить «aria-current лише на вибраному»; у `ColumnsTable` `{column.unitId === null ? '' : …}` →
 * `{column.unitId}` валить «без голого числа замість одиниці».
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    (Catalog[key] ?? `⟦${key}⟧`).replace(/\{(\w+)\}/g, (match, name: string) =>
      params !== undefined && name in params ? String(params[name]) : match,
    ),
}));

afterEach(cleanup);

function show(node: ReactNode): void {
  render(
    <TestProviders>
      <MemoryRouter>{node}</MemoryRouter>
    </TestProviders>,
  );
}

describe('StructureTree', () => {
  const draft = draftFixture();

  it('вузол «Permit details» з кількістю полів і таблиці з кількістю стовпців', () => {
    show(<StructureTree draft={draft} headerFieldCount={8} selected="Discharge" editable onSelect={() => undefined} open />);
    const tree = screen.getByRole('complementary', { name: 'Tables of this type' });
    const buttons = within(tree).getAllByRole('button').filter((b) => b.className.includes('pt-node'));
    expect(buttons.map((b) => b.textContent)).toEqual(['Permit details8', 'Discharge3', 'Storage1']);
  });

  it('aria-current лише на вибраному вузлі', () => {
    show(<StructureTree draft={draft} headerFieldCount={8} selected="Storage" editable onSelect={() => undefined} open />);
    const nodes = screen.getAllByRole('button').filter((b) => b.className.includes('pt-node'));
    expect(nodes.filter((b) => b.getAttribute('aria-current') === 'true').map((b) => b.textContent)).toEqual(['Storage1']);
  });

  it('клік по вузлу повідомляє код; для шапки — HeadNode', () => {
    const onSelect = vi.fn();
    show(<StructureTree draft={draft} headerFieldCount={8} selected="Storage" editable onSelect={onSelect} open />);
    fireEvent.click(screen.getByRole('button', { name: /^Discharge/ }));
    fireEvent.click(screen.getByRole('button', { name: /^Permit details/ }));
    expect(onSelect.mock.calls.map((c) => c[0])).toEqual(['Discharge', HeadNode]);
  });

  it('кнопки «Add table» (дві) є лише для правної чернетки', () => {
    show(<StructureTree draft={draft} headerFieldCount={8} selected="Storage" editable onSelect={() => undefined} open />);
    expect(screen.getByRole('button', { name: 'Add table' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Add another table' })).toBeTruthy();
    cleanup();
    show(<StructureTree draft={draft} headerFieldCount={8} selected="Storage" editable={false} onSelect={() => undefined} open />);
    expect(screen.queryByRole('button', { name: /Add/ })).toBeNull();
  });

  it('без таблиць — чесне пояснення', () => {
    show(<StructureTree draft={{ ...draft, tables: [] }} headerFieldCount={8} selected={HeadNode} editable onSelect={() => undefined} open />);
    expect(screen.getByText(/No tables yet/)).toBeTruthy();
  });

  it('data-open керує показом дерева на вузькому екрані', () => {
    show(<StructureTree draft={draft} headerFieldCount={8} selected="Storage" editable onSelect={() => undefined} open={false} />);
    expect(screen.getByRole('complementary', { name: 'Tables of this type', hidden: true }).getAttribute('data-open')).toBe('false');
  });
});

describe('ColumnsTable', () => {
  const type = permitType();
  const tables = type.tables;

  it('шапка стовпців і рядок: назва + друга мова, код, тип, одиниця, галочки, група', () => {
    const columns = [
      ...tables[0]!.columns.slice(0, 2),
      { ...tables[0]!.columns[2]!, group: 'Volumes' },
    ];
    show(<ColumnsTable columns={columns} tables={tables} selectedCode={null} unitCode={(id) => (id === 7 ? 't/yr' : '')} onSelect={() => undefined} />);
    expect(screen.getAllByRole('columnheader').map((h) => h.textContent)).toEqual([
      'Column', 'Code', 'Type', 'Unit', 'Required', 'Part of key', 'Group',
    ]);
    const row = screen.getByRole('row', { name: 'Annual' });
    expect(within(row).getByText('Annual ru')).toBeTruthy();
    expect(within(row).getByText('Number')).toBeTruthy();
    expect(within(row).getByText('t/yr')).toBeTruthy();
    expect(within(row).getByText('Volumes')).toBeTruthy();
    expect(within(row).queryAllByRole('img')).toHaveLength(0);
    const key = screen.getByRole('row', { name: 'Water_body' });
    expect(within(key).getAllByRole('img').map((i) => i.getAttribute('aria-label'))).toEqual(['Required', 'Part of key']);
  });

  it('невідома одиниця — порожньо, а не голе число', () => {
    show(<ColumnsTable columns={[tables[0]!.columns[2]!]} tables={tables} selectedCode={null} unitCode={() => ''} onSelect={() => undefined} />);
    const row = screen.getByRole('row', { name: 'Annual' });
    expect(row.textContent).not.toContain('7');
  });

  it('клік і Enter/Space вибирають стовпець; вибраний має aria-selected', () => {
    const onSelect = vi.fn();
    show(<ColumnsTable columns={tables[0]!.columns} tables={tables} selectedCode="Annual" unitCode={() => ''} onSelect={onSelect} />);
    const row = screen.getByRole('row', { name: 'Sub_period' });
    fireEvent.click(row);
    fireEvent.keyDown(row, { key: 'Enter' });
    fireEvent.keyDown(row, { key: ' ' });
    fireEvent.keyDown(row, { key: 'a' });
    expect(onSelect.mock.calls.map((c) => c[0])).toEqual(['Sub_period', 'Sub_period', 'Sub_period']);
    expect(screen.getByRole('row', { name: 'Annual' }).getAttribute('aria-selected')).toBe('true');
    expect(row.getAttribute('aria-selected')).toBe('false');
  });

  it('readOnly (поля шапки): рядки не інтерактивні', () => {
    const onSelect = vi.fn();
    show(<ColumnsTable columns={type.headerFields} tables={tables} selectedCode={null} unitCode={() => ''} readOnly onSelect={onSelect} />);
    const rows = screen.getAllByRole('row').slice(1);
    expect(rows.every((r) => r.getAttribute('tabindex') === null)).toBe(true);
    fireEvent.click(rows[0]!);
    expect(onSelect).not.toHaveBeenCalled();
  });

  it('порожня таблиця — рядок з поясненням', () => {
    show(<ColumnsTable columns={[]} tables={tables} selectedCode={null} unitCode={() => ''} onSelect={() => undefined} />);
    expect(screen.getByText('No columns yet')).toBeTruthy();
  });
});

describe('UnderTheHood', () => {
  it('таблиця: реєстр, «Part of», живуть, поки чинний дозвіл', () => {
    show(<UnderTheHood registryCode="PRM_WaterDischarge_Storage" parentCode="PRM_WaterDischarge" />);
    expect(screen.getByText('Under the hood')).toBeTruthy();
    expect(screen.getByText(/saved as the registry PRM_WaterDischarge_Storage, a part of PRM_WaterDischarge/)).toBeTruthy();
    expect(screen.getByText('Part of')).toBeTruthy();
    expect(screen.getByText('while the permit is valid')).toBeTruthy();
  });

  it('шапка: без «Part of»', () => {
    show(<UnderTheHood registryCode="PRM_WaterDischarge" />);
    expect(screen.queryByText('Part of')).toBeNull();
    expect(screen.getByText(/permit details are saved as the registry PRM_WaterDischarge/)).toBeTruthy();
  });
});

describe('підписи', () => {
  it('textOf: мова → en → будь-яка → порожньо', () => {
    expect(textOf({ en: 'A', ru: 'Б' }, 'ru')).toBe('Б');
    expect(textOf({ en: 'A', ru: ' ' }, 'ru')).toBe('A');
    expect(textOf({ kz: 'К' }, 'ru')).toBe('К');
    expect(textOf({}, 'ru')).toBe('');
    expect(textOf(null)).toBe('');
  });

  it('secondaryTextOf: інша мова, ніж основний підпис', () => {
    expect(secondaryTextOf({ en: 'Water', ru: 'Вода' }, 'Water')).toBe('Вода');
    expect(secondaryTextOf({ en: 'Water' }, 'Water')).toBe('');
  });

  it('тип стовпця: звичайні, спільний реєстр, список типу, таблиця дозволу', () => {
    const base = draftFixture();
    const shared = { ...withListScope(newColumn('Sub', { en: 'Sub' }, 'Lookup'), 'shared'), listRegistryCode: 'Substances' };
    const own = { ...withListScope(newColumn('Use', { en: 'Use' }, 'Lookup'), 'type'), choices: ['a'] };
    const permit = withListScope(newColumn('Place', { en: 'Place' }, 'Lookup'), 'permit', ['Storage']);
    const draft = [shared, own, permit].reduce((d, c) => addColumn(d, 'Discharge', c), addTable(base, { code: 'Extra', nameL10n: { en: 'Extra' }, columns: [column('K', { key: true })] }));
    const tables = draft.tables;
    expect(columnTypeLabel(shared, tables)).toBe('Link → Substances');
    expect(columnTypeLabel(own, tables)).toBe('Choice');
    expect(columnTypeLabel(permit, tables)).toBe('Link → Storage (this permit)');
    expect(columnTypeLabel(column('N', { dataType: 'Decimal' }), tables)).toBe('Number');
    expect(dataTypeLabel('Bool')).toBe('Yes / No');
    expect(dataTypeLabel('Unknown')).toBe('Unknown');
  });
});
