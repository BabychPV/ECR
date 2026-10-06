import type { FocusEventHandler, JSX } from 'react';
import { StrictDateInput } from './StrictDateInput';
import { formatIsoDay, parseUserDate } from './userDate';

/**
 * Поле календарної дати, значення якого — рядок `yyyy-MM-dd` (`''` — порожньо), як його віддає й
 * приймає сервер. Заміна рідного `<TextInput type="date">` (борг `D15-09`, A1-02).
 *
 * ⛔ Чому не рідне поле: воно бере формат з ОС, а не з мови продукту, і недонабрану чи неіснуючу дату
 * (`31.02.2026`) віддає як `''` — форма мовчки отримувала порожнє значення. Тут розбір строгий
 * (`StrictDateInput`): нерозібраний текст лишається в полі з відмовою під ним, значення не змінюється.
 *
 * ⚠ Вантажити ЛИШЕ через `import('@/shared/dates/DateInputWithStyles')` (`D-132`): статичний імпорт
 * затяг би `@mantine/dates` у чанк сторінки, а окремий спільний модуль-обгортка з `lazy()` — новий
 * чанк, чиє ім'я осідає в карті передзавантаження вхідного чанка (росте бюджет КОЖНОГО маршруту).
 */
export function DateOnlyInput({
  value,
  onChange,
  label,
  description,
  size,
  clearable = true,
  autoFocus,
  onFocus,
  onBlur,
  onInvalidChange,
}: {
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly label: string;
  readonly description?: string | undefined;
  readonly size?: 'xs' | 'sm' | undefined;
  readonly clearable?: boolean | undefined;
  /** `data-autofocus` для `Modal` Mantine: перше поле діалогу. */
  readonly autoFocus?: boolean | undefined;
  readonly onFocus?: FocusEventHandler<HTMLInputElement> | undefined;
  readonly onBlur?: FocusEventHandler<HTMLInputElement> | undefined;
  readonly onInvalidChange?: ((invalid: boolean) => void) | undefined;
}): JSX.Element {
  return (
    <StrictDateInput
      label={label}
      description={description}
      size={size ?? 'sm'}
      clearable={clearable}
      // Значення — `yyyy-MM-dd` (те, що віддавало рідне поле); розбір у формі показу, незалежно від мови.
      value={value === '' ? null : parseUserDate(value, 'en')}
      onChange={(next) => onChange(next === null ? '' : formatIsoDay(next))}
      onFocus={onFocus}
      onBlur={onBlur}
      onInvalidChange={onInvalidChange}
      {...(autoFocus === true ? { 'data-autofocus': true } : {})}
    />
  );
}
