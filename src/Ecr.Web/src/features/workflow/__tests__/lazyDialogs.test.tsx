import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { LazyConfirmModal, LazyReasonModal } from '../lazyDialogs';

/**
 * Ліниві діалоги екрана документа (бюджет `D-132`): код діалогу не входить у
 * чанк сторінки, але після першого відкриття діалог малюється так само, як
 * статичний, і лишається змонтованим після закриття.
 */

function renderConfirm(opened: boolean) {
  const view = (open: boolean) => (
    <MantineProvider>
      <LazyConfirmModal opened={open} title="Підтвердити?" verb="Так" onConfirm={() => undefined} onClose={() => undefined} />
    </MantineProvider>
  );
  const utils = render(view(opened));

  return { ...utils, setOpened: (open: boolean) => utils.rerender(view(open)) };
}

describe('LazyConfirmModal', () => {
  it('не монтує діалог, доки його жодного разу не відкрили', () => {
    renderConfirm(false);

    expect(screen.queryByText('Підтвердити?')).toBeNull();
  });

  it('малює діалог після відкриття', async () => {
    const { setOpened } = renderConfirm(false);

    setOpened(true);

    expect(await screen.findByText('Підтвердити?')).toBeTruthy();
  });

  it('малює діалог, відкритий з першого кадру', async () => {
    renderConfirm(true);

    expect(await screen.findByText('Підтвердити?')).toBeTruthy();
  });
});

describe('LazyReasonModal', () => {
  it('малює діалог із полем причини після відкриття', async () => {
    const view = (open: boolean) => (
      <MantineProvider>
        <LazyReasonModal
          opened={open}
          title="Причина"
          label="Вкажіть причину"
          confirmLabel="Надіслати"
          isPending={false}
          onConfirm={() => undefined}
          onClose={() => undefined}
        />
      </MantineProvider>
    );
    const { rerender } = render(view(false));

    expect(screen.queryByText('Причина')).toBeNull();

    rerender(view(true));

    expect(await screen.findByText('Причина')).toBeTruthy();
    expect(screen.getByLabelText(/Вкажіть причину/)).toBeTruthy();
  });
});
