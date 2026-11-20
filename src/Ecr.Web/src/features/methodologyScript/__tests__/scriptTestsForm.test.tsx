import { fireEvent, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { renderWithQuery } from '@/test/render';
import { TestFormBody, type ScriptTestEditing } from '../ScriptTestsPanel';

/*
 * ⚠ Каталог рядків у тестах не завантажується, тому підписи — `⟦ключ⟧`.
 */

const editing: ScriptTestEditing = {
  code: 'TEST_1',
  isNew: true,
  form: {
    period: 202601,
    documentId: null,
    tableInstanceId: null,
    rowKey: null,
    inputs: [{ name: 'Hours', value: null }],
    expected: [],
    tolerance: '0.0001',
  },
};

function show(): void {
  renderWithQuery(
    <TestFormBody
      editing={editing}
      existingCodes={[]}
      outline={null}
      substances={undefined}
      saving={false}
      onSave={() => undefined}
    />,
  );
}

describe('F13: форма тесту — поля входів', () => {
  it('поле імені входу не перемонтовується на кожен символ (інакше губиться фокус)', () => {
    show();
    const field = screen.getByDisplayValue('Hours');
    field.focus();
    fireEvent.change(field, { target: { value: 'Hours2' } });

    const after = screen.getByDisplayValue('Hours2');
    expect(after).toBe(field);
    expect(document.activeElement).toBe(field);
  });

  it('значення входу показує те, що надруковано: 1. → 1.0 → 1.05 (не стискається до 1 і 15)', () => {
    show();
    const value = screen.getByLabelText(/tests\.value/) as HTMLInputElement;

    for (const typed of ['1', '1.', '1.0', '1.05']) {
      fireEvent.change(value, { target: { value: typed } });
      expect(value.value).toBe(typed);
    }
    expect(screen.getByLabelText(/tests\.value/)).toBe(value);
  });
});
