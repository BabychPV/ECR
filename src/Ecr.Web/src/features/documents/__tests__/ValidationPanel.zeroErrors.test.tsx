import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import { ValidationPanel } from '@/features/documents/ValidationPanel';
import { renderWithMantine } from '@/test/render';

/**
 * `A2-10`: перевірка знайшла лише попередження — значок над переліком казав
 * «ПРОВЕРКА ОБНАРУЖИЛА ОШИБКИ: 0.» (ru), тобто «виявила помилки» там, де
 * помилок немає. На нуль — окремий рядок `document.validationNoErrors`.
 */

afterEach(() => {
  cleanup();
});

function finding(severity: string): never {
  return {
    tableDefId: 1,
    ruleCode: 'R1',
    rowKey: 'R-1',
    columnCode: 'C-1',
    severity,
    message: 'Повідомлення',
  } as never;
}

describe('ValidationPanel: значок числа помилок', () => {
  it('лише попередження — «помилок не знайдено», а не «помилки: 0»', () => {
    renderWithMantine(<ValidationPanel messages={[finding('Warning'), finding('Info')]} />);

    expect(screen.getByText('⟦document.validationNoErrors⟧')).toBeTruthy();
    expect(screen.queryByText(/document\.validationErrors/)).toBeNull();
  });

  it('є помилки — як і раніше, число з `document.validationErrors`', () => {
    renderWithMantine(<ValidationPanel messages={[finding('Error'), finding('Warning')]} />);

    expect(screen.getByText('⟦document.validationErrors (count=1)⟧')).toBeTruthy();
    expect(screen.queryByText('⟦document.validationNoErrors⟧')).toBeNull();
  });
});
