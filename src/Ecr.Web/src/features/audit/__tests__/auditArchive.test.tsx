import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { CellChangesTable, type CellChange } from '@/features/audit/CellChangesTable';
import { ArchiveWindowDays, archiveWindow } from '@/features/audit/api';
import { testTheme } from '@/test/render';

/**
 * НФ-8.9.6 / D-247: рядок журналу змін комірок, прочитаний з архіву аудиту, позначений «Archive»;
 * кнопка «Archive» ставить вікно на 90 днів перед межею гарячого журналу.
 */

const Base: CellChange = {
  changedAt: '2024-07-10T10:00:00Z',
  periodKey: 202407,
  documentId: 1,
  rowKey: 'R1',
  columnDefId: 2,
  oldValue: '1',
  newValue: '2',
  changedByUserId: 3,
  origin: 'UserEdit',
  isLateEdit: false,
  isOutOfWindow: false,
  fromArchive: false,
  changedByDisplayName: 'D. Nurlanova',
  documentBusinessKey: 'AKT-001',
  documentNameL10n: null,
  columnCode: 'CO2',
  columnHeaderL10n: { values: { en: 'CO2 emissions' } },
  columnDataType: 'Decimal',
};

function rows(items: CellChange[]): HTMLElement[] {
  render(
    <MantineProvider theme={testTheme}>
      <CellChangesTable items={items} />
    </MantineProvider>,
  );

  return screen.getAllByRole('row').slice(1) as HTMLElement[];
}

describe('CellChangesTable: позначка архіву аудиту (НФ-8.9.6)', () => {
  it('рядок з архіву має позначку з поясненням, гарячий — ні', () => {
    const [archived, hot] = rows([
      { ...Base, rowKey: 'A', fromArchive: true },
      { ...Base, rowKey: 'B', fromArchive: false },
    ]);

    // Мутація «прибрати гілку fromArchive» лишає archived без позначки.
    const mark = (archived as HTMLElement).querySelector('[data-audit-archive]') as HTMLElement;
    expect(mark).not.toBeNull();
    expect(mark.textContent).toBe('⟦audit.fromArchive⟧');
    expect(mark.getAttribute('title')).toBe('⟦audit.fromArchiveHint⟧');

    expect((hot as HTMLElement).querySelector('[data-audit-archive]')).toBeNull();
  });
});

describe('archiveWindow: вікно перед межею гарячого журналу (НФ-8.9.6)', () => {
  it('закінчується днем перед першим числом місяця 24 місяці тому, 90 днів включно', () => {
    expect(archiveWindow(new Date(2026, 9, 11))).toEqual({ from: '2024-07-03', to: '2024-09-30' });

    // Через межу року: місяць, що відраховується, — січень.
    expect(archiveWindow(new Date(2026, 0, 15))).toEqual({ from: '2023-10-03', to: '2023-12-31' });
  });

  it('вікно вміщується в серверну стелю 92 доби з запасом на добовий зсув пояса', () => {
    const { from, to } = archiveWindow(new Date(2026, 9, 11));
    const span = (Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / 86_400_000 + 1;

    expect(span).toBe(ArchiveWindowDays);
    expect(span).toBeLessThan(92);
  });
});
