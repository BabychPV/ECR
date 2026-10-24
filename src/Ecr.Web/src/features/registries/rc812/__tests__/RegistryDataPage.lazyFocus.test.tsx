import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { loadCatalog, t } from '@/shared/i18n';
import { mockServer, showDataPage } from './fixtures';

/**
 * WCAG 2.4.3: шторка запису довідника (`EntryDrawer`, лінивий чанк, D-132) — «⋯» рядка натиснули РАНІШЕ, ніж приїхав
 * чанк.
 *
 * ⛔ Шторка монтується ВЖЕ відкритою (`?panel=entry-…`), і `returnFocus` Mantine відкривача не бачить. Повертає фокус
 * `DetailDrawer`: відкривача він читає в рендері на першому відкритому кадрі, тобто коли чанк приїхав, — кнопка «⋯» на
 * той час досі у фокусі. Чанк тут тримає `vi.mock` із воротами — шторка монтується лише після кліку.
 *
 * ⚠ Ворота — `use(promise)` у підміненому компоненті, а НЕ `await` у фабриці `vi.mock`: код сам передзавантажує чанк
 * (до кліку), і другий `import()` того самого модуля, поки фабрика ще чекає, vitest віддавав справжнім модулем — діалог
 * з'являвся одразу (CI 40107107198). Для React це той самий стан, що й чанк у дорозі: межа `Suspense` показує
 * `fallback={null}`, компонент монтується вже відкритим лише після `open()`.
 *
 * Мутаційний доказ: прибрати `if (opened) setOpener(focusedOutside(panelId));` у `DetailDrawer` — тест червоний.
 */
const chunk = vi.hoisted(() => {
  let release: () => void = () => undefined;
  const ready = new Promise<void>((resolve) => {
    release = resolve;
  });

  return { ready, open: () => release() };
});

vi.mock('@/features/registries/rc812/EntryDrawer', async (importOriginal) => {
  const { createElement, use } = await import('react');
  const actual = await importOriginal<typeof import('@/features/registries/rc812/EntryDrawer')>();
  function LateDrawer(props: Parameters<typeof actual.EntryDrawer>[0]): ReturnType<typeof actual.EntryDrawer> {
    use(chunk.ready);

    return createElement(actual.EntryDrawer, props);
  }

  return { ...actual, EntryDrawer: LateDrawer };
});

beforeEach(async () => {
  mockServer();
  await loadCatalog('en', 'private');
  await loadCatalog('en', 'public');
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('Дані довідника: фокус після закриття шторки запису, коли чанк запізнився', () => {
  it('«⋯» рядка → чанк приїхав → Esc → фокус знову на «⋯»', async () => {
    showDataPage();
    await screen.findByRole('grid', { name: 'Stream cases' });
    const label = `${t('registries.data.openEntry')}: `;
    const [opener] = screen.getAllByRole('button', { name: (name) => name.startsWith(label) });
    if (opener === undefined) throw new Error('no «open entry» button');
    act(() => opener.focus());

    fireEvent.click(opener);
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    // Шторки ще немає: чанк не приїхав, а `?panel=entry-…` уже в адресі.
    expect(screen.queryByRole('dialog')).toBeNull();

    chunk.open();
    const drawer = await screen.findByRole('dialog');
    await waitFor(() => expect(drawer.contains(document.activeElement)).toBe(true));

    fireEvent.keyDown(drawer, { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(opener));
  });
});
