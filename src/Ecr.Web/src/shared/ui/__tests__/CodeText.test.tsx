import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { theme } from '@/shared/theme/theme';
import { CodeText } from '@/shared/ui/CodeText';
import { showApiError, showDone } from '@/shared/ui/notify';

/**
 * `UI-04`: код моноширинним; блоком — із копіюванням.
 *
 * ⚠ Тости підмінені, бо предмет перевірки — РІШЕННЯ компонента (вдалося /
 * не вдалося), а не те, як Mantine малює сповіщення.
 */
vi.mock('@/shared/ui/notify', () => ({
  showDone: vi.fn(),
  showApiError: vi.fn(),
}));

/**
 * ⚠ Вміст монтується у власний вузол-пробу: `MantineProvider` кладе поруч
 * `<style>` із усією темою, і `container.textContent` після цього містить
 * двадцять кілобайтів CSS, а не текст компонента.
 */
function show(node: ReactNode): HTMLElement {
  const { container } = render(
    <MantineProvider theme={theme}>
      <div data-probe="">{node}</div>
    </MantineProvider>,
  );

  const probe = container.querySelector<HTMLElement>('[data-probe]');
  if (probe === null) throw new Error('пробний вузол не змонтувався');

  return probe;
}

/**
 * Клік по кнопці.
 *
 * ⛔ `fireEvent`, а не `userEvent.setup()`: останній ПІДМІНЯЄ
 * `navigator.clipboard` власною заглушкою при налаштуванні
 * (`attachClipboardStubToView`), тобто затер би рівно те, чим цей файл
 * керує, — і перевірка «буфера немає» перевіряла б заглушку бібліотеки.
 */
function click(name: string): void {
  fireEvent.click(screen.getByRole('button', { name }));
}

/** Ставить (або прибирає) буфер обміну — у jsdom його немає зовсім. */
function setClipboard(value: Clipboard | undefined): void {
  Object.defineProperty(navigator, 'clipboard', {
    configurable: true,
    writable: true,
    value,
  });
}

beforeEach(() => {
  vi.mocked(showDone).mockClear();
  vi.mocked(showApiError).mockClear();
});

afterEach(() => {
  cleanup();
  setClipboard(undefined);
});

describe('CodeText малює код', () => {
  it('рядковий варіант — <code>, без кнопки', () => {
    const container = show(<CodeText>Document.Approve</CodeText>);

    expect(screen.getByText('Document.Approve').tagName.toLowerCase()).toBe('code');
    expect(container.querySelector('button')).toBeNull();
  });

  it('блоковий варіант — <pre> і кнопка копіювання з доступним іменем', () => {
    const container = show(<CodeText block>SUM(B2:B12)</CodeText>);

    expect(container.querySelector('pre')).not.toBeNull();
    expect(screen.getByRole('button', { name: 'Copy' })).toBeDefined();
  });

  it('підпис кнопки — проп: рядків у каталозі під нього немає', () => {
    show(
      <CodeText block copyLabel="Копіювати">
        SUM(B2:B12)
      </CodeText>,
    );

    expect(screen.getByRole('button', { name: 'Копіювати' })).toBeDefined();
  });
});

describe('D15-06: порожній код не малюється', () => {
  it.each(['', '   ', '\n\t'])('код «%s» не лишає ні рамки, ні кнопки', (value) => {
    const container = show(<CodeText block>{value}</CodeText>);

    expect(container.textContent).toBe('');
    expect(container.querySelector('code')).toBeNull();
    expect(container.querySelector('button')).toBeNull();
  });
});

describe('копіювання', () => {
  it('кладе в буфер саме текст коду і підтверджує тостом', async () => {
    const writeText = vi.fn<(text: string) => Promise<void>>().mockResolvedValue(undefined);
    setClipboard({ writeText } as unknown as Clipboard);

    show(<CodeText block>SUM(B2:B12)</CodeText>);

    click('Copy');

    await waitFor(() => {
      expect(writeText).toHaveBeenCalledWith('SUM(B2:B12)');
    });
    expect(vi.mocked(showDone)).toHaveBeenCalledTimes(1);
    expect(vi.mocked(showApiError)).not.toHaveBeenCalled();
  });

  /*
   * ⛔ Це головний випадок, а не додатковий. `navigator.clipboard` відсутній
   * у незахищеному контексті (`http://` не на `localhost`) — тобто саме там,
   * де застосунок живе в корпоративній мережі. Без `try`/`catch` звернення до
   * `undefined.writeText` кидає з обробника події, і React 19 гасить ЕКРАН
   * разом із незбереженими правками.
   *
   * ⚠ Буфер тут не «мок, що відхиляє», а ВІДСУТНІЙ — саме так виглядає
   * справжня відмова. Заглушка з `mockRejectedValue` перевіряла б інший шлях:
   * вона сама ловить відхилення і зеленіє незалежно від `catch` у компоненті.
   */
  it('буфера немає — сторінка ЖИВА, причина названа тостом', async () => {
    setClipboard(undefined);

    show(<CodeText block>SUM(B2:B12)</CodeText>);

    click('Copy');

    await waitFor(() => {
      expect(vi.mocked(showApiError)).toHaveBeenCalledTimes(1);
    });

    expect(vi.mocked(showDone)).not.toHaveBeenCalled();

    // Сторінка на місці: код і кнопка нікуди не зникли.
    expect(screen.getByText('SUM(B2:B12)')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Copy' })).toBeDefined();
  });

  it('буфер відмовив правами — та сама гілка, причина від браузера', async () => {
    const writeText = vi
      .fn<(text: string) => Promise<void>>()
      .mockRejectedValue(new Error('Write permission denied'));
    setClipboard({ writeText } as unknown as Clipboard);

    show(<CodeText block>SUM(B2:B12)</CodeText>);

    click('Copy');

    await waitFor(() => {
      expect(vi.mocked(showApiError)).toHaveBeenCalledTimes(1);
    });

    // ⚠ Саме текст браузера, а не «не вдалося»: `ФВ-14.24`.
    expect(vi.mocked(showApiError).mock.calls[0]?.[0]).toBeInstanceOf(Error);
    expect(vi.mocked(showDone)).not.toHaveBeenCalled();
  });
});
