import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { testTheme } from '@/test/render';
import { EndEarlyModal } from '../EndEarlyModal';

/**
 * KI-04: поріг довжини причини в «End early» видно людині. Поки причина коротша за `minLength`, під полем (в його описі)
 * стоїть підказка з числом; кнопка підтвердження вимкнена; щойно порогу досягнуто - підказка зникає, кнопка вмикається.
 *
 * Мутаційний доказ (вручну, відновлено): прибрати блок `{enough ? null : <Text … end-reason-hint>}` в `EndEarlyModal` -
 * валить усі три тести цього файлу.
 */
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  t: (key: string, params?: Record<string, string | number>) =>
    params === undefined ? key : `${key} ${JSON.stringify(params)}`,
}));

afterEach(cleanup);

function open(): void {
  render(
    <MantineProvider theme={testTheme}>
      <EndEarlyModal
        opened
        title="End"
        label="Reason"
        description="Consequences"
        confirmLabel="Do end"
        minLength={10}
        isPending={false}
        error={null}
        onEdit={vi.fn()}
        returnFocusTo={() => null}
        onConfirm={vi.fn()}
        onClose={vi.fn()}
      />
    </MantineProvider>,
  );
}

describe('EndEarlyModal: підказка мінімальної довжини причини (KI-04)', () => {
  it('порожня причина: підказка з числом 10 видна, зв\'язана з полем через aria-describedby, кнопка вимкнена', async () => {
    open();

    const dialog = await screen.findByRole('dialog');
    const hint = within(dialog).getByTestId('end-reason-hint');
    expect(hint.textContent).toContain('permits.endEarly.minLengthHint');
    expect(hint.textContent).toContain('"min":"10"');

    const field = within(dialog).getByRole('textbox', { name: /Reason/ });
    const describedBy = (field.getAttribute('aria-describedby') ?? '').split(' ');
    const description = dialog.querySelector(`[id="${describedBy.find((id) => id.endsWith('-description')) ?? 'none'}"]`);
    expect(description, 'опис поля є й зв\'язаний з ним').not.toBeNull();
    expect(description?.contains(hint)).toBe(true);

    expect((within(dialog).getByRole('button', { name: 'Do end' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('9 символів: підказка лишається; 10: зникає, кнопка вмикається', async () => {
    open();
    const dialog = await screen.findByRole('dialog');
    const field = within(dialog).getByRole('textbox', { name: /Reason/ });
    const confirm = within(dialog).getByRole('button', { name: 'Do end' }) as HTMLButtonElement;

    fireEvent.change(field, { target: { value: '123456789' } });
    expect(within(dialog).queryByTestId('end-reason-hint')).not.toBeNull();
    expect(confirm.disabled).toBe(true);

    fireEvent.change(field, { target: { value: '1234567890' } });
    expect(within(dialog).queryByTestId('end-reason-hint')).toBeNull();
    expect(confirm.disabled).toBe(false);
  });

  it('пробіли не рахуються: 10 пробілів - підказка лишається', async () => {
    open();
    const dialog = await screen.findByRole('dialog');

    fireEvent.change(within(dialog).getByRole('textbox', { name: /Reason/ }), { target: { value: '          ' } });

    expect(within(dialog).queryByTestId('end-reason-hint')).not.toBeNull();
  });
});
