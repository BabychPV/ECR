import { describe, it, expect, vi, afterEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MantineProvider, PasswordInput } from '@mantine/core';
import { loadCatalog } from '@/shared/i18n';
import {
  closeNotificationButtonProps,
  closeNotificationLabel,
  passwordToggleProps,
  undoLabel,
} from '@/shared/ui/a11yLabels';

/**
 * `X-26`: доступні імена службових кнопок ішли англійськими ЛІТЕРАЛАМИ в
 * обхід каталогу — російський і казахський інтерфейс озвучував «Close
 * notification» і «Toggle password visibility» англійською.
 *
 * ⚠ Каталог тут завантажується насправді: без нього відрізнити «з каталогу»
 * від «запасний літерал» неможливо.
 */

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
    expect(closeNotificationLabel()).toBe('Close notification');
    expect(undoLabel()).toBe('Undo');
    expect(passwordToggleProps()).toEqual({ 'aria-label': 'Toggle password visibility', tabIndex: 0 });
  });

  it('з каталогом — текст каталогу, і геттер читає його в момент звернення', async () => {
    // Об'єкт узято ДО завантаження каталогу — так його імпортують тема й тости.
    const props = closeNotificationButtonProps;

    catalog({
      'common.closeNotification': 'Закрыть уведомление',
      'common.togglePasswordVisibility': 'Показать или скрыть пароль',
      'common.undo': 'Отменить',
    });
    await loadCatalog('ru', 'public');

    // ⛔ Мутація «повернути літерал» (стара поведінка) дає тут англійську.
    expect({ ...props }).toEqual({ 'aria-label': 'Закрыть уведомление' });
    expect(undoLabel()).toBe('Отменить');

    render(
      <MantineProvider>
        <PasswordInput label="Password" visibilityToggleButtonProps={passwordToggleProps()} />
      </MantineProvider>,
    );

    expect(screen.getByRole('button', { name: 'Показать или скрыть пароль' })).toBeTruthy();
  });
});
