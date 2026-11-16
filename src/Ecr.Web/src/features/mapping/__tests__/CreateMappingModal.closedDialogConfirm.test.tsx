import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { CreateMappingModal } from '@/features/mapping/CreateMappingModal';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';

/**
 * B145b: діалог «Create mapping», що вже закрився, не створює мапінг удруге.
 *
 * ⛔ Діра (зонд на базі d618ea1f6, справжній діалог): після відповіді сервера власник закриває діалог,
 * власний `Modal` ще ~200 мс у DOM, поля — з тими самими значеннями, кнопка активна. Другий клік
 * подвійного кліку слав другий `POST` створення з тим самим тілом — дубль мапінгу.
 *
 * Захист — у самому діалозі: кнопка діє лише у відкритому діалозі й не під час запиту.
 *
 * Мутаційний доказ: прибрати `!opened ||` у `CreateMappingModal.tsx` → червоний
 * (`expected 2 to be 1`).
 */

function Host({ onCreated }: { readonly onCreated: () => void }): JSX.Element {
  const [opened, setOpened] = useState(true);

  return <CreateMappingModal sourceEntityId={11} opened={opened} onClose={() => setOpened(false)} onCreated={onCreated} />;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B145b: Create mapping, що закрився (ще в DOM), не створює вдруге', () => {
  it('один POST; клік по кнопці закритого діалогу нічого не шле', async () => {
    const server = holdWrites({ write: () => ({ body: { id: 1 } }) });
    const onCreated = vi.fn();
    renderKeepMounted(<Host onCreated={onCreated} />);

    fireEvent.change(screen.getByLabelText('⟦mapping.createField⟧'), { target: { value: 'Unit1|Flow' } });
    fireEvent.change(screen.getByLabelText('⟦mapping.createTargetId⟧'), { target: { value: '42' } });
    const submit = screen.getByRole('button', { name: '⟦mapping.createSubmit⟧' });
    await waitFor(() => {
      expect(submit).toHaveProperty('disabled', false);
    });
    fireEvent.click(submit);
    await waitFor(() => {
      expect(server.sent).toHaveLength(1);
    });
    expect(server.sent[0]).toMatch(/^POST /);

    server.release();
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
      expect(submit).toHaveProperty('disabled', false);
    });

    // Клік по кнопці діалогу, що закрився: вона ще в DOM і не вимкнена.
    expect(submit.isConnected).toBe(true);
    fireEvent.click(submit);
    await settle();
    expect(server.sent.length).toBe(1);
    expect(onCreated).toHaveBeenCalledTimes(1);
  });
});
