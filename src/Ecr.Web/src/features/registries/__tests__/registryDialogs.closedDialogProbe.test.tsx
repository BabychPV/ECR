import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, screen, waitFor } from '@testing-library/react';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefDto } from '@/api/types';
import { CreateRegistryModal } from '@/features/registries/CreateRegistryModal';
import { RegistryImportPanel } from '@/features/registries/RegistryImportPanel';
import { SourceKindSwitchDialog } from '@/features/registries/SourceKindSwitchDialog';
import { holdWrites, renderKeepMounted, settle } from '@/test/closedDialogProbe';
import { gateReads, probeThreeWindows } from '@/test/stillOpenProbe';

/**
 * B149, група «скидання після успіху → кнопка вимкнена»: діалоги довідників, що закрилися (ще в DOM) чи ще
 * чекають перечитування переліку, не шлють запит удруге.
 *
 * Зонд (справжній діалог, `keepMountedModalsTheme`, запис тримає тест, перечитування тримають ворота читань):
 * - CreateRegistryModal — вікно 2: `isPending` до кінця `onSuccess` → кнопка `loading` (вимкнена); вікно 3:
 *   код і назву скинуто → `disabled`. ЗАКРИТО, один `POST /registries`.
 * - SourceKindSwitchDialog — те саме: набір кодів і причину скинуто → `disabled`. ЗАКРИТО, один `PUT`.
 * - RegistryImportPanel «Apply» — тіло діалогу живе лише при `reviewed !== null`: після успіху звіт і файл
 *   скинуто, кнопка від'єднана від документа. ЗАКРИТО, один `POST …/entries/import` застосування.
 *
 * F13 (база 0c1ec8ad9): шлях «заповнено, закрито БЕЗ запису» для CreateRegistryModal і SourceKindSwitchDialog був
 * ВІДКРИТИЙ — закриття форму не скидає, кнопка закритого `Modal` слала `POST /registries` / `PUT …/source-kind`.
 * Захист в обробнику: `if (!opened || x.isPending) return`. Мутації: без `!opened ||` — червоний відповідний тест
 * «закрито без запису»; `disabled={… && false}` разом зі зняттям захисту — червоний і зонд «три вікна» (вікно 3).
 *
 * Перелік довідників змонтовано в господарі навмисно: без активного запиту `invalidateQueries` нічого не
 * чекає, і вікна 2 у тесті не було б, а на сторінці воно є.
 */

function ListHost({ queryKey }: { queryKey: readonly unknown[] }): null {
  useQuery({ queryKey, queryFn: () => apiFetch<unknown>('/api/v1/registries') });
  return null;
}

function CreateHost(): JSX.Element {
  const [opened, setOpened] = useState(true);

  return (
    <>
      <ListHost queryKey={queryKeys.registries.list()} />
      <CreateRegistryModal opened={opened} onClose={() => setOpened(false)} onCreated={() => undefined} />
    </>
  );
}

const registries = [
  { code: 'FUEL', nameL10n: { values: { en: 'Fuel' } }, sourceKind: 'Local' },
] as unknown as RegistryDefDto[];

function SwitchHost(): JSX.Element {
  const [opened, setOpened] = useState(true);

  return (
    <>
      <ListHost queryKey={queryKeys.registries.list()} />
      <SourceKindSwitchDialog registries={registries} opened={opened} onClose={() => setOpened(false)} />
    </>
  );
}

function ImportHost(): JSX.Element {
  return (
    <>
      <ListHost queryKey={queryKeys.registries.entries('FUEL')} />
      <RegistryImportPanel registryCode="FUEL" />
    </>
  );
}

const isOpen = (): boolean => screen.queryByRole('dialog') !== null;

