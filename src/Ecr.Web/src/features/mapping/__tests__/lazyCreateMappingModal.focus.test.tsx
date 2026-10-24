import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { Button, MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { testTheme } from '@/test/render';
import { LazyCreateMappingModal } from '@/features/mapping/lazyCreateMappingModal';

/**
 * WCAG 2.4.3: «Add mapping» (`lazyCreateMappingModal.tsx`, D-132) — натиснули РАНІШЕ, ніж приїхав чанк форми.
 *
 * ⛔ Діалог монтується ВЖЕ відкритим, `returnFocus` Mantine переходу `opened` false → true не бачить, і без
 * `useRestoreFocus` обгортки після закриття фокус падав би на `BODY`.
 *
 * ⚠ Ворота — `use(promise)` у підміненому компоненті, а НЕ `await` у фабриці `vi.mock`, і клік — лише після `loaded`
 * (той самий прийом, що `VersionDiffButton.lateChunk.test.tsx`): обгортка змонтована завжди й вантажить чанк одразу,
 * а другий `import()` того самого модуля, поки async-фабрика ще працює, vitest віддавав би справжнім модулем.
 *
 * Мутаційний доказ: прибрати `useRestoreFocus(props.opened)` з `LazyCreateMappingModal` — тест червоний.
 *
 * ⚠ Каталог не вантажиться: `t()` дає `⟦ключ⟧`.
 */
const chunk = vi.hoisted(() => {
  let release: () => void = () => undefined;
  const ready = new Promise<void>((resolve) => {
    release = resolve;
  });

  return { ready, open: () => release(), loaded: false as boolean };
});

vi.mock('@/features/mapping/CreateMappingModal', async (importOriginal) => {
  const { createElement, use } = await import('react');
  const actual = await importOriginal<typeof import('@/features/mapping/CreateMappingModal')>();
  function LateModal(props: Parameters<typeof actual.CreateMappingModal>[0]): ReturnType<typeof actual.CreateMappingModal> {
    use(chunk.ready);

    return createElement(actual.CreateMappingModal, props);
  }

  chunk.loaded = true;

  return { ...actual, CreateMappingModal: LateModal };
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('«Add mapping»: фокус після закриття, коли чанк запізнився', () => {
  it('клік → чанк приїхав → Esc → фокус знову на кнопці', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response('[]', { status: 200, headers: { 'Content-Type': 'application/json' } })),
    );

    function Harness(): JSX.Element {
      const [opened, setOpened] = useState(false);

      return (
        <>
          <Button onClick={() => setOpened(true)}>Add mapping</Button>
          <LazyCreateMappingModal
            sourceEntityId={5}
            opened={opened}
            onClose={() => setOpened(false)}
            onCreated={() => undefined}
          />
        </>
      );
    }

    render(
      <MantineProvider theme={testTheme}>
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <Harness />
        </QueryClientProvider>
      </MantineProvider>,
    );

    await waitFor(() => expect(chunk.loaded).toBe(true));
    // Ще одна макрозадача — щоб vitest устиг покласти доготований модуль у реєстр.
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });

    const opener = screen.getByRole('button', { name: 'Add mapping' });
    opener.focus();
    fireEvent.click(opener);
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    // Діалогу ще немає: чанк «у дорозі», а `opened` уже `true`.
    expect(screen.queryByRole('dialog')).toBeNull();

    chunk.open();
    const dialog = await screen.findByRole('dialog');
    await waitFor(() => expect(dialog.contains(document.activeElement)).toBe(true));

    // У формі немає «Cancel» — закриваємо Esc (Mantine `Modal`, `closeOnEscape`).
    fireEvent.keyDown(dialog, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(opener));
  });
});
