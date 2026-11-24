import { describe, expect, it, vi } from 'vitest';
import { columnDefLabel } from '../ColumnDefPicker';

vi.mock('@/shared/i18n/localized', () => ({
  localized: (text: { values: Record<string, string> } | null) => text?.values['en'] ?? '',
}));

/**
 * ✎ 24.11 (UI-прохід редактора скрипта, знімок людини): вибір колонки в «Required input» показував
 * «Agent (Agent) · S03_STATIONARY_EQUIPMENT/T3_00» — назва й код однакові, той самий текст двічі.
 */
const column = (code: string, header: string | null) => ({
  id: 1,
  code,
  headerL10n: { values: header === null ? {} : { en: header } },
  tableDefId: 2,
  tableCode: 'T3_00',
  sheetDefId: 3,
  sheetCode: 'S03_STATIONARY_EQUIPMENT',
  templateVersionId: 4,
});

describe('columnDefLabel', () => {
  it('назва, що дорівнює коду, не повторюється', () => {
    expect(columnDefLabel(column('Agent', 'Agent'))).toBe('Agent · S03_STATIONARY_EQUIPMENT/T3_00');
    // Регістр коду важливий для скрипта (`row.EMISSION`) — різний регістр лишає код у дужках.
    expect(columnDefLabel(column('EMISSION', 'Emission'))).toBe('Emission (EMISSION) · S03_STATIONARY_EQUIPMENT/T3_00');
  });

  it('без назви — код; інша назва — «Назва (КОД)»', () => {
    expect(columnDefLabel(column('Agent', null))).toBe('Agent · S03_STATIONARY_EQUIPMENT/T3_00');
    expect(columnDefLabel(column('Q', 'Fuel quantity'))).toBe('Fuel quantity (Q) · S03_STATIONARY_EQUIPMENT/T3_00');
  });
});