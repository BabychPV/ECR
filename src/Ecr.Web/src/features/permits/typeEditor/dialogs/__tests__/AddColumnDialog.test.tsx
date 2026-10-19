import { describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithMantine } from '@/test/render';
import { AddColumnDialog } from '../AddColumnDialog';
import { units } from './fixtures';

/** Діалог «Add a column» (PM-08c): перевірка, точний склад стовпця, скасування, фокус. */

const k = (key: string): string => `⟦permits.editor.dialogs.${key}⟧`;

function mount(overrides: Partial<Parameters<typeof AddColumnDialog>[0]> = {}) {
  const onApply = vi.fn();
  const onClose = vi.fn();
  renderWithMantine(
    <AddColumnDialog
      opened
      tableCode="Limits"
      existingCodes={['Name', 'Annual']}
      units={units}
      onApply={onApply}
      onClose={onClose}
      {...overrides}
    />,
  );

  return { onApply, onClose };
}

describe('AddColumnDialog', () => {
  it('початковий фокус — на полі назви', async () => {
    mount();
    await waitFor(() => {
      expect(document.activeElement).toBe(screen.getByLabelText(k('col.nameEn')));
    });
  });

  it('порожня назва: помилка біля поля, фокус на ньому, onApply не викликано', async () => {
    const user = userEvent.setup();
    const { onApply, onClose } = mount();

    await user.click(screen.getByRole('button', { name: k('addColumn.apply') }));

    expect(await screen.findByText(k('col.nameRequired'))).toBeDefined();
    expect(document.activeElement).toBe(screen.getByLabelText(k('col.nameEn')));
    expect(onApply).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('точний склад стовпця: код з назви, унікальний, тип Number, одиниця, обов’язковий', async () => {
    const user = userEvent.setup();
    const { onApply, onClose } = mount();

    await user.type(screen.getByLabelText(k('col.nameEn')), 'Annual');
    await user.click(screen.getByLabelText(k('col.type')));
    await user.click(await screen.findByRole('option', { name: k('type.Decimal') }));
    await user.click(screen.getByLabelText(k('col.unit')));
    await user.click(await screen.findByRole('option', { name: 't' }));
    await user.click(screen.getByLabelText(k('col.required')));
    await user.click(screen.getByRole('button', { name: k('addColumn.apply') }));

    expect(onApply).toHaveBeenCalledTimes(1);
    expect(onApply).toHaveBeenCalledWith({
      code: 'Annual_2',
      nameL10n: { en: 'Annual' },
      dataType: 'Decimal',
      unitId: 4,
      required: true,
      key: false,
      group: null,
      precision: null,
      scale: null,
      listScope: null,
      listRegistryCode: null,
      siblingTableCode: null,
      choices: null,
      order: 3,
    });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('одиниця недоступна для тексту й скидається при зміні типу на нечисловий', async () => {
    const user = userEvent.setup();
    const { onApply } = mount();

    await user.type(screen.getByLabelText(k('col.nameEn')), 'Note');
    expect((screen.getByLabelText(k('col.unit')) as HTMLInputElement).disabled).toBe(true);

    await user.click(screen.getByLabelText(k('col.type')));
    await user.click(await screen.findByRole('option', { name: k('type.Decimal') }));
    await user.click(screen.getByLabelText(k('col.unit')));
    await user.click(await screen.findByRole('option', { name: 't' }));

    await user.click(screen.getByLabelText(k('col.type')));
    await user.click(await screen.findByRole('option', { name: k('type.Date') }));
    await user.click(screen.getByRole('button', { name: k('addColumn.apply') }));

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({ dataType: 'Date', unitId: null }));
  });

  it('Enter у полі назви додає стовпець', async () => {
    const user = userEvent.setup();
    const { onApply } = mount();

    await user.type(screen.getByLabelText(k('col.nameEn')), 'Volume{Enter}');

    expect(onApply).toHaveBeenCalledWith(expect.objectContaining({ code: 'Volume', dataType: 'String' }));
  });

  it('скасування і Escape закривають без застосування', async () => {
    const user = userEvent.setup();
    const { onApply, onClose } = mount();

    await user.type(screen.getByLabelText(k('col.nameEn')), 'Volume');
    await user.click(screen.getByRole('button', { name: '⟦common.cancel⟧' }));
    expect(onClose).toHaveBeenCalledTimes(1);

    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    await waitFor(() => {
      expect(onClose.mock.calls.length).toBeGreaterThanOrEqual(2);
    });
    expect(onApply).not.toHaveBeenCalled();
  });
});
