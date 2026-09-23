import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RegistryDefDto, RegistryEntryDto } from '@/api/types';
import { RegistryEntryEditor } from '@/features/registries/RegistryEntryEditor';
import { testTheme } from '@/test/render';
import { t } from '@/shared/i18n';

/**
 * Живий дефект: після збереження нового запису довідника й ПОВТОРНОГО
 * відкриття «New entry» поля лишалися заповненими попереднім введенням, і
 * новий текст КОНКАТЕНУВАВСЯ зі старим (курсор у кінці наявного тексту в
 * `TextInput data-autofocus`) — наприклад стара назва "PHENOL" і новий ввід
 * "TOLUENE" давали в одному полі "PHENOLTOLUENE".
 *
 * ⛔ Причина: `loadedFor` для НОВОГО запису завжди `null` (`entry` завжди
 * `null`), і скид форми йшов лише по умові `loadedFor !== key`. Перше
 * відкриття «New entry» переводило `loadedFor` із початкового `undefined` у
 * `null` — скид спрацьовував один раз. `upsert.onSuccess` закривав модалку
 * голим `onClose()`, не чіпаючи `loadedFor`, тож він лишався `null`. Друге
 * відкриття «New entry» знову давало `key === null`, умова `loadedFor !==
 * key` була хибною — скид НЕ спрацьовував.
 *
 * Фікс: `handleClose` скидає `loadedFor` у `undefined` ДО делегування в
 * переданий `onClose`, тож наступне відкриття завжди бачить `undefined !==
 * key` — істину.
 */
const Registry: RegistryDefDto = {
  id: 11,
  code: 'CHEM',
  nameL10n: { values: { en: 'Chemicals' } },
  isHierarchical: false,
  isTemporal: false,
  sourceKind: 'Local',
  fields: [],
};

const Languages = [{ code: 'en', isDefault: true, nameNative: 'English' }];

function json(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  });
}

const afterEachRestorers: Array<() => void> = [];

/** Підміняє мережу: мови довідника й успішне збереження запису. */
function serveOk(): { postedBodies: () => Record<string, unknown>[] } {
  const postedBodies: Record<string, unknown>[] = [];
  const original = globalThis.fetch;

  globalThis.fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = new URL(String(input), 'http://x');

    if (url.pathname === '/api/v1/languages') {
      return json(Languages);
    }

    if (url.pathname === '/api/v1/registries/CHEM/entries' && init?.method === 'POST') {
      postedBodies.push(JSON.parse(String(init.body)) as Record<string, unknown>);
      return json({ id: postedBodies.length });
    }

    throw new Error(`Неочікуваний запит у тесті: ${String(input)}`);
  }) as typeof globalThis.fetch;

  afterEachRestorers.push(() => {
    globalThis.fetch = original;
  });

  return { postedBodies: () => postedBodies };
}

afterEach(() => {
  cleanup();
  for (const restore of afterEachRestorers.splice(0)) restore();
  vi.unstubAllGlobals();
});

/**
 * Той самий взірець керування, що й `RegistriesPage.tsx`: `entry` —
 * `undefined`, коли модалка закрита, і `null` для «New entry» (на відміну
 * від об'єкта запису — для правки). Сам `RegistryEntryEditor` лишається
 * змонтованим між відкриттями — так само, як на сторінці реєстрів — тому
 * саме тут і відтворюється дефект: `loadedFor` живе в тому самому екземплярі
 * компонента між закриттям і повторним відкриттям.
 */
function Harness(): JSX.Element {
  const [editing, setEditing] = useState<RegistryEntryDto | null | undefined>(undefined);

  return (
    <>
      <button type="button" onClick={() => setEditing(null)}>
        open-new-entry
      </button>
      <RegistryEntryEditor
        registry={Registry}
        entry={editing ?? null}
        opened={editing !== undefined}
        onClose={() => setEditing(undefined)}
      />
    </>
  );
}

function mount() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });

  return render(
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={client}>
        <Harness />
      </QueryClientProvider>
    </MantineProvider>,
  );
}

describe('RegistryEntryEditor: повторне відкриття "New entry" після збереження', () => {
  it('поля порожні, а не заповнені чи конкатеновані з попереднім записом', async () => {
    const net = serveOk();
    const user = userEvent.setup();
    mount();

    // Перше відкриття «New entry», введення і збереження.
    await user.click(screen.getByRole('button', { name: 'open-new-entry' }));
    const code = await screen.findByLabelText(t('registries.code'));
    const name = await screen.findByLabelText(`${t('registries.name')} · English`);

    await user.type(code, 'PHENOL');
    await user.type(name, 'Phenol');

    await user.click(screen.getByRole('button', { name: t('common.save') }));

    await waitFor(() => expect(net.postedBodies()).toHaveLength(1));
    expect(net.postedBodies()[0]?.code).toBe('PHENOL');

    // Модалка закрилася (upsert.onSuccess кличе handleClose → onClose).
    await waitFor(() => expect(screen.queryByLabelText(t('registries.code'))).toBeNull());

    // Друге відкриття «New entry» — той самий компонент, key знову null.
    await user.click(screen.getByRole('button', { name: 'open-new-entry' }));
    const codeAgain = await screen.findByLabelText(t('registries.code'));
    const nameAgain = await screen.findByLabelText(`${t('registries.name')} · English`);

    // ⛔ ЧЕРВОНИЙ до фіксу: поля містили б "PHENOL" / "Phenol" — старий текст
    // лишався б, і будь-який новий ввід дописувався б до нього, а не заміняв.
    expect((codeAgain as HTMLInputElement).value).toBe('');
    expect((nameAgain as HTMLInputElement).value).toBe('');

    // Новий ввід не конкатенується зі старим (яке саме прочитання дефекту
    // й було репортом: "PHENOL" + "TOLUENE" → "PHENOLTOLUENE").
    await user.type(codeAgain, 'TOLUENE');
    expect((codeAgain as HTMLInputElement).value).toBe('TOLUENE');
  });
});
