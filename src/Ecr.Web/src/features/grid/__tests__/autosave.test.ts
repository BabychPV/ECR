import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createDebouncer, registerUnloadFlush } from '../autosave';

/**
 * Автозбереження (`B-35`, `#38`).
 *
 * ⛔ До виправлення `useCellPatch.ts` ОБІЦЯВ коментарем дебаунс ~500 мс і
 * збереження перед закриттям вкладки — і жоден механізм не існував. Ці тести
 * доводять поведінку, а не факт виклику: фальшивий таймер і подія
 * `beforeunload`, без монтування grid.
 */

describe('Дебаунс автозбереження', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('спрацьовує через 500 мс тиші після ОСТАННЬОГО виклику trigger', () => {
    const callback = vi.fn();
    const debouncer = createDebouncer(callback);

    debouncer.trigger();
    vi.advanceTimersByTime(300);
    // ⚠ Друга правка ПЕРЕЗАПУСКАЄ вікно тиші: без цього тест довів би
    // троттлінг, не дебаунс — а це два різні механізми з різним ризиком
    // (троттлінг зберігає півправки, дебаунс — ні).
    debouncer.trigger();
    vi.advanceTimersByTime(300);

    expect(callback).not.toHaveBeenCalled();

    vi.advanceTimersByTime(200);

    expect(callback).toHaveBeenCalledTimes(1);
  });

  it('без жодного виклику trigger нічого не зберігає', () => {
    const callback = vi.fn();
    createDebouncer(callback);

    vi.advanceTimersByTime(5000);

    expect(callback).not.toHaveBeenCalled();
  });

  it('cancel знімає заплановане збереження', () => {
    const callback = vi.fn();
    const debouncer = createDebouncer(callback);

    debouncer.trigger();
    debouncer.cancel();
    vi.advanceTimersByTime(5000);

    expect(callback).not.toHaveBeenCalled();
  });
});

describe('Збереження перед закриттям вкладки (beforeunload)', () => {
  it('незбережені правки надсилаються перед вивантаженням сторінки', () => {
    // ⛔ D-134: БЕЗ виправлення тут немає жодного слухача `beforeunload`
    // взагалі — `flush` не викликається ніколи, і цей `expect` падає з
    // «expected spy to be called once, but got 0 calls»: правильна причина,
    // не помилка стенду.
    const flush = vi.fn();
    const unregister = registerUnloadFlush(() => true, flush);

    window.dispatchEvent(new Event('beforeunload'));

    expect(flush).toHaveBeenCalledTimes(1);

    unregister();
  });

  it('без незбережених правок нічого не надсилає', () => {
    const flush = vi.fn();
    const unregister = registerUnloadFlush(() => false, flush);

    window.dispatchEvent(new Event('beforeunload'));

    expect(flush).not.toHaveBeenCalled();

    unregister();
  });

  it('відписка знімає слухача — подія після неї нічого не робить', () => {
    const flush = vi.fn();
    const unregister = registerUnloadFlush(() => true, flush);

    unregister();
    window.dispatchEvent(new Event('beforeunload'));

    expect(flush).not.toHaveBeenCalled();
  });

  it('не показує діалог підтвердження виходу', () => {
    // ⚠ Мета — прибрати ПОТРЕБУ питати оператора, а не замінити явне
    // збереження попередженням, яке за звичкою закривають не читаючи.
    const event = new Event('beforeunload', { cancelable: true });
    const preventDefault = vi.spyOn(event, 'preventDefault');

    const unregister = registerUnloadFlush(() => true, vi.fn());
    window.dispatchEvent(event);
    unregister();

    expect(preventDefault).not.toHaveBeenCalled();
  });
});
