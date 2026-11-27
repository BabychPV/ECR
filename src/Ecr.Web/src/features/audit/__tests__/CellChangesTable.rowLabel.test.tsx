import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { CellChangesTable, rowText, type CellChange } from '@/features/audit/CellChangesTable';
import { testTheme } from '@/test/render';

/**
 * audit-filters-followup AC-12: у «Document · cell» сирий GUID-ключ рядка (рядок, доданий людиною) не показується
 * 32 символами. Джерело людської мітки — `rowLabelL10n` (`cfg.RowDef.LabelL10n` фіксованого рядка); у динамічного
 * рядка мітки немає ніде, тож «Row» + початок ключа, повний ключ — у підказці.
 */
const Guid = 'f9f64b6282654c8083b2dd81252770be';

function change(rowKey: string, label?: string): CellChange {
  return {
    changedAt: '2026-11-20T10:00:00Z',
    periodKey: 202611,
    documentId: 96,
    rowKey,
    ...(label === undefined ? {} : { rowLabelL10n: { values: { en: label } } }),
    columnDefId: 1,
    columnCode: 'M10',
    oldValue: '1',
    newValue: '2',
    changedByUserId: 5,
    origin: 'UserEdit',
    isLateEdit: false,
    isOutOfWindow: false,
    isOverwrite: false,
    fromArchive: false,
  } as CellChange;
}

afterEach(cleanup);

describe('rowText', () => {
  it('мітка рядка шаблону > короткий код як є > GUID скорочено', () => {
    expect(rowText(change('r1', 'Boiler 1'))).toBe('Boiler 1');
    expect(rowText(change('LTD-A02'))).toBe('LTD-A02');
    expect(rowText(change(Guid))).toBe('⟦grid.rowLabelHeader⟧ f9f64b62…');
  });

  it('AC-13: рядок, доданий людиною, з номером на екрані — «Row N»; мітка шаблону важливіша за номер', () => {
    // Мутація (локально): прибрати гілку `rowOrdinal` у `rowText` — червоне (знову «Row f9f64b62…»).
    expect(rowText({ ...change(Guid), rowOrdinal: 7 })).toBe('⟦grid.rowLabelHeader⟧ 7');
    expect(rowText({ ...change(Guid, 'Boiler 1'), rowOrdinal: 7 })).toBe('Boiler 1');
    // ✎ DOCUX (огляд UI 27.11, координатор): рядок без мітки — «Row N» для БУДЬ-ЯКОГО ключа, як рядок формули й інспектор
    // (згенерований ключ «p1-T3_00-0020» — не код для людини). Доти читабельний ключ лишався ключем (AC-13).
    expect(rowText({ ...change('LTD-A02'), rowOrdinal: 3 })).toBe('⟦grid.rowLabelHeader⟧ 3');
    expect(rowText({ ...change('p1-T3_00-0020'), rowOrdinal: 20 })).toBe('⟦grid.rowLabelHeader⟧ 20');
    // ⛔ b19: «Row 0» не показується ніколи (0 — не позиція на екрані).
    expect(rowText({ ...change('p1-T3_00-0020'), rowOrdinal: 0 })).toBe('p1-T3_00-0020');
    expect(rowText({ ...change(Guid), rowOrdinal: 0 })).toBe('⟦grid.rowLabelHeader⟧ f9f64b62…');
    expect(rowText({ ...change(Guid), rowOrdinal: null })).toBe('⟦grid.rowLabelHeader⟧ f9f64b62…');
  });
});

describe('CellChangesTable: рядок комірки', () => {
  it('GUID не показується повністю, повний ключ — у підказці', () => {
    render(
      <MantineProvider theme={testTheme}>
        <CellChangesTable items={[change(Guid), change('LTD-A02')]} />
      </MantineProvider>,
    );
    expect(screen.queryByText(new RegExp(Guid, 'u'))).toBeNull();
    expect(screen.getByText(/f9f64b62…/u)).toBeTruthy();
    expect(screen.getByText(/LTD-A02 · /u)).toBeTruthy();
    expect(document.querySelector(`[title*="${Guid}"]`)).not.toBeNull();
  });
});
