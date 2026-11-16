import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { AirRowEditor, type AirEditTarget } from '../AirRowEditor';

/**
 * B149: форма правки рядка «Sources & parameters» — один пакетний запис на збереження.
 *
 * Зонд (база 826f654e4):
 * - закриття — ЗАКРИТО: власник (`AirLimitsTab`) монтує форму лише за `{editing !== null && …}`, сама форма —
 *   `<Modal opened>` без закритого стану; після успіху форма від'єднана;
 * - подвійний сабміт — ВІДКРИТО: `submit` не дивився на `isPending`, спінер — лише через 100 мс, а `onSettled` хука ще
 *   й чекає перечитування картки: другий сабміт (наступний тік) слав другий `POST …/batch` з тим самим рядком і
 *   версією (сервер відмовив би `entryChanged` — після власного успішного запису).
 *
 * Мутаційний доказ: прибрати `if (save.isPending) return;` у `AirRowEditor.tsx` → червоний
 * (`expected [ …(2) ] to deeply equal [ Array(1) ]`).
 */

const Target: AirEditTarget = {
  title: 'Boiler 1',
  registryCode: 'AIR_SOURCES',
  id: 17,
  version: 'v17',
  fields: [{ code: 'Name', label: 'Name', kind: 'text', added: false }],
  initial: { Name: 'Boiler 1' },
};

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: AirRowEditor — один запис на збереження', () => {
  it('подвійний сабміт шле один запит; після успіху форму знято з дерева', async () => {
    const server = holdWrites({ write: () => ({ body: { applied: true, rows: [] } }) });

    function Host(): JSX.Element | null {
      const [open, setOpen] = useState(true);

      return open ? <AirRowEditor target={Target} permitId={4} onClose={() => setOpen(false)} /> : null;
    }

    renderKeepMounted(<Host />);
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Boiler 1A' } });
    const form = screen.getByRole('dialog').querySelector('form');
    if (form === null) throw new Error('форми немає');

    fireEvent.submit(form);
    await waitFor(() => {
      expect(server.sent).toHaveLength(1);
    });
    const first = server.sent[0];
    await nextTick();
    fireEvent.submit(form);
    await settle();
    expect(server.sent).toEqual([first]);

    server.release();
    await waitFor(() => {
      expect(form.isConnected).toBe(false);
    });
    expect(server.sent).toEqual([first]);
    expect(first).toMatch(/^POST \/api\/v1\/registries\/AIR_SOURCES\//);
  });
});
