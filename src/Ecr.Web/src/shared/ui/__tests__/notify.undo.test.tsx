import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, render, screen, cleanup } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { showUndo } from '@/shared/ui/notify';

/**
 * Тост із дією «назад» (директива №15, §2, Шар 2: `notify.undo(message,
 * onUndo, ms = 8000)`; правило `L7`).
 *
 * ⛔ Що саме тут доводиться і чому обома боками:
 *   • натиснули «назад» → `onUndo` викликано, обіцянка `true`;
 *   • вікно збігло → `onUndo` НЕ викликано, обіцянка `false`.
 * Половина цього доказу без другої нічого не варта: компонент, який кличе
 * `onUndo` завжди, і компонент, який не кличе його ніколи, кожен пройшов би
 * рівно одну половину.
 *
 * ⚠ Обидва механізми з директиви («після спливу таймера» і «одразу з
 * компенсацією») тримаються на ОДНОМУ факті — на тому, ЩО повертає обіцянка;
 * тому вона тут і перевіряється значенням, а не фактом розв'язання.
 */
function mount(): void {
  render(
    <MantineProvider>
      <Notifications />
    </MantineProvider>,
  );
}

beforeEach(() => {
  notifications.cleanQueue();
  notifications.clean();
});

afterEach(() => {
  vi.useRealTimers();
  cleanup();
});

describe('showUndo: натиснута дія «назад»', () => {
  it('кличе onUndo рівно раз і розв’язує обіцянку значенням true', async () => {
    const onUndo = vi.fn();
    mount();

    let result: boolean | undefined;

    await act(async () => {
      // ⚠ Вікно навмисно довге: цей тест — про НАТИСКАННЯ, і таймер не має
      // права втрутитися в його результат.
      void showUndo('Аркуш видалено.', onUndo, 100_000).then((value) => {
        result = value;
      });
    });

    expect(screen.getByText('Аркуш видалено.')).toBeDefined();

    await act(async () => {
      screen.getByRole('button', { name: 'Undo' }).click();
    });

    expect(onUndo).toHaveBeenCalledOnce();
    expect(result).toBe(true);

    // Тост після скасування зникає: вікно рішення закрите.
    expect(screen.queryByText('Аркуш видалено.')).toBeNull();
  });

  it('кнопка «назад» має доступне ім’я, як і хрестик поруч', async () => {
    mount();

    await act(async () => {
      void showUndo('Аркуш видалено.', vi.fn(), 100_000);
    });

    expect(screen.getByRole('button', { name: 'Undo' })).toBeDefined();

    // ⚠ Той самий хрестик, що й у решти тостів (`notify.test.tsx`, F8):
    // додавання дії «назад» не мало права його загубити.
    expect(screen.getByRole('button', { name: 'Close notification' })).toBeDefined();
  });

  it('підпис дії задається параметром — літерал лише за замовчуванням', async () => {
    mount();

    await act(async () => {
      void showUndo('Аркуш видалено.', vi.fn(), 100_000, 'Повернути');
    });

    expect(screen.getByRole('button', { name: 'Повернути' })).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Undo' })).toBeNull();
  });

  it('закритий хрестиком тост — це відповідь «ні»: onUndo не кличеться', async () => {
    const onUndo = vi.fn();
    mount();

    let result: boolean | undefined;

    await act(async () => {
      void showUndo('Аркуш видалено.', onUndo, 100_000).then((value) => {
        result = value;
      });
    });

    await act(async () => {
      screen.getByRole('button', { name: 'Close notification' }).click();
    });

    expect(onUndo).not.toHaveBeenCalled();
    expect(result).toBe(false);
  });
});

/**
 * ⛔ Другий бік доказу. Рендер тут не потрібен навмисно: обіцянка — контракт
 * ФУНКЦІЇ, і вона не сміє залежати від того, чи змонтований
 * `<Notifications />` (екран міг бути вже розмонтований, коли вікно збігло).
 */
describe('showUndo: вікно збігло', () => {
  it('onUndo не викликається, обіцянка — false', async () => {
    vi.useFakeTimers();

    const onUndo = vi.fn();
    const settled = vi.fn();

    void showUndo('Аркуш видалено.', onUndo, 1000).then(settled);

    await vi.advanceTimersByTimeAsync(1000);

    expect(onUndo).not.toHaveBeenCalled();
    expect(settled).toHaveBeenCalledWith(false);
  });

  /**
   * ⛔ Саме це число з директиви — 8000, і воно перевіряється МЕЖЕЮ, а не
   * читанням константи. Мутаційний доказ обома боками: зменште дефолт (4000)
   * — падає перша перевірка (обіцянка розв'язалася раніше 8 с); збільште
   * (12000) — падає друга (на 8 с іще не розв'язалася). Приберіть дефолт
   * зовсім — `ms` стане `undefined`, `setTimeout` спрацює негайно, і падає
   * знову перша.
   */
  it('вікно за замовчуванням — рівно 8000 мс', async () => {
    vi.useFakeTimers();

    const settled = vi.fn();

    void showUndo('Аркуш видалено.', vi.fn()).then(settled);

    await vi.advanceTimersByTimeAsync(7999);
    expect(settled).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(1);
    expect(settled).toHaveBeenCalledWith(false);
  });

  it('обіцянка розв’язується один раз: тост, що збіг, уже не змінює відповіді', async () => {
    vi.useFakeTimers();

    const onUndo = vi.fn();
    const settled = vi.fn();

    void showUndo('Аркуш видалено.', onUndo, 1000).then(settled);

    await vi.advanceTimersByTimeAsync(5000);

    // ⚠ П'ять секунд — це п'ять спрацювань, якби таймер був циклічним, і
    // щонайменше два шляхи закриття (власний таймер плюс `autoClose`
    // Mantine). Відповідь мусить лишитися ОДНА.
    expect(settled).toHaveBeenCalledTimes(1);
    expect(onUndo).not.toHaveBeenCalled();
  });
});
