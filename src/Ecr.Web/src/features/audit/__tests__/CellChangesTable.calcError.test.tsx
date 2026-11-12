import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { CellChangesTable, originLabel, type CellChange } from '@/features/audit/CellChangesTable';
import { cellChangeOrigins, isHumanOrigin } from '@/features/audit/api';
import { testTheme } from '@/test/render';

/**
 * ✎ C-1 (D-475): журнал змін комірок — подія «Помилка розрахунку» (`CalcError`) і її усунення (`CalcErrorResolved`).
 * Обидві — у фільтрі вкладки, з підписом каталогу й піктограмою, системні (не людські); код і входи — у причині.
 *
 * ⚠ Каталог у тестах не завантажено — `t` дає позначений ключ `⟦ключ⟧`.
 */

const Base: CellChange = {
  changedAt: '2026-11-11T10:00:00Z',
  periodKey: 202611,
  documentId: 1,
  rowKey: 'R1',
  columnDefId: 2,
  oldValue: '5',
  newValue: '5',
  changedByUserId: 0,
  origin: 'CalcError',
  isLateEdit: false,
  isOutOfWindow: false,
  fromArchive: false,
  isOverwrite: false,
  changedByDisplayName: null,
  documentBusinessKey: 'AKT-001',
  documentNameL10n: null,
  columnCode: 'C',
  columnHeaderL10n: { values: { en: 'Ratio' } },
  columnDataType: 'Formula',
  reason: '#DIV/0 · A=10; B=0',
};

function rows(items: CellChange[]): HTMLElement[] {
  render(
    <MantineProvider theme={testTheme}>
      <CellChangesTable items={items} />
    </MantineProvider>,
  );

  return screen.getAllByRole('row').slice(1);
}

describe('CellChangesTable: помилка розрахунку (C-1, D-475)', () => {
  it('обидва походження — у фільтрі журналу, з підписом каталогу, системні', () => {
    expect(cellChangeOrigins).toContain('CalcError');
    expect(cellChangeOrigins).toContain('CalcErrorResolved');
    expect(originLabel('CalcError')).toBe('⟦audit.originLabel.CalcError⟧');
    expect(originLabel('CalcErrorResolved')).toBe('⟦audit.originLabel.CalcErrorResolved⟧');
    expect(isHumanOrigin('CalcError')).toBe(false);
    expect(isHumanOrigin('CalcErrorResolved')).toBe(false);
  });

  it('рядок події — підпис і піктограма походження, код і входи в причині', () => {
    const [appeared, resolved] = rows([
      Base,
      { ...Base, rowKey: 'R2', origin: 'CalcErrorResolved', reason: '#DIV/0' },
    ]);

    const origin = appeared!.querySelector('[data-audit-origin="CalcError"]');
    expect(origin?.textContent).toBe('⟦audit.originLabel.CalcError⟧');
    expect(origin?.querySelector('svg')).not.toBeNull();
    expect(appeared!.querySelector('[data-audit-reason]')?.textContent).toBe('⟦audit.reason⟧: #DIV/0 · A=10; B=0');

    const resolvedOrigin = resolved!.querySelector('[data-audit-origin="CalcErrorResolved"]');
    expect(resolvedOrigin?.textContent).toBe('⟦audit.originLabel.CalcErrorResolved⟧');
    expect(resolvedOrigin?.querySelector('svg')).not.toBeNull();
  });
});
