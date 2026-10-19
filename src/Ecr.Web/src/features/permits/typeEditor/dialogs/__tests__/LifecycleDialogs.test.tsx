import { describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { renderWithMantine } from '@/test/render';
import { DeleteTypeConfirm } from '../DeleteTypeConfirm';
import { NewVersionConfirm } from '../NewVersionConfirm';
import { PublishTypeDialog } from '../PublishTypeDialog';
import { permitType } from './fixtures';

/** Діалоги життєвого циклу типу (PM-08c): публікація, нова версія, видалення. */

const k = (key: string): string => `⟦permits.editor.dialogs.${key}⟧`;
const kp = (key: string, params: Record<string, string | number>): string =>
  `⟦permits.editor.dialogs.${key} (${Object.entries(params)
    .map(([name, value]) => `${name}=${String(value)}`)
    .join(', ')})⟧`;

describe('PublishTypeDialog', () => {
  function mount(props: Partial<Parameters<typeof PublishTypeDialog>[0]> = {}) {
    const onSubmit = vi.fn();
    const onClose = vi.fn();
    const onAddTable = vi.fn();
    renderWithMantine(
      <PublishTypeDialog
        opened
        type={permitType()}
        onSubmit={onSubmit}
        onAddTable={onAddTable}
        onClose={onClose}
        {...props}
      />,
    );

    return { onSubmit, onClose, onAddTable };
  }

  it('заголовок називає тип і МАЙБУТНЮ версію (draftVersion)', () => {
    mount({ type: permitType({ status: 'Published', version: 2, draftVersion: 3 }) });
    expect(
      screen.getByText(kp('publish.title', { name: 'Water discharge', version: 3 })),
    ).toBeDefined();
  });

  it('кнопка вимкнена, доки причина коротша за 10 символів (без урахування пробілів)', async () => {
    const user = userEvent.setup();
    const { onSubmit } = mount();
    const confirm = screen.getByRole('button', { name: k('publish.verb') });

    await user.type(screen.getByLabelText(k('publish.reason')), '   short   ');
    expect((confirm as HTMLButtonElement).disabled).toBe(true);

    await user.clear(screen.getByLabelText(k('publish.reason')));
    await user.type(screen.getByLabelText(k('publish.reason')), '  Added the storage table  ');
    expect((confirm as HTMLButtonElement).disabled).toBe(false);

    await user.click(confirm);
    expect(onSubmit).toHaveBeenCalledWith('Added the storage table');
  });

  it('речення про дозволи: є лише коли кількість відома', () => {
    const { onClose } = mount({ permitCount: 12 });
    expect(screen.getByText(kp('publish.usedBy', { count: 12 }))).toBeDefined();
    expect(onClose).not.toHaveBeenCalled();
  });

  it('permitCount = 0 — «жоден дозвіл не використовує»; без числа — без речення', () => {
    const first = renderWithMantine(
      <PublishTypeDialog opened type={permitType()} permitCount={0} onSubmit={vi.fn()} onClose={vi.fn()} />,
    );
    expect(screen.getByText(k('publish.unused'))).toBeDefined();
    first.unmount();

    renderWithMantine(
      <PublishTypeDialog opened type={permitType()} onSubmit={vi.fn()} onClose={vi.fn()} />,
    );
    expect(screen.queryByText(k('publish.unused'))).toBeNull();
    expect(screen.queryByText(kp('publish.usedBy', { count: 0 }))).toBeNull();
  });

  it('тип без таблиць: діалог-відмова «Add a table before publishing» з дією «Add table…»', async () => {
    const user = userEvent.setup();
    const { onSubmit, onAddTable } = mount({ type: permitType({ tables: [] }) });

    expect(screen.getByText(k('publish.blockedTitle'))).toBeDefined();
    expect(screen.queryByLabelText(k('publish.reason'))).toBeNull();

    await user.click(screen.getByRole('button', { name: k('publish.addTable') }));
    expect(onAddTable).toHaveBeenCalledTimes(1);
    expect(onSubmit).not.toHaveBeenCalled();
  });

  it('Escape закриває без публікації', async () => {
    const { onSubmit, onClose } = mount();
    fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
    await waitFor(() => {
      expect(onClose).toHaveBeenCalled();
    });
    expect(onSubmit).not.toHaveBeenCalled();
  });
});

describe('NewVersionConfirm', () => {
  it('називає наступну версію, не небезпечна; фокус на скасуванні; підтвердження викликає onConfirm', async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    const onClose = vi.fn();
    renderWithMantine(
      <NewVersionConfirm
        opened
        type={permitType({ status: 'Published', version: 4, draftVersion: null, hasUnpublishedChanges: false })}
        onConfirm={onConfirm}
        onClose={onClose}
      />,
    );

    expect(screen.getByText(kp('newVersion.draft', { next: 5, published: 4 }))).toBeDefined();
    await waitFor(() => {
      expect(document.activeElement).toBe(screen.getByTestId('confirm-cancel'));
    });

    await user.click(screen.getByRole('button', { name: k('newVersion.verb') }));
    expect(onConfirm).toHaveBeenCalledTimes(1);

    await user.click(screen.getByTestId('confirm-cancel'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});

describe('DeleteTypeConfirm', () => {
  it('ніколи не опублікований тип видаляється: підтвердження доступне', async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    renderWithMantine(
      <DeleteTypeConfirm opened type={permitType()} onConfirm={onConfirm} onClose={vi.fn()} />,
    );

    expect(screen.getByText(kp('deleteType.removed', { tables: 1 }))).toBeDefined();
    await user.click(screen.getByRole('button', { name: k('deleteType.verb') }));
    expect(onConfirm).toHaveBeenCalledTimes(1);
  });

  it('опублікований тип: кнопка вимкнена й причина написана (сервер дав би 4094)', async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    renderWithMantine(
      <DeleteTypeConfirm
        opened
        type={permitType({ status: 'Published' })}
        onConfirm={onConfirm}
        onClose={vi.fn()}
      />,
    );

    expect(screen.getByText(k('deleteType.published'))).toBeDefined();
    const confirm = screen.getByRole('button', { name: k('deleteType.verb') });
    expect((confirm as HTMLButtonElement).disabled).toBe(true);
    await user.click(confirm);
    expect(onConfirm).not.toHaveBeenCalled();
  });
});
