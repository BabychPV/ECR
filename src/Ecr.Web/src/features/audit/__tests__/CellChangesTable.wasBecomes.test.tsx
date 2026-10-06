import { describe, it, expect } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { CellChangesTable, auditDeltaText, type CellChange } from '@/features/audit/CellChangesTable';
import { testTheme } from '@/test/render';

/**
 * UI-38 (макет `docs/design/hybrid/screens-ops.js` `/admin/audit`): журнал читається як історія —
 * старе значення закреслене → нове, Δ для числа, «new» для першого значення, походження словом і
 * піктограмою, пізня правка позначена.
 */

const Change: CellChange = {
  changedAt: '2026-09-24T10:00:00Z',
  periodKey: 202609,
  documentId: 1,
  rowKey: 'R1',
  columnDefId: 2,
  oldValue: '53.1771000000000000',
  newValue: '54.2000000000000000',
  changedByUserId: 3,
  origin: 'UserEdit',
  isLateEdit: false,
  isOutOfWindow: false,
  changedByDisplayName: 'D. Nurlanova',
  documentBusinessKey: 'AKT-001',
  documentNameL10n: null,
  columnCode: 'CO2',
  columnHeaderL10n: { values: { en: 'CO2 emissions' } },
  columnDataType: 'Decimal',
};

function show(items: CellChange[]): HTMLElement[] {
  render(
    <MantineProvider theme={testTheme}>
      <CellChangesTable items={items} />
    </MantineProvider>,
  );

  return screen.getAllByRole('row').slice(1);
}

describe('CellChangesTable: Was → becomes', () => {
  it('колонки макета: When · Who · Document·cell · Was→becomes · Origin · Late', () => {
    show([Change]);

    const headers = screen.getAllByRole('columnheader').map((th) => th.textContent);
    expect(headers).toEqual([
      '⟦audit.when⟧',
      '⟦audit.who⟧',
      '⟦audit.documentCell⟧',
      '⟦audit.wasBecomes⟧',
      '⟦audit.origin⟧',
      '⟦audit.lateMark⟧',
    ]);
  });

  it('старе закреслене, нове поруч, Δ зі знаком; читалка чує «було/стало»', () => {
    const [row] = show([Change]);
    const cell = (row as HTMLElement).querySelector('[data-audit-was-becomes]') as HTMLElement;

    // ⛔ Мутація «прибрати td=line-through» — старе значення не закреслене.
    const was = within(cell).getByText('53.1771');
    expect(was.getAttribute('data-audit-was')).toBe('');
    expect(getComputedStyle(was).textDecoration).toContain('line-through');
    expect(within(cell).getByText('54.2').getAttribute('data-audit-becomes')).toBe('');
    // ⛔ Мутація «Δ = було − стало» дає «-1.0229».
    expect(cell.querySelector('[data-audit-delta]')?.textContent).toBe('⟦audit.delta⟧+1.0229');
    // Порядок для читалки: «Was 53.1771 Becomes 54.2».
    expect(cell.textContent).toBe('⟦import.was⟧53.1771⟦import.becomes⟧54.2⟦audit.delta⟧+1.0229');
  });

  it('перше значення комірки: «—» не закреслене, позначка «new», без Δ', () => {
    const [row] = show([{ ...Change, oldValue: null }]);
    const cell = (row as HTMLElement).querySelector('[data-audit-was-becomes]') as HTMLElement;

    expect(getComputedStyle(within(cell).getByText('—')).textDecoration).not.toContain('line-through');
    expect(within(cell).getByText('⟦audit.newValue⟧')).toBeTruthy();
    expect(cell.querySelector('[data-audit-delta]')).toBeNull();
  });

  it('текстова колонка — без Δ', () => {
    expect(auditDeltaText({ oldValue: '1', newValue: '2', columnDataType: 'String' })).toBeNull();
    expect(auditDeltaText({ oldValue: '2', newValue: '1', columnDataType: 'Int' })).toBe('-1');
    expect(auditDeltaText({ oldValue: '2', newValue: '2', columnDataType: 'Decimal' })).toBe('0');
  });

  it('походження словом і піктограмою; невідоме — як є', () => {
    const rows = show([
      { ...Change, origin: 'Import' },
      { ...Change, rowKey: 'R2', origin: 'Recalculation' },
      { ...Change, rowKey: 'R3', origin: 'Collector' },
    ]);

    const origin = (row: HTMLElement | undefined): HTMLElement => (row as HTMLElement).querySelector('[data-audit-origin]') as HTMLElement;

    expect(origin(rows[0]).textContent).toBe('⟦audit.originLabel.Import⟧');
    expect(origin(rows[0]).querySelector('svg')).not.toBeNull();
    expect(origin(rows[1]).textContent).toBe('⟦audit.originLabel.Recalculation⟧');
    // ⛔ Невідоме серверу значення не ховається за чужим словом: журнал — доказ.
    expect(origin(rows[2]).textContent).toBe('Collector');
    expect(origin(rows[2]).querySelector('svg')).toBeNull();
  });

  it('пізня правка — позначка Late з поясненням; звичайна — порожньо', () => {
    const [late, plain] = show([{ ...Change, isLateEdit: true }, { ...Change, rowKey: 'R2' }]);

    // ⛔ Мутація «прибрати гілку isLateEdit» лишає рядок без позначки.
    const mark = (late as HTMLElement).querySelector('[data-audit-late]') as HTMLElement;
    expect(mark.textContent).toBe('⟦audit.lateMark⟧');
    expect(mark.getAttribute('title')).toBe('⟦audit.lateHint⟧');
    expect((plain as HTMLElement).querySelector('[data-audit-late]')).toBeNull();
  });

  it('комірка — верхнім рядком, документ і період — нижнім', () => {
    const [row] = show([Change]);

    expect(within(row as HTMLElement).getByText('R1 · CO2 emissions (CO2)').getAttribute('data-two-line-primary')).toBe('');
    expect(within(row as HTMLElement).getByText('AKT-001').closest('[data-two-line-secondary]')).not.toBeNull();
  });
});
