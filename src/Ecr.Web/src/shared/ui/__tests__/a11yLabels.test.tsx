import { describe, it, expect, vi, afterEach, beforeEach } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider, Modal, PasswordInput } from '@mantine/core';

/**
 * `X-26`: доступні імена службових кнопок ішли англійськими ЛІТЕРАЛАМИ в
 * обхід каталогу — російський і казахський інтерфейс озвучував «Close
 * notification» і «Toggle password visibility» англійською.
 *
 * ⚠ Каталог тут завантажується насправді: без нього відрізнити «з каталогу»
 * від «запасний літерал» неможливо.
 */

type Fresh = {
  loadCatalog: typeof import('@/shared/i18n').loadCatalog;
  theme: typeof import('@/shared/theme/theme').theme;
  labels: typeof import('@/shared/ui/a11yLabels');
};

/**
 * ⛔ Каталог i18n живе в стані модуля й не скидається: тест, що завантажив u,
 * ламав б «до каталогу» в іншому порядку (--sequence.shuffle). Тому кожен тест
 * бере СВІЖІ копії модулів після i.resetModules().
 */
let fresh: Fresh;

beforeEach(async () => {
  vi.resetModules();
  const [i18n, themeModule, labels] = await Promise.all([
    import('@/shared/i18n'),
    import('@/shared/theme/theme'),
    import('@/shared/ui/a11yLabels'),
  ]);
  fresh = { loadCatalog: i18n.loadCatalog, theme: themeModule.theme, labels };
});

function catalog(strings: Record<string, string>): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async () =>
      Promise.resolve(
        new Response(JSON.stringify({ languageCode: 'ru', revision: 2, strings }), {
          status: 200,
          headers: { 'Content-Type': 'application/json', ETag: '"public-ru-2"' },
        }),
      ),
    ),
  );
}

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('a11yLabels: доступні імена — з каталогу', () => {
  it('до каталогу — англійський запасний, а не ⟦ключ⟧', () => {
    expect(fresh.labels.closeNotificationLabel()).toBe('Close notification');
    expect(fresh.labels.undoLabel()).toBe('Undo');
    expect(fresh.labels.passwordToggleProps()).toEqual({ 'aria-label': 'Toggle password visibility', tabIndex: 0 });
  });

  it('з каталогом — текст каталогу, і геттер читає його в момент звернення', async () => {
    // Об'єкт узято ДО завантаження каталогу — так його імпортують тема й тости.
    const props = fresh.labels.closeNotificationButtonProps;

    catalog({
      'common.closeNotification': 'Закрыть уведомление',
      'common.togglePasswordVisibility': 'Показать или скрыть пароль',
      'common.undo': 'Отменить',
    });
    await fresh.loadCatalog('ru', 'public');

    // ⛔ Мутація «повернути літерал» (стара поведінка) дає тут англійську.
    expect({ ...props }).toEqual({ 'aria-label': 'Закрыть уведомление' });
    expect(fresh.labels.undoLabel()).toBe('Отменить');

    render(
      <MantineProvider>
        <PasswordInput label="Password" visibilityToggleButtonProps={fresh.labels.passwordToggleProps()} />
      </MantineProvider>,
    );

    expect(screen.getByRole('button', { name: 'Показать или скрыть пароль' })).toBeTruthy();
  });

  // P3-N6: хрестик модалки в ru - «Закрыть», а не англійський літерал теми. Мутація (вручну, відновлено): повернути
  // `closeButtonProps: { 'aria-label': 'Close' }` у `theme.ts` - тест червоний.
  it('хрестик модалки: до каталогу - Close, з каталогом ru - Закрыть', async () => {
    const open = (): void => {
      render(
        <MantineProvider theme={fresh.theme}>
          <Modal opened onClose={() => undefined} title="Title" transitionProps={{ duration: 0 }}>
            body
          </Modal>
        </MantineProvider>,
      );
    };
    open();
    expect(screen.getByRole('button', { name: 'Close' })).toBeTruthy();
    cleanup();

    catalog({ 'common.close': 'Закрыть' });
    await fresh.loadCatalog('ru', 'public');
    open();

    expect(screen.getByRole('button', { name: 'Закрыть' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Close' })).toBeNull();
  });
});
