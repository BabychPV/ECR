import { useState, type JSX } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PeriodPicker } from '@/shared/ui/PeriodPicker';
import { renderWithMantine } from '@/test/render';

/**
 * A4-02 (приймальна №4): після Enter пікер поводиться ОДНАКОВО на всіх екранах — поле віддає фокус і
 * показує назву періоду. Раніше це траплялось лише на DocumentPage (вміст перемонтовувався), а на
 * списку документів, у зрізах і кампанії поле лишалось у фокусі з ключем `202610` до Tab.
 *
 * ⛔ Мутаційний доказ (перевірено): прибрати `onKeyDown` у `PeriodPicker` — червоніють усі «Enter»-випадки
 * (поле в фокусі й показує ключ).
 *
 * Споживачі моделюються способом, яким вони тримають значення: стан у батьківському (список документів,
 * кампанія, зрізи — `useUrlNumber`), `value ?? попереднє` (DocumentPage, діалоги), і періодичність проєкту.
 */

afterEach(() => {
  cleanup();
});

const periodInput = (): HTMLInputElement =>
  screen.getByRole<HTMLInputElement>('textbox', { name: '⟦documents.period⟧' });

/** Керований споживач: значення в батьківському стані (як адреса в списках). */
function Controlled({ keepOnClear = false, periodKind }: { keepOnClear?: boolean; periodKind?: string }): JSX.Element {
  const [period, setPeriod] = useState<number | null>(202609);

  return (
    <PeriodPicker
      value={period}
      periodKind={periodKind}
      onChange={(next) => setPeriod(keepOnClear ? (next ?? period) : next)}
    />
  );
}

describe('PeriodPicker: Enter підтверджує — blur і назва', () => {
  it('керований споживач (список документів / зрізи / кампанія): Enter після набору → фокус вийшов, назва', async () => {
    const user = userEvent.setup();
    renderWithMantine(<Controlled />);
    const input = periodInput();

    await user.click(input);
    await user.keyboard('202610');
    expect(document.activeElement).toBe(input);
    expect(input.value).toBe('202610');

    await user.keyboard('{Enter}');

    expect(document.activeElement).not.toBe(input);
    expect(input.value).toBe('October 2026');
  });

  it('споживач `value ?? попереднє` (DocumentPage, діалоги): Enter → blur і назва', async () => {
    const user = userEvent.setup();
    renderWithMantine(<Controlled keepOnClear />);
    const input = periodInput();

    await user.click(input);
    await user.keyboard('202508{Enter}');

    expect(document.activeElement).not.toBe(input);
    expect(input.value).toBe('August 2025');
  });

  it('квартальний проєкт: Enter → blur і назва кварталу', async () => {
    const user = userEvent.setup();
    renderWithMantine(<Controlled periodKind="Quarterly" />);
    const input = periodInput();

    await user.click(input);
    await user.keyboard('202603{Enter}');

    expect(document.activeElement).not.toBe(input);
    expect(input.value).not.toBe('202603');
    expect(input.value.length).toBeGreaterThan(0);
  });

  it('Enter без змін (поле лише відкрите, значення повне) теж знімає фокус і лишає назву', async () => {
    const user = userEvent.setup();
    renderWithMantine(<Controlled />);
    const input = periodInput();

    await user.click(input);
    // UI-13: у фокусі поле лишає назву виділеною (перша цифра замінює її ключем) — ключ не показується.
    expect(input.value).toBe('September 2026');
    await user.keyboard('{Enter}');

    expect(document.activeElement).not.toBe(input);
    expect(input.value).toBe('September 2026');
  });

  it('неповний набір: Enter нічого не підтверджує — фокус лишається, onChange не кличеться', async () => {
    const onChange = vi.fn();
    const user = userEvent.setup();
    renderWithMantine(<PeriodPicker value={202609} onChange={onChange} />);
    const input = periodInput();

    await user.click(input);
    await user.keyboard('2026{Enter}');

    expect(document.activeElement).toBe(input);
    expect(input.value).toBe('2026');
    expect(onChange).not.toHaveBeenCalled();
  });

  it('після Enter кнопка › далі крокує від підтвердженого періоду', async () => {
    const user = userEvent.setup();
    renderWithMantine(<Controlled />);
    const input = periodInput();

    await user.click(input);
    await user.keyboard('202610{Enter}');
    expect(document.activeElement).not.toBe(input);

    await user.click(screen.getByRole('button', { name: '⟦period.next⟧' }));
    expect(periodInput().value).toBe('November 2026');
  });
});
