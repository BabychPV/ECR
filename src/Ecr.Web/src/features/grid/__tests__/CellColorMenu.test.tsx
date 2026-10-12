import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { CellColorMenu } from '../CellColorMenu';

function show(props: Partial<React.ComponentProps<typeof CellColorMenu>> = {}) {
  const onApply = vi.fn();
  render(
    <MantineProvider>
      <CellColorMenu onApply={onApply} disabled={false} disabledHint={null} {...props} />
    </MantineProvider>,
  );

  return onApply;
}

describe('CellColorMenu', () => {
  it('заливка: зразок викликає onApply(fill, hex) і закриває палітру', async () => {
    const onApply = show();
    await userEvent.click(screen.getByTestId('cell-color-fill'));
    await userEvent.click(await screen.findByRole('button', { name: '#ffff00' }));

    expect(onApply).toHaveBeenCalledWith('fill', '#ffff00');
  });

  it('колір тексту: «Автоматичний» скидає (null)', async () => {
    const onApply = show();
    await userEvent.click(screen.getByTestId('cell-color-text'));
    await userEvent.click(await screen.findByTestId('cell-color-text-reset'));

    expect(onApply).toHaveBeenCalledWith('text', null);
  });

  it('«Без заливки» скидає заливку', async () => {
    const onApply = show();
    await userEvent.click(screen.getByTestId('cell-color-fill'));
    await userEvent.click(await screen.findByTestId('cell-color-fill-reset'));

    expect(onApply).toHaveBeenCalledWith('fill', null);
  });

  it('без виділення / права кнопки вимкнені й пояснюють причину', () => {
    show({ disabled: true, disabledHint: 'чому' });

    expect(screen.getByTestId('cell-color-fill').getAttribute('aria-disabled')).toBe('true');
    expect(screen.getByTestId('cell-color-fill').getAttribute('aria-describedby')).toBe('cell-color-fill-why');
    expect(screen.getAllByText('чому')[0]?.id).toBe('cell-color-fill-why');
    expect(screen.getByTestId('cell-color-fill').getAttribute('title')).toBe('чому');
  });

  it('кожен зразок доступний текстом (aria-label = hex)', async () => {
    show();
    await userEvent.click(screen.getByTestId('cell-color-fill'));

    expect((await screen.findAllByRole('button', { name: /^#[0-9a-f]{6}$/ })).length).toBeGreaterThanOrEqual(60);
  });
});