/** Закрити діалог Escape-ом (без запису); `Modal` лишається в DOM (`keepMounted`). */
async function closeByEscape(): Promise<void> {
  fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
  await waitFor(() => {
    expect(isOpen()).toBe(false);
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('B149: діалоги довідників — один запит на відкриття', () => {
  it('CreateRegistryModal: один POST /registries у всіх трьох вікнах', async () => {
    const server = holdWrites({ write: () => ({ body: { code: 'FUEL' } }) });
    const gate = gateReads();
    renderKeepMounted(<CreateHost />);

    fireEvent.change(screen.getByLabelText(/registries\.code/), { target: { value: 'FUEL' } });
    fireEvent.change(screen.getByLabelText(/registries\.name/), { target: { value: 'Fuel' } });
    const save = screen.getByRole('button', { name: '⟦common.save⟧' });

    await probeThreeWindows({
      server,
      gate,
      button: () => save,
      isOpen,
      expected: ['POST /api/v1/registries'],
      awaitedRead: '/api/v1/registries',
    });
  });

  it('SourceKindSwitchDialog: один PUT /registries/source-kind у всіх трьох вікнах', async () => {
    const server = holdWrites({ write: () => ({ body: { affectedRows: 1 } }) });
    const gate = gateReads();
    renderKeepMounted(<SwitchHost />);

    fireEvent.click(screen.getByRole('checkbox', { name: /FUEL/ }));
    fireEvent.change(screen.getByLabelText(/workflow\.reason/), { target: { value: 'перехід на AF' } });
    const confirm = screen.getAllByRole('button', { name: '⟦registries.sourceSwitch⟧' }).at(-1);
    if (confirm === undefined) throw new Error('немає кнопки перемикання');

    await probeThreeWindows({
      server,
      gate,
      button: () => confirm,
      isOpen,
      expected: ['PUT /api/v1/registries/source-kind'],
      awaitedRead: '/api/v1/registries',
    });
    expect(server.bodies[0]).toEqual({ registryCodes: ['FUEL'], sourceKind: 'Local', reason: 'перехід на AF' });
  });

  it('CreateRegistryModal: форму заповнено, вікно закрито без запису — кнопка закритого вікна нічого не шле', async () => {
    const server = holdWrites({ write: () => ({ body: { code: 'FUEL' } }) });
    renderKeepMounted(<CreateHost />);

    fireEvent.change(screen.getByLabelText(/registries\.code/), { target: { value: 'FUEL' } });
    fireEvent.change(screen.getByLabelText(/registries\.name/), { target: { value: 'Fuel' } });
    const save = screen.getByRole('button', { name: '⟦common.save⟧' });
    await waitFor(() => {
      expect(save).toHaveProperty('disabled', false);
    });

    await closeByEscape();
    expect(save.isConnected).toBe(true);
    fireEvent.click(save);
    await settle();
    expect(server.sent).toEqual([]);
  });

  it('SourceKindSwitchDialog: вибрано й пояснено, вікно закрито без запису — кнопка закритого вікна нічого не шле', async () => {
    const server = holdWrites({ write: () => ({ body: { affectedRows: 1 } }) });
    renderKeepMounted(<SwitchHost />);

    fireEvent.click(screen.getByRole('checkbox', { name: /FUEL/ }));
    fireEvent.change(screen.getByLabelText(/workflow\.reason/), { target: { value: 'перехід на AF' } });
    const confirm = screen.getAllByRole('button', { name: '⟦registries.sourceSwitch⟧' }).at(-1);
    if (confirm === undefined) throw new Error('немає кнопки перемикання');
    await waitFor(() => {
      expect(confirm).toHaveProperty('disabled', false);
    });

    await closeByEscape();
    expect(confirm.isConnected).toBe(true);
    fireEvent.click(confirm);
    await settle();
    expect(server.sent).toEqual([]);
  });

  it('RegistryImportPanel «Apply»: один POST застосування; після закриття кнопка від\'єднана', async () => {
    const report = { added: 1, updated: 0, unchanged: 0, errors: [] };
    const server = holdWrites({
      write: (_method, _url, index) => ({ body: { ...report, applied: index > 0 } }),
    });
    const gate = gateReads();
    const view = renderKeepMounted(<ImportHost />);

    const picker = view.container.querySelector('input[type="file"]');
    if (picker === null) throw new Error('немає поля файлу');
    fireEvent.change(picker, { target: { files: [new File(['code\nA'], 'fuel.csv', { type: 'text/csv' })] } });

    // Перегляд (dryRun) — теж запис; відповідь віддаємо одразу.
    await waitFor(() => {
      expect(server.sent).toHaveLength(1);
    });
    server.release();
    const apply = await screen.findByRole('button', { name: '⟦registry.import.apply⟧' });

    // Перегляд + рівно одне застосування. Вікно 2 є: після `applied` панель чекає перечитування записів.
    const importUrl = 'POST /api/v1/registries/FUEL/entries/import';
    await probeThreeWindows({
      server,
      gate,
      button: () => apply,
      isOpen,
      expected: [importUrl, importUrl],
      awaitedRead: '/api/v1/registries',
      afterClose: 'detached',
    });
  });
});
