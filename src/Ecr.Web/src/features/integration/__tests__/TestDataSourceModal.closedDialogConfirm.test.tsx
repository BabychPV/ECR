import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { TestDataSourceModal } from '../TestDataSourceModal';

/**
 * B149: «Test connection» — одна проба на натиск і жодної з закритого діалогу.
 *
 * Зонд (база 826f654e4):
 * - подвійний клік — ЗАКРИТО: `loading={test.isPending}` вимикає кнопку з наступним рендером, другий клік (наступний тік)
 *   нічого не шле (мутація «без `test.isPending` в обробнику» лишає тест зеленим — тримає `loading`; перевірка в
 *   обробнику — другий шар);
 * - закритий діалог — ВІДКРИТО: власник (`DataSourceDrawer`) тримає діалог змонтованим (`opened={testing}`), причина
 *   після закриття лишається в полі, кнопка активна — клік по кнопці закритого `Modal` слав
 *   `POST /api/v1/data-sources/7/test` (проба в чужу систему й запис у журнал безпеки) уже після закриття.
 *
 * Мутаційний доказ: прибрати `!opened ||` у `TestDataSourceModal.tsx` → червоний другий тест
 * (`expected [ 'POST /api/v1/data-sources/7/test' ] to deeply equal []`).
 */

const Probe = 'POST /api/v1/data-sources/7/test';

function Host(): JSX.Element {
  const [opened, setOpened] = useState(true);

  return (
    <>
      <TestDataSourceModal opened={opened} sourceId={7} sourceName="PI Atyrau" onClose={() => setOpened(false)} />
      <button type="button" onClick={() => setOpened(false)}>
        close
      </button>
    </>
  );
}

/** Наступний тік: `isPending` мутації вже дійшов до рендера (так приходить другий клік подвійного кліку). */
async function nextTick(): Promise<void> {
  await act(async () => {
    await new Promise<void>((resolve) => setTimeout(resolve, 0));
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: TestDataSourceModal — одна проба на натиск', () => {
  it('подвійний клік шле одну пробу', async () => {
    const server = holdWrites({ write: () => ({ body: { ok: true, durationMs: 12, message: null } }) });
    renderKeepMounted(<Host />);
    fireEvent.change(screen.getByRole('textbox', { name: /sources\.testReason/ }), { target: { value: 'Check after password rotation' } });
    const run = screen.getByRole('button', { name: /sources\.testConnection/ });

    fireEvent.click(run);
    await waitFor(() => {
      expect(server.sent).toEqual([Probe]);
    });
    await nextTick();
    fireEvent.click(run);
    await settle();
    expect(server.sent).toEqual([Probe]);
  });

  it('кнопка закритого діалогу (ще в DOM) нічого не шле', async () => {
    const server = holdWrites();
    renderKeepMounted(<Host />);
    fireEvent.change(screen.getByRole('textbox', { name: /sources\.testReason/ }), { target: { value: 'Check after password rotation' } });
    const run = screen.getByRole('button', { name: /sources\.testConnection/ });

    fireEvent.click(screen.getByRole('button', { name: 'close' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog')).toBeNull();
    });

    expect(run.isConnected).toBe(true);
    expect(run).toHaveProperty('disabled', false);
    fireEvent.click(run);
    await settle();
    expect(server.sent).toEqual([]);
  });
});
