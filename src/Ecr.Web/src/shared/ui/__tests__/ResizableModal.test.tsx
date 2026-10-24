import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider, TextInput } from '@mantine/core';
import { theme } from '@/shared/theme/theme';
import { withTestDefaults } from '@/test/render';
import { MinHeight, MinWidth, KeyStep, KeyStepLarge, storageKey } from '@/shared/ui/modalSize';
import { ResizableModal } from '@/shared/ui/ResizableModal';
import { useInResizableModal } from '@/shared/ui/resizableModalContext';

/**
 * Вікно формул і методологій, яке можна розгорнути чи потягнути за кут (P44).
 *
 * ⚠ Тема — справжня (`theme.ts`): саме з неї вікно бере `aria-label` хрестика й тривалість
 * переходу через `useProps('Modal')`; на голій темі цього не перевірити.
 *
 * ⚠ У jsdom немає розкладки: `getBoundingClientRect()` вікна дає нулі, тож перший крок стрілкою
 * відштовхується від мінімуму (`MinWidth × MinHeight`). Сама арифметика меж — у `modalSize.test.ts`.
 */

const Key = 'test.formula';

function GrowProbe(): JSX.Element {
  return <span data-testid="grow">{useInResizableModal() ? 'inside' : 'outside'}</span>;
}

function Host({ initiallyOpen = true }: { readonly initiallyOpen?: boolean }): JSX.Element {
  const [opened, setOpened] = useState(initiallyOpen);

  return (
    <MantineProvider theme={withTestDefaults(theme)}>
      <button type="button" onClick={() => setOpened(true)}>
        Open
      </button>
      <ResizableModal sizeKey={Key} opened={opened} onClose={() => setOpened(false)} title="Formula" size="lg">
        <TextInput label="Code" data-autofocus />
        <GrowProbe />
      </ResizableModal>
    </MantineProvider>
  );
}

function content(): HTMLElement {
  const dialog = screen.getByRole('dialog');
  const found = dialog.closest<HTMLElement>('[data-modal-content]') ?? dialog;
  return found;
}

function toggle(): HTMLElement {
  return screen.getByRole('button', { name: 'Full screen' });
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  try {
    globalThis.localStorage.clear();
  } catch {
    // Див. тест зі сховищем, що кидає.
  }
});

describe('ResizableModal: на весь екран', () => {
  it('кнопка перемикає повний екран, стан — в aria-pressed, назва діалогу не змінюється', async () => {
    render(<Host />);

    // ⛔ Кнопка НЕ в заголовку: доступне ім'я діалогу лишається назвою вікна.
    expect(await screen.findByRole('dialog', { name: 'Formula' })).toBeDefined();
    expect(toggle().getAttribute('aria-pressed')).toBe('false');
    expect(content().getAttribute('data-rm-size')).toBe('normal');

    fireEvent.click(toggle());
    expect(toggle().getAttribute('aria-pressed')).toBe('true');
    expect(content().getAttribute('data-rm-size')).toBe('full');
    expect(content().hasAttribute('data-full-screen')).toBe(true);
    // На весь екран тягнути кут нема чого.
    expect(screen.queryByRole('button', { name: 'Resize window' })).toBeNull();

    fireEvent.click(toggle());
    expect(toggle().getAttribute('aria-pressed')).toBe('false');
    expect(content().getAttribute('data-rm-size')).toBe('normal');
  });

  it('подвійний клік по заголовку робить те саме', async () => {
    render(<Host />);
    const title = await screen.findByRole('heading', { name: 'Formula' });

    fireEvent.doubleClick(title);
    expect(toggle().getAttribute('aria-pressed')).toBe('true');

    fireEvent.doubleClick(title);
    expect(toggle().getAttribute('aria-pressed')).toBe('false');
  });

  it('хрестик лишається з назвою з теми, Esc закриває вікно', async () => {
    render(<Host />);
    await screen.findByRole('dialog');

    expect(screen.getByRole('button', { name: 'Close' })).toBeDefined();

    fireEvent.keyDown(screen.getByLabelText('Code'), { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });

  it('вміст знає, що він у вікні зі змінним розміром (редактор виразу росте)', async () => {
    render(<Host />);
    expect((await screen.findByTestId('grow')).textContent).toBe('inside');
  });
});

describe('ResizableModal: ручка розміру з клавіатури', () => {
  it('стрілки змінюють розмір у межах, Shift — крупніше', async () => {
    render(<Host />);
    await screen.findByRole('dialog');
    const handle = screen.getByRole('button', { name: 'Resize window' });

    // Від нульового прямокутника jsdom — перший крок упирається в мінімум.
    fireEvent.keyDown(handle, { key: 'ArrowRight' });
    expect(content().getAttribute('data-rm-size')).toBe('custom');
    expect(content().getAttribute('data-rm-width')).toBe(String(MinWidth));
    expect(content().getAttribute('data-rm-height')).toBe(String(MinHeight));

    fireEvent.keyDown(handle, { key: 'ArrowRight' });
    expect(content().getAttribute('data-rm-width')).toBe(String(MinWidth + KeyStep));

    fireEvent.keyDown(handle, { key: 'ArrowDown', shiftKey: true });
    expect(content().getAttribute('data-rm-height')).toBe(String(MinHeight + KeyStepLarge));

    // Менше мінімуму не стискається.
    for (let i = 0; i < 10; i += 1) fireEvent.keyDown(handle, { key: 'ArrowLeft', shiftKey: true });
    expect(content().getAttribute('data-rm-width')).toBe(String(MinWidth));
  });
});

describe('ResizableModal: пам’ять розміру', () => {
  it('розмір переживає закриття і повторне відкриття; «Згорнути» його стирає', async () => {
    render(<Host />);
    await screen.findByRole('dialog');

    fireEvent.click(toggle());
    expect(globalThis.localStorage.getItem(storageKey(Key))).toBe('{"mode":"full"}');

    fireEvent.click(screen.getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());

    fireEvent.click(screen.getByRole('button', { name: 'Open' }));
    await screen.findByRole('dialog');
    expect(toggle().getAttribute('aria-pressed')).toBe('true');

    fireEvent.click(toggle());
    expect(globalThis.localStorage.getItem(storageKey(Key))).toBeNull();
  });

  it('потягнутий розмір відновлюється при відкритті', async () => {
    globalThis.localStorage.setItem(storageKey(Key), JSON.stringify({ mode: 'custom', width: 700, height: 500 }));
    render(<Host />);
    await screen.findByRole('dialog');

    expect(content().getAttribute('data-rm-size')).toBe('custom');
    expect(content().getAttribute('data-rm-width')).toBe('700');
    expect(content().style.height).toBe('500px');
  });

  it('сховище, що кидає: вікно працює, розмір просто не запам’ятовується', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('denied', 'SecurityError');
    });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('denied', 'SecurityError');
    });

    render(<Host />);
    await screen.findByRole('dialog');

    fireEvent.click(toggle());
    expect(toggle().getAttribute('aria-pressed')).toBe('true');

    fireEvent.keyDown(screen.getByLabelText('Code'), { key: 'Escape' });
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
  });
});
