import { useEffect, useRef, useState, type FocusEvent, type JSX, type KeyboardEvent, type MouseEvent } from 'react';
import { DateInput, type DateInputProps } from '@mantine/dates';
import { language, t } from '@/shared/i18n';
import { parseUserDate } from './userDate';

/**
 * Поле дати продукту: `DateInput` із `@mantine/dates` зі СТРОГИМ розбором набраного тексту (A1-02).
 *
 * ⛔ Що було без нього (приймальний прохід A1, шапка документа):
 *   - `05.10.2026` зберігалось як 2026-05-10 — розбір `@mantine/dates` іде в `new Date(text)`;
 *   - `2026-13-45` ставало 2027-02-14 — рушій перекочує неіснуючий день;
 *   - чого рушій не розібрав, на виході з поля мовчки стиралося до попереднього значення
 *     (`fixOnBlur`), і людина не дізнавалась, що введене не прийнято.
 *
 * Тепер: текст розбирає `parseUserDate` за мовою інтерфейсу; нерозібраний чи поза `minDate`/`maxDate`
 * текст лишається в полі як є, під полем — відмова мовою інтерфейсу, значення НЕ змінюється (ні на
 * перекочену дату, ні на порожнє). Вибір дня в календарі й кнопка очищення — як і раніше.
 *
 * ⚠ Формат показу — завжди `YYYY-MM-DD` (`valueFormat` не приймається): однозначний для всіх мов, і
 * `parseUserDate` розбирає його в будь-якій мові, тож показане поле набирається назад.
 *
 * `onInvalidChange` — для форм, що мусять не дати зберегти, поки в полі нерозібраний текст.
 *
 * ⛔ Календар закривають Enter і Escape (A2-09): рідний `DateInput` закривав його лише вибором дня чи
 * виходом із поля, тож після набраної дати випадний блок лишався поверх кнопки «Зберегти» шапки, доки
 * людина не тиснула Tab. Фокус лишається на полі (відкривачі); набір, клік чи новий фокус відкривають
 * календар знову.
 */
export type StrictDateInputProps = Omit<DateInputProps, 'valueFormat' | 'dateParser' | 'fixOnBlur'> & {
  readonly onInvalidChange?: ((invalid: boolean) => void) | undefined;
};

type Problem = 'invalid' | 'outOfRange';

/** Календарний день як число `yyyyMMdd` — порівняння меж без годин. */
function dayNumber(date: Date): number {
  return date.getFullYear() * 10000 + (date.getMonth() + 1) * 100 + date.getDate();
}

function problemOf(text: string, lang: string, minDate: Date | undefined, maxDate: Date | undefined): Problem | null {
  if (text.trim() === '') return null;

  const parsed = parseUserDate(text, lang);
  if (parsed === null) return 'invalid';
  if (minDate !== undefined && dayNumber(parsed) < dayNumber(minDate)) return 'outOfRange';
  if (maxDate !== undefined && dayNumber(parsed) > dayNumber(maxDate)) return 'outOfRange';

  return null;
}

export function StrictDateInput({
  onInvalidChange,
  onBlur,
  onChange,
  onFocus,
  onClick,
  onKeyDown,
  popoverProps,
  error,
  label,
  ...props
}: StrictDateInputProps): JSX.Element {
  const lang = language();
  // Останній набраний текст; `null` — людина нічого не набирала після останнього значення.
  const [draft, setDraft] = useState<string | null>(null);
  // Відмову показуємо після виходу з поля, а не на кожній проміжній літері.
  const [shown, setShown] = useState(false);
  // Календар закрито з клавіатури (Enter/Escape). Стан відкриття живе всередині `DateInput`, тож
  // закриття — накладка `popoverProps.opened = false`, яку знімає наступна дія людини в полі.
  const [dismissed, setDismissed] = useState(false);

  const problem = draft === null ? null : problemOf(draft, lang, props.minDate, props.maxDate);

  // ⚠ Колбек — через ref: форма передає нову стрілку на кожен рендер, і ефект на неї крутився б щоразу.
  const reportInvalid = useRef(onInvalidChange);
  reportInvalid.current = onInvalidChange;
  const invalid = problem !== null;
  useEffect(() => {
    if (!invalid) return undefined;
    reportInvalid.current?.(true);
    // Текст виправили, поле зникло з форми — форма більше не тримає його відмову.
    return () => reportInvalid.current?.(false);
  }, [invalid]);

  const typed = { value: draft?.trim() ?? '' };
  const message =
    problem === null || !shown ? null : problem === 'invalid' ? t('dates.invalid', typed) : t('dates.outOfRange', typed);

  return (
    <DateInput
      {...props}
      // Підпис — від того, хто ставить поле (ФВ-14.20 перевіряє його там).
      label={label}
      popoverProps={dismissed ? { ...popoverProps, opened: false } : (popoverProps ?? {})}
      valueFormat="YYYY-MM-DD"
      // ⛔ `null` замість `Invalid Date`: `DateInput` тоді не змінює значення — ні перекочування, ні стирання.
      dateParser={(text) => {
        setDraft(text);
        setDismissed(false);
        return parseUserDate(text, lang);
      }}
      // ⛔ Нерозібраний текст лишається в полі: інакше поле мовчки повернуло б попереднє значення.
      fixOnBlur={problem === null}
      error={error ?? message}
      onChange={(next) => {
        // Значення прийнято (розібраний текст, день у календарі, очищення) — набраного «боргу» немає.
        setDraft(null);
        setShown(false);
        setDismissed(false);
        onChange?.(next);
      }}
      onKeyDown={(event: KeyboardEvent<HTMLInputElement>) => {
        // ⚠ Лише закриття: Enter не перехоплюється (`preventDefault` не кличемо), щоб форма навколо
        // поводилась як і раніше; набране значення вже прийняте розбором на кожну літеру.
        if (event.key === 'Enter' || event.key === 'Escape') {
          setDismissed(true);
          // A3-01: Enter не виводить із поля, тож без цього недійсна дата після Enter лишалась без пояснення
          // (Save неактивний, `aria-invalid=false`) — відмову показуємо й тут, а не лише на blur.
          const text = event.currentTarget.value;
          setDraft(text);
          setShown(problemOf(text, lang, props.minDate, props.maxDate) !== null);
        }
        onKeyDown?.(event);
      }}
      onFocus={(event: FocusEvent<HTMLInputElement>) => {
        setDismissed(false);
        onFocus?.(event);
      }}
      onClick={(event: MouseEvent<HTMLInputElement>) => {
        setDismissed(false);
        onClick?.(event);
      }}
      onBlur={(event: FocusEvent<HTMLInputElement>) => {
        // ⚠ Очищення поля з `clearable` розбір не кличе — тож текст беремо з самого поля.
        const text = event.currentTarget.value;
        setDraft(text);
        setShown(problemOf(text, lang, props.minDate, props.maxDate) !== null);
        onBlur?.(event);
      }}
    />
  );
}
