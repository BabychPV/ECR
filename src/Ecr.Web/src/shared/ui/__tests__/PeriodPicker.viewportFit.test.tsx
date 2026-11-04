import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen } from '@testing-library/react';
import { PeriodPicker } from '@/shared/ui/PeriodPicker';
import { renderWithMantine } from '@/test/render';

/**
 * Живий стенд, вікно ~600 px: випадайка вибору періоду виходила за правий край вікна —
 * третій стовпець (березень/червень/вересень/грудень) обрізався. `PeriodMonthGrid`
 * тепер зсуває випадайку вліво так, щоб правий край був ≤ вікно − 8 px, але не лівіше 8 px.
 *
 * jsdom розкладки не має — розміри підставляються `getBoundingClientRect`; справжній
 * браузер перевіряє `e2e/periodPickerFit.spec.ts`.
 */
beforeAll(async () => {
  await import('@/shared/ui/PeriodMonthGrid');
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

const PopWidth = 300;

function stubLayout(viewport: number, popLeft: number): void {
  Object.defineProperty(document.documentElement, 'clientWidth', { configurable: true, value: viewport });
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function (this: HTMLElement) {
    const shift = this.classList.contains('ecr-period-pop') ? Number.parseFloat(this.style.left || '0') : 0;
    const left = popLeft + shift;

    return { left, right: left + PopWidth, top: 100, bottom: 400, width: PopWidth, height: 300, x: left, y: 100, toJSON: () => ({}) } as DOMRect;
  });
}

async function openGrid(): Promise<HTMLElement> {
  renderWithMantine(<PeriodPicker value={202609} onChange={() => undefined} />);
  fireEvent.click(screen.getByRole('button', { name: '⟦period.choose⟧' }));

  return screen.findByTestId('period-grid');
}

describe('Period picker fit: випадайка в межах вікна', () => {
  it('біля правого краю вузького вікна зсувається вліво: правий край = вікно − 8', async () => {
    stubLayout(600, 400);
    const pop = await openGrid();

    // 400 + 300 = 700 > 592 → зсув −108.
    expect(pop.style.left).toBe('-108px');
  });

  it('коли місця досить — лишається вирівняною з контролом', async () => {
    stubLayout(1440, 400);
    const pop = await openGrid();

    expect(pop.style.left).toBe('0px');
  });

  it('не виходить за лівий край: ліве поле 8 px має пріоритет', async () => {
    stubLayout(250, 100);
    const pop = await openGrid();

    // Зсув до 8 − 100 = −92 (правий край і так не влізе — ширину обмежує CSS).
    expect(pop.style.left).toBe('-92px');
  });
});
