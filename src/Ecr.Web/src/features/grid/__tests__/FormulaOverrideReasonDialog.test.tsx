import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import FormulaOverrideReasonDialog from '@/features/grid/FormulaOverrideReasonDialog';

/** ✎ D-461: причина обов'язкова — «Зберегти» вимкнена, доки причина не пройде перевірку сервера. */
function show(onSave = vi.fn(), onCancel = vi.fn(), initialReason = ''): { onSave: typeof onSave; onCancel: typeof onCancel } {
  render(
    <MantineProvider>
      <FormulaOverrideReasonDialog count={1} initialReason={initialReason} onSave={onSave} onCancel={onCancel} />
    </MantineProvider>,
  );

  return { onSave, onCancel };
}

function saveButton(): HTMLButtonElement {
  return document.querySelector('[data-formula-override-save]') as HTMLButtonElement;
}

function reasonField(): HTMLTextAreaElement {
  return document.querySelector('textarea[data-formula-override-reason]') as HTMLTextAreaElement;
}

describe('FormulaOverrideReasonDialog', () => {
  it('порожня причина — «Зберегти» вимкнена; введена — обрізана їде в onSave', () => {
    const { onSave } = show();
    expect(saveButton().disabled).toBe(true);

    fireEvent.change(reasonField(), { target: { value: '   ' } });
    expect(saveButton().disabled).toBe(true);

    fireEvent.change(reasonField(), { target: { value: '  meter replaced  ' } });
    expect(saveButton().disabled).toBe(false);
    fireEvent.click(saveButton());
    expect(onSave).toHaveBeenCalledWith('meter replaced');
  });

  it('понад 1000 символів — помилка поля, зберегти не можна', () => {
    show();
    fireEvent.change(reasonField(), { target: { value: 'x'.repeat(1001) } });
    expect(saveButton().disabled).toBe(true);
    expect(screen.getByText(/grid\.formulaOverride\.reasonTooLong/)).toBeTruthy();
  });

  it('наявна причина ручної комірки — у полі одразу; «Скасувати» — onCancel', () => {
    const { onCancel } = show(vi.fn(), vi.fn(), 'old reason');
    expect(reasonField().value).toBe('old reason');
    fireEvent.click(screen.getByRole('button', { name: /grid\.confirmCancel/ }));
    expect(onCancel).toHaveBeenCalled();
  });
});
