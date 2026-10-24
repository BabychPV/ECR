import { useState, type JSX, type ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { Button, MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { RegistryDefDto, RegistryEntryDto } from '@/api/types';
import { testTheme } from '@/test/render';
import { LazyCreateRegistryModal, LazyDeleteEntryDialog, LazyRegistryEntryEditor } from '../lazyDialogs';

/**
 * WCAG 2.4.3 для лінивих діалогів сторінки довідників (`lazyDialogs.tsx`, D-132): натиснули РАНІШЕ, ніж приїхав чанк.
 *
 * ⛔ Сценарій: кнопка натиснута за мілісекунди після показу сторінки, `import()` ще в дорозі — діалог монтується ВЖЕ
 * відкритим. `returnFocus` Mantine запам'ятовує відкривача лише на переході `opened` false → true усередині змонтованої
 * `Modal`, тож після закриття фокус падав на `BODY`. Чанк тут тримає `vi.mock` із воротами: модуль діалогу віддається
 * лише після `open()`, тобто після кліку.
 *
 * ⚠ Кожен модуль вантажиться у файлі РІВНО раз (кеш модулів), тож на модуль — один тест «чанк запізнився».
 * `ValidityEditor` ділить модуль із `RegistryEntryEditor` і тому живе в `lazyValidityEditor.focus.test.tsx`.
 *
 * Мутаційний доказ: прибрати `useRestoreFocus(props.opened)` з `LazyCreateRegistryModal` — червоний перший тест;
 * з `LazyRegistryEntryEditor` — другий. Третій (`LazyDeleteEntryDialog`) — сторож: власної обгортки-хука там немає,
 * фокус повертає `useRestoreFocus(opened, returnFocusTo)` усередині `ConfirmModal` (прибрати його — третій червоний).
 *
 * ⚠ Каталог не вантажиться: `t()` дає `⟦ключ⟧`.
 */
const chunks = vi.hoisted(() => {
  const gate = (): { ready: Promise<void>; open: () => void } => {
    let release: () => void = () => undefined;
    const ready = new Promise<void>((resolve) => {
      release = resolve;
    });

    return { ready, open: () => release() };
  };

  return { create: gate(), entry: gate(), remove: gate() };
});

vi.mock('../CreateRegistryModal', async (importOriginal) => {
  await chunks.create.ready;

  return importOriginal();
});
vi.mock('../RegistryEntryEditor', async (importOriginal) => {
  await chunks.entry.ready;

  return importOriginal();
});
vi.mock('../DeleteEntryDialog', async (importOriginal) => {
  await chunks.remove.ready;

  return importOriginal();
});

const Registry: RegistryDefDto = {
  id: 11,
  code: 'CHEM',
  nameL10n: { values: { en: 'Chemicals' } },
  isHierarchical: false,
  isTemporal: true,
  sourceKind: 'Local',
  fields: [],
};

const Entry = {
  id: 42,
  code: 'KG',
  display: 'Kilogram',
  parentEntryId: null,
  validFrom: null,
  validTo: null,
} as unknown as RegistryEntryDto;

function wrap(node: ReactNode): JSX.Element {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () => new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } })),
  );

  return (
    <MantineProvider theme={testTheme}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        {node}
      </QueryClientProvider>
    </MantineProvider>
  );
}

/**
 * Клік по відкривачу, поки чанк у дорозі, → чанк приїхав → «Cancel» → фокус знову на відкривачі.
 * `opener` — уже знайдений і сфокусований елемент; `release` — ворота модуля.
 */
async function openLateThenCancel(opener: HTMLElement, release: () => void): Promise<void> {
  fireEvent.click(opener);
  // Діалогу ще немає: чанк не приїхав, а `opened` уже `true`.
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 0));
  });
  expect(screen.queryByRole('dialog')).toBeNull();

  release();
  const dialog = await screen.findByRole('dialog');
  await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));

  fireEvent.click(within(dialog).getByRole('button', { name: /common\.cancel/ }));
  await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  await waitFor(() => expect(document.activeElement).not.toBe(document.body));
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('ліниві діалоги довідників: фокус після закриття, коли чанк запізнився', () => {
  it('«New registry»: фокус повертається на кнопку', async () => {
    function Harness(): JSX.Element {
      const [opened, setOpened] = useState(false);

      return (
        <>
          <Button onClick={() => setOpened(true)}>New registry</Button>
          <LazyCreateRegistryModal opened={opened} onClose={() => setOpened(false)} onCreated={() => undefined} />
        </>
      );
    }
    render(wrap(<Harness />));
    const opener = screen.getByRole('button', { name: 'New registry' });
    opener.focus();

    await openLateThenCancel(opener, chunks.create.open);

    expect(document.activeElement).toBe(opener);
  });

  it('«New entry»: фокус повертається на кнопку', async () => {
    function Harness(): JSX.Element {
      const [opened, setOpened] = useState(false);

      return (
        <>
          <Button onClick={() => setOpened(true)}>New entry</Button>
          <LazyRegistryEntryEditor registry={Registry} entry={null} opened={opened} onClose={() => setOpened(false)} />
        </>
      );
    }
    render(wrap(<Harness />));
    const opener = screen.getByRole('button', { name: 'New entry' });
    opener.focus();

    await openLateThenCancel(opener, chunks.entry.open);

    expect(document.activeElement).toBe(opener);
  });

  it('«Delete» рядка: фокус повертається на кнопку (хук `ConfirmModal` бачить її, бо вона досі у фокусі)', async () => {
    function Harness(): JSX.Element {
      const [deleting, setDeleting] = useState<RegistryEntryDto | null>(null);

      return (
        <>
          <Button onClick={() => setDeleting(Entry)}>Delete</Button>
          <LazyDeleteEntryDialog
            entry={deleting}
            error={null}
            isPending={false}
            isTemporal
            onConfirm={() => undefined}
            onClose={() => setDeleting(null)}
            onValidity={() => undefined}
          />
        </>
      );
    }
    render(wrap(<Harness />));
    const opener = screen.getByRole('button', { name: 'Delete' });
    opener.focus();

    await openLateThenCancel(opener, chunks.remove.open);

    expect(document.activeElement).toBe(opener);
  });
});
