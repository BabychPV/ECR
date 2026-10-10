import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { CellChangesTable, type CellChange } from '@/features/audit/CellChangesTable';
import { testTheme } from '@/test/render';

/**
 * AN-115: «людина чи система» у колонці «Who» журналу змін комірок.
 *
 * ⛔ Правка в сітці та імпорт книги Excel (`Import`, `ImportOverwrite`) — ім'я людини, яка їх
 * зробила; перерахунок, інтеграція, міграція — «Система». Ім'я того, хто запустив перерахунок,
 * біля числа, яке порахувала формула, читалося б як «це ввів він» — тому воно лише в підказці.
 */

const Base: CellChange = {
  changedAt: '2026-09-24T10:00:00Z',
  periodKey: 202609,
  documentId: 1,
  rowKey: 'R1',
  columnDefId: 2,
  oldValue: '1',
  newValue: '2',
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

function authorCell(origin: string): HTMLElement {
  render(
    <MantineProvider theme={testTheme}>
      <CellChangesTable items={[{ ...Base, origin }]} />
    </MantineProvider>,
  );

  const [row] = screen.getAllByRole('row').slice(1);
  return (row as HTMLElement).querySelector('[data-audit-author]') as HTMLElement;
}

describe('CellChangesTable: автор — людина чи система (AN-115)', () => {
  it.each(['UserEdit', 'Import', 'ImportOverwrite'])('%s — ім’я того, хто змінив', (origin) => {
    const cell = authorCell(origin);

    expect(cell.textContent).toBe('D. Nurlanova');
    expect(cell.getAttribute('title')).toBe('#3');
  });

  it.each(['Recalculation', 'Integration', 'Migration'])('%s — «Система», хто запустив — у підказці', (origin) => {
    const cell = authorCell(origin);

    expect(cell.textContent).toBe('⟦audit.systemAuthor⟧');
    expect(cell.getAttribute('title')).toBe('D. Nurlanova (#3)');
  });
});
