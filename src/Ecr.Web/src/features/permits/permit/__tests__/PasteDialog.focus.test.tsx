import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { renderWithQuery } from '@/test/render';
import { PasteDialog } from '../PasteDialog';
import { permit } from './permitFixtures';

/**
 * PA-U-03 (фікс-пак): «Paste from Excel…» монтується вже відкритим (`<Modal opened>`), тож власний `returnFocus`
 * Mantine нічого не запам'ятовує, і після Esc фокус падав на `BODY`.
 *
 * Мутаційний доказ (вручну, відновлено): прибрати `useRestoreFocus(true)` з `PasteDialog` — тест червоніє.
 */
function Host(): JSX.Element {
  const [open, setOpen] = useState(false);

  return (
    <>
      <button type="button" onClick={() => setOpen(true)}>
        Trigger
      </button>
      {open && (
        <PasteDialog
          permit={permit()}
          registryCode="limits-registry"
          tableName="Limits"
          fields={[]}
          fromFile={false}
          onClose={() => setOpen(false)}
        />
      )}
    </>
  );
}

afterEach(() => cleanup());

describe('PasteDialog', () => {
  it('Esc повертає фокус на кнопку-виклик, а не на BODY', async () => {
    renderWithQuery(<Host />);
    const trigger = screen.getByRole('button', { name: 'Trigger' });
    trigger.focus();
    fireEvent.click(trigger);
    const dialog = await screen.findByRole('dialog');
    await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));

    fireEvent.keyDown(dialog, { key: 'Escape' });

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(trigger));
  });
});
