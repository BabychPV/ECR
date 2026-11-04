import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { CellChangesTable, originLabel, type CellChange } from '@/features/audit/CellChangesTable';
import { cellChangeOrigins, isHumanOrigin } from '@/features/audit/api';
import { cellChangeReasonText } from '@/features/audit/cellChangeReason';
import { testTheme } from '@/test/render';

/**
 * ✎ D-461: журнал змін комірок — ручне значення замість формули. Походження `FormulaOverride`/`FormulaRestored`
 * мають підпис каталогу (а не сирий рядок), причина (`reason`) стоїть під походженням; два системні літерали
 * сервера («Excel import», «Version migration») — словами каталогу, причина людини — як є.
 *
 * ⚠ Каталог у тестах не завантажено — `t` дає позначений ключ `⟦ключ⟧`.
 */

const Base: CellChange = {
  changedAt: '2026-10-30T10:00:00Z',
  periodKey: 202610,
  documentId: 1,
  rowKey: 'R1',
  columnDefId: 2,
  oldValue: '1',
  newValue: '2',
  changedByUserId: 3,
  origin: 'FormulaOverride',
  isLateEdit: false,
  isOutOfWindow: false,
  fromArchive: false,
  isOverwrite: false,
  changedByDisplayName: 'D. Nurlanova',
  documentBusinessKey: 'AKT-001',
  documentNameL10n: null,
  columnCode: 'CO2',
  columnHeaderL10n: { values: { en: 'CO2 emissions' } },
  columnDataType: 'Decimal',
  reason: null,
};

function rows(items: CellChange[]): HTMLElement[] {
  render(
    <MantineProvider theme={testTheme}>
      <CellChangesTable items={items} />
    </MantineProvider>,
  );

  return screen.getAllByRole('row').slice(1);
}

describe('cellChangeReasonText (D-461)', () => {
  it('системні літерали сервера — ключами каталогу, точним збігом', () => {
    expect(cellChangeReasonText('Excel import')).toBe('⟦audit.reasonExcelImport⟧');
    expect(cellChangeReasonText('Version migration')).toBe('⟦audit.reasonVersionMigration⟧');
    // ✎ Аудит 31.10 (F2): перенос на версію, де комірку не рахує жодна формула.
    expect(cellChangeReasonText('Version migration: no formula')).toBe(
      '⟦audit.reasonVersionMigrationNoFormula⟧',
    );
  });

  it('причина людини (навіть схожа на літерал) — як є; порожньої немає', () => {
    expect(cellChangeReasonText('Metering device was replaced')).toBe('Metering device was replaced');
    expect(cellChangeReasonText('excel import')).toBe('excel import');
    expect(cellChangeReasonText(null)).toBeNull();
    expect(cellChangeReasonText(undefined)).toBeNull();
    expect(cellChangeReasonText('   ')).toBeNull();
  });
});

describe('CellChangesTable: ручне значення замість формули (D-461)', () => {
  it('походження FormulaOverride/FormulaRestored — підписом каталогу, причина — під ним', () => {
    const [override, restored, plain] = rows([
      { ...Base, rowKey: 'A', origin: 'FormulaOverride', reason: 'Metering device was replaced' },
      { ...Base, rowKey: 'B', origin: 'FormulaRestored', reason: 'Version migration' },
      { ...Base, rowKey: 'C', origin: 'UserEdit', reason: null },
    ]);

    const overrideOrigin = override!.querySelector('[data-audit-origin="FormulaOverride"]');
    expect(overrideOrigin?.textContent).toBe('⟦audit.originLabel.FormulaOverride⟧');
    // Піктограма є — походження відоме, а не «невідоме серверу значення».
    expect(overrideOrigin?.querySelector('svg')).not.toBeNull();
    const overrideReason = override!.querySelector('[data-audit-reason]') as HTMLElement;
    expect(overrideReason.textContent).toBe('⟦audit.reason⟧: Metering device was replaced');
    expect(overrideReason.getAttribute('title')).toBe('⟦audit.reason⟧: Metering device was replaced');

    expect(restored!.querySelector('[data-audit-origin="FormulaRestored"]')?.textContent).toBe(
      '⟦audit.originLabel.FormulaRestored⟧',
    );
    expect(restored!.querySelector('[data-audit-reason]')?.textContent).toContain('⟦audit.reasonVersionMigration⟧');
    expect(restored!.querySelector('[data-audit-reason]')?.textContent).not.toContain('Version migration');

    // Без причини — рядка причини немає.
    expect(plain!.querySelector('[data-audit-reason]')).toBeNull();
  });

  it('імпорт, що замінив формулу, — причина «Excel import» словами каталогу', () => {
    const [row] = rows([{ ...Base, reason: 'Excel import' }]);

    expect(row!.querySelector('[data-audit-reason]')?.textContent).toBe('⟦audit.reason⟧: ⟦audit.reasonExcelImport⟧');
  });

  it('обидва походження — у фільтрі журналу, з підписом, і людські (як серверний IsHuman)', () => {
    expect(cellChangeOrigins).toContain('FormulaOverride');
    expect(cellChangeOrigins).toContain('FormulaRestored');
    expect(originLabel('FormulaOverride')).toBe('⟦audit.originLabel.FormulaOverride⟧');
    expect(originLabel('FormulaRestored')).toBe('⟦audit.originLabel.FormulaRestored⟧');
    expect(isHumanOrigin('FormulaOverride')).toBe(true);
    expect(isHumanOrigin('FormulaRestored')).toBe(true);
  });

  it('автор ручного значення — людина, а не «Система»', () => {
    const [row] = rows([{ ...Base, origin: 'FormulaOverride', reason: 'x' }]);

    expect(row!.querySelector('[data-audit-author]')?.textContent).toBe('D. Nurlanova');
  });
});
