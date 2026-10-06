import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ValidationFindingDto } from '@/api/types';
import { ValidationPanel } from '@/features/documents/ValidationPanel';
import { ruleLabel } from '@/features/documents/ruleLabel';

/**
 * A2-04: колонка «Rule» для знахідки Check показувала `REL-CHK_TOT_v5` — службовий префікс і суфікс версії,
 * який дописує клон шаблону. Тепер — код зв'язку, як його бачить автор шаблону; повний код — у підказці.
 */
const Check = (over: Partial<ValidationFindingDto> = {}): ValidationFindingDto => ({
  severity: 'Error',
  ruleCode: 'REL-CHK_TOT_v5',
  message: 'Check: Mass = 7 (source row S1) does not match Mass = 8',
  tableDefId: 7,
  rowKey: 'TOT',
  columnCode: 'Mass',
  blocksSave: false,
  ...over,
});

describe('A2-04 підпис правила в панелі зауважень', () => {
  it.each([
    ['REL-CHK_TOT_v5', 'CHK_TOT'],
    ['REL-CHK_TOT', 'CHK_TOT'],
    ['REL-CHK_v12', 'CHK'],
    // Лише кінцевий суфікс: `_v` усередині коду — частина назви.
    ['REL-MASS_v2_TOTAL', 'MASS_v2_TOTAL'],
    // Не зв'язок — код правила як є, навіть із `_vN`.
    ['CAP_v2', 'CAP_v2'],
    ['ECR-VAL-RULE', 'ECR-VAL-RULE'],
    // Порожній код зв'язку не перетворюється на порожню клітинку.
    ['REL-', 'REL-'],
  ])('%s → %s', (code, expected) => {
    expect(ruleLabel(code)).toBe(expected);
  });

  it('у колонці — код зв\'язку без REL- і _vN, повний код — у підказці', () => {
    render(
      <MantineProvider>
        <ValidationPanel messages={[Check()]} />
      </MantineProvider>,
    );

    const cell = screen.getByText('CHK_TOT');
    expect(cell.getAttribute('title')).toBe('REL-CHK_TOT_v5');
    expect(screen.queryByText('REL-CHK_TOT_v5')).toBeNull();
  });
});
