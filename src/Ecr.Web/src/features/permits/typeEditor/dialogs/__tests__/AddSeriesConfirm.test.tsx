import { describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithMantine } from '@/test/render';
import { AddSeriesConfirm } from '../AddSeriesConfirm';
import { units } from './fixtures';

/** Підтвердження «Add 12 month columns?» (PM-08c): склад серії, одиниця, скасування, фокус. */

const k = (key: string): string => `⟦permits.editor.dialogs.${key}⟧`;

function mount(unit: (typeof units)[number] | null = units[0], existingCodes: string[] = ['Name']) {
  const onApply = vi.fn();
  const onClose = vi.fn();
  renderWithMantine(
    <AddSeriesConfirm
      opened
      tableCode="Limits"
      existingCodes={existingCodes}
      unit={unit}
      onApply={onApply}
      onClose={onClose}
    />,
  );

  return { onApply, onClose };
}

describe('AddSeriesConfirm', () => {
  it('фокус на «Скасувати», а не на підтвердженні', async () => {
    mount();
    await waitFor(() => {
      expect(document.activeElement).toBe(screen.getByTestId('confirm-cancel'));
    });
  });

  it('підтвердження додає рівно 12 стовпців-місяців з одиницею, групою й порядком після наявних', async () => {
    const user = userEvent.setup();
    const { onApply, onClose } = mount(units[0], ['Name', 'Annual']);

    await user.click(screen.getByRole('button', { name: k('addSeries.verb') }));

    expect(onApply).toHaveBeenCalledTimes(1);
    const columns = onApply.mock.calls[0]?.[0] as Record<string, unknown>[];
    expect(columns).toHaveLength(12);
    expect(columns.map((c) => c['code'])).toEqual([
      'January', 'February', 'March', 'April', 'May', 'June',
      'July', 'August', 'September', 'October', 'November', 'December',
    ]);
    expect(columns[0]).toEqual({
      code: 'January',
      nameL10n: { en: 'January', ru: 'Январь', kz: 'Қаңтар' },
      dataType: 'Decimal',
      unitId: 3,
      required: false,
      key: false,
      group: 'Estimated_volume_by_month',
      precision: null,
      scale: null,
      listScope: null,
      listRegistryCode: null,
      siblingTableCode: null,
      choices: null,
      order: 3,
    });
    expect(columns[11]?.['order']).toBe(14);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('без одиниці в довіднику діалог каже це і додає серію з unitId null', async () => {
    const user = userEvent.setup();
    const { onApply } = mount(null);

    expect(screen.getByText(k('addSeries.noUnit'))).toBeDefined();
    await user.click(screen.getByRole('button', { name: k('addSeries.verb') }));

    const columns = onApply.mock.calls[0]?.[0] as { unitId: number | null }[];
    expect(columns.every((c) => c.unitId === null)).toBe(true);
  });

  it('скасування й Escape не додають нічого', async () => {
    const user = userEvent.setup();
    const { onApply, onClose } = mount();

    await user.click(screen.getByTestId('confirm-cancel'));
    expect(onClose).toHaveBeenCalledTimes(1);

    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    await waitFor(() => {
      expect(onClose.mock.calls.length).toBeGreaterThanOrEqual(2);
    });
    expect(onApply).not.toHaveBeenCalled();
  });
});
