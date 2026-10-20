import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen, waitFor } from '@testing-library/react';
import { renderWithMantine } from '@/test/render';
import { StrictDateInput } from '@/shared/dates/StrictDateInput';
import { calendarLocale } from '@/shared/dates/calendarLocale';

/**
 * P3-N5: назва місяця в календарі - мовою інтерфейсу (раніше завжди «January 2026»).
 * Мутаційний доказ (вручну, відновлено): прибрати `locale={calendarLocale(lang)}` - ru/kz тести червоні.
 */
const lang = vi.hoisted(() => ({ value: 'en' }));
vi.mock('@/shared/i18n', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/shared/i18n')>()),
  language: () => lang.value,
}));

afterEach(() => cleanup());

function openCalendar(): void {
  renderWithMantine(<StrictDateInput label="Date" value={new Date('2026-01-15T00:00:00')} onChange={() => undefined} />);
  const input = screen.getByLabelText('Date');
  input.focus();
  fireEvent.focus(input);
  fireEvent.click(input);
}

const monthLabel = (): string =>
  [...document.querySelectorAll('[data-dates-dropdown] button')].map((b) => b.textContent ?? '').find((text) => /2026/.test(text) && !/^\d+$/.test(text)) ?? '';

describe('календар: мова назви місяця', () => {
  it('calendarLocale: en/ru/kz -> en/ru/kk', () => {
    expect([calendarLocale('en'), calendarLocale('ru'), calendarLocale('kz'), calendarLocale('xx')]).toEqual(['en', 'ru', 'kk', 'en']);
  });

  for (const [language, expected] of [
    ['en', 'january 2026'],
    ['ru', 'январь 2026'],
    ['kz', 'қаңтар 2026'],
  ] as const) {
    it(`${language}: «${expected}» (без урахування регістру)`, async () => {
      lang.value = language;
      openCalendar();

      await waitFor(() => expect(monthLabel().toLowerCase()).toBe(expected));
    });
  }
});
