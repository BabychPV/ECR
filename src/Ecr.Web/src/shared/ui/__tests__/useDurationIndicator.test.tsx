import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { act, render, renderHook, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { testTheme } from '@/test/render';
import {
  DurationThresholdsMs,
  useDurationIndicator,
  type DurationPhase,
} from '../useDurationIndicator';
import { DurationProgress } from '../DurationProgress';

/**
 * Індикація за тривалістю дії (`ФВ-14.26`).
 *
 * ⚠ Час — лише фейковий: справжні таймери зробили б тест і повільним, і
 * плаваючим саме на межах, які він перевіряє.
 */
beforeEach(() => {
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

/** Хук із керованим `pending`. */
function track(initial: boolean) {
  return renderHook(({ pending }: { pending: boolean }) => useDurationIndicator(pending), {
    initialProps: { pending: initial },
  });
}

function advance(ms: number): void {
  act(() => {
    vi.advanceTimersByTime(ms);
  });
}

describe('useDurationIndicator: фази за порогами', () => {
  it('ФВ-14.26: межі збігаються з текстом вимоги — 100 мс, 1 с, 10 с', () => {
    expect(DurationThresholdsMs).toEqual({ inline: 100, progress: 1000, background: 10_000 });
  });

  it('ФВ-14.26: 99 мс — нічого', () => {
    const { result } = track(true);

    advance(99);

    expect(result.current).toBe('none');
  });

  it('ФВ-14.26: 150 мс — індикатор на елементі', () => {
    const { result } = track(true);

    advance(150);

    expect(result.current).toBe('inline');
  });

  it('ФВ-14.26: 999 мс — ще на елементі, 1.5 с — прогрес із текстом', () => {
    const { result } = track(true);

    advance(999);
    expect(result.current).toBe('inline');

    advance(501);
    expect(result.current).toBe('progress');
  });

  it('ФВ-14.26: понад 10 с синхронна дія лишається прогресом — у фон переводить сервер', () => {
    const { result } = track(true);

    advance(12_000);

    expect(result.current).toBe('progress');
  });

  it('ФВ-14.26: дія, що завершилась до 100 мс, не блимнула жодного разу', () => {
    const seen: DurationPhase[] = [];
    const { result, rerender } = track(true);
    seen.push(result.current);

    advance(80);
    seen.push(result.current);

    rerender({ pending: false });
    seen.push(result.current);

    // ⛔ Таймери скасовані: пізніші пороги не вмикають фазу для дії, якої вже немає.
    advance(2000);
    seen.push(result.current);

    expect(seen).toEqual(['none', 'none', 'none', 'none']);
  });

  it('завершення гасить фазу в тому ж рендері, а наступна дія починає з нуля', () => {
    const { result, rerender } = track(true);

    advance(1500);
    expect(result.current).toBe('progress');

    rerender({ pending: false });
    expect(result.current).toBe('none');

    // ⚠ Нова дія не успадковує `progress` попередньої — інакше короткий
    // другий запит показав би прогрес одразу.
    rerender({ pending: true });
    expect(result.current).toBe('none');

    advance(50);
    expect(result.current).toBe('none');

    advance(60);
    expect(result.current).toBe('inline');
  });
});

describe('DurationProgress: показ лише у фазі progress', () => {
  function show(phase: DurationPhase): void {
    render(
      <MantineProvider theme={testTheme}>
        <DurationProgress phase={phase} label="Зберігаємо" />
      </MantineProvider>,
    );
  }

  it('ФВ-14.26: у none та inline не займає місця', () => {
    show('none');
    expect(screen.queryByRole('status')).toBeNull();

    show('inline');
    expect(screen.queryByRole('status')).toBeNull();
  });

  it('ФВ-14.26: у progress — текст в області status', () => {
    show('progress');

    const status = screen.getByRole('status');
    expect(status.textContent).toBe('Зберігаємо');
    expect(status.getAttribute('aria-live')).toBe('polite');
  });
});
