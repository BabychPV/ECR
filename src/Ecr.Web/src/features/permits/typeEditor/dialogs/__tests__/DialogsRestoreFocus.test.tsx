import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { renderWithMantine } from '@/test/render';
import { AddColumnDialog } from '../AddColumnDialog';
import { AddRuleDialog } from '../AddRuleDialog';
import { PublishTypeDialog } from '../PublishTypeDialog';
import { permitType, units } from './fixtures';

/**
 * PA-U-03 (фікс-пак): прості `<Modal>` діалогів редактора типу повертають фокус на кнопку-виклик після Esc,
 * а не лишають його на `BODY`.
 *
 * Мутаційний доказ (вручну, відновлено): прибрати `useRestoreFocus(...)` з відповідного діалогу — його тест
 * червоніє (фокус лишається на `BODY`).
 */
function Host({ children }: { readonly children: (close: () => void) => JSX.Element }): JSX.Element {
  const [open, setOpen] = useState(false);

  return (
    <>
      <button type="button" onClick={() => setOpen(true)}>
        Trigger
      </button>
      {/* Як `?dialog=`: діалог існує в дереві лише відкритим (власний returnFocus Mantine тут нічого не запам'ятовує). */}
      {open && children(() => setOpen(false))}
    </>
  );
}

async function escapeReturnsFocus(title: string): Promise<void> {
  const trigger = screen.getByRole('button', { name: 'Trigger' });
  trigger.focus();
  fireEvent.click(trigger);
  const dialog = await screen.findByRole('dialog');
  expect(dialog.textContent).toContain(title);
  await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));

  fireEvent.keyDown(dialog, { key: 'Escape' });

  await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  await waitFor(() => expect(document.activeElement).toBe(trigger));
}

afterEach(() => cleanup());

describe('діалоги редактора типу: фокус після Esc', () => {
  it('AddColumnDialog', async () => {
    renderWithMantine(
      <Host>
        {(close) => (
          <AddColumnDialog opened tableCode="Limits" existingCodes={[]} units={units} onApply={() => undefined} onClose={close} />
        )}
      </Host>,
    );
    await escapeReturnsFocus('addColumn.title');
  });

  it('AddRuleDialog', async () => {
    renderWithMantine(
      <Host>
        {(close) => <AddRuleDialog opened type={permitType()} onApply={() => undefined} onClose={close} />}
      </Host>,
    );
    await escapeReturnsFocus('addRule.title');
  });

  it('PublishTypeDialog: відмова «без таблиць»', async () => {
    renderWithMantine(
      <Host>
        {(close) => <PublishTypeDialog opened type={permitType({ tables: [] })} onSubmit={() => undefined} onClose={close} />}
      </Host>,
    );
    await escapeReturnsFocus('publish.blockedTitle');
  });
});
