import { useState, type JSX } from 'react';
import { flushSync } from 'react-dom';
import { TextInput, type TextInputProps } from '@mantine/core';
import { formatDecimalReadable, ReadableSignificantDigits } from '@/shared/format/number';

/** Вирівнювання і табличні цифри — одне правило для всіх числових полів. */
const NumericInputStyles = { input: { textAlign: 'end' as const, fontVariantNumeric: 'tabular-nums' } };

export interface DecimalInputProps extends Omit<TextInputProps, 'value' | 'onChange' | 'inputMode'> {
  /** Збережений рядок `decimal` (повна точність) або те, що набрав користувач. */
  readonly value: string;
  /** Новий текст поля — як набрано; розбір і нормалізація лишаються викликачеві. */
  readonly onValueChange: (next: string) => void;
  /** Значущих цифр у поданні без фокуса. */
  readonly significant?: number;
}

/**
 * Поле десяткового (людина 25.11, LIVE): без фокуса — читабельне подання (`formatDecimalReadable`: без хвостових
 * нулів, ≤ 7 значущих, крихітні — `4.013E-13`), точне значення — у підказці; на фокусі — збережений рядок ПОВНІСТЮ,
 * для правки. Незмінене значення лишається тим самим рядком (подання не торкається `value`), тож на сервер не йде
 * округлене.
 *
 * ⚠ Нечислове (помилка вводу) показується як є і на фокусі, і без нього — щоб помилку було видно.
 */
export function DecimalInput({
  value,
  onValueChange,
  significant = ReadableSignificantDigits,
  label,
  'aria-label': ariaLabel,
  readOnly,
  onFocus,
  onBlur,
  title,
  ...rest
}: DecimalInputProps): JSX.Element {
  const [focused, setFocused] = useState(false);
  const readable = formatDecimalReadable(value, significant);
  const shown = focused || readable === null ? value : readable.text;
  const hint = readable?.rounded === true ? readable.exact : undefined;

  return (
    <TextInput
      {...rest}
      label={label}
      aria-label={ariaLabel}
      inputMode="decimal"
      value={shown}
      readOnly={readOnly}
      title={title ?? hint}
      data-rounded={!focused && hint !== undefined ? '' : undefined}
      styles={NumericInputStyles}
      onFocus={(event) => {
        // ⚠ Текст поля на фокусі ЗМІНЮЄТЬСЯ (подання → повний рядок): синхронно, і тоді виділити весь — інакше каретка
        // стояла б у випадковому місці іншого рядка, а набране злилося б із ним (e2e: «97.000…96.5»).
        const differs = readable !== null && readable.text !== value;
        flushSync(() => setFocused(true));
        if (differs && !readOnly) event.currentTarget.select();
        onFocus?.(event);
      }}
      onBlur={(event) => {
        setFocused(false);
        onBlur?.(event);
      }}
      onChange={(event) => onValueChange(event.currentTarget.value)}
    />
  );
}
