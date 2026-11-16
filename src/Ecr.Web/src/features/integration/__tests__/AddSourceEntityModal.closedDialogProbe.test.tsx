import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { AddSourceEntityModal } from '@/features/integration/AddSourceEntityModal';
import { holdWrites, renderKeepMounted } from '@/test/closedDialogProbe';
import { gateReads, probeThreeWindows } from '@/test/stillOpenProbe';

/**
 * B149, група «скидання після успіху → кнопка вимкнена»: «Add collection entity», що закрився (ще в DOM), не
 * заводить сутність удруге.
 *
 * Зонд (справжній діалог, `keepMountedModalsTheme`, запис тримає тест): вікно 1 — `loading={create.isPending}`
 * (вимкнена); вікна 2 немає — `onSuccess` нічого не чекає (`void invalidateQueries`), `setChosen(null)` і
 * `onClose()` в одному оновленні; вікно 3 — `chosen === null` → `disabled`, обробник теж мовчить. ЗАКРИТО,
 * один `POST /api/v1/sources`.
 */

function Host(): JSX.Element {
  const [opened, setOpened] = useState(true);

  return <AddSourceEntityModal dataSourceId={3} sourceName="PI AF" opened={opened} onClose={() => setOpened(false)} />;
}

function read(url: string): unknown {
  if (url.endsWith('/api/v1/data-sources/3/catalog')) {
    return { items: [{ code: 'Boiler1', displayName: 'Boiler 1', path: '\\\\AF\\Plant\\Boiler1', kind: 'Element' }], nextCursor: null };
  }

  return {};
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: AddSourceEntityModal — один POST /sources на відкриття', () => {
  it('подвійний клік і закритий діалог — рівно один POST', async () => {
    const server = holdWrites({ read, write: () => ({ body: { id: 9, code: 'Boiler1' } }) });
    const gate = gateReads();
    renderKeepMounted(<Host />);

    const dialog = screen.getByRole('dialog');
    fireEvent.click(await within(dialog).findByRole('button', { name: '⟦sources.catalogChoose⟧' }));
    const save = within(dialog).getByRole('button', { name: '⟦sources.addEntity⟧' });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });

    await probeThreeWindows({
      server,
      gate,
      button: () => save,
      isOpen: () => screen.queryByRole('dialog') !== null,
      expected: ['POST /api/v1/sources'],
      awaitedRead: null,
    });
    expect(server.bodies[0]).toMatchObject({ dataSourceId: 3, code: 'Boiler1' });
  });
});
