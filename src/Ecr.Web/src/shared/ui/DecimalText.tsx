import type { JSX } from 'react';
import { Text, type TextProps } from '@mantine/core';
import { formatDecimalReadable, ReadableSignificantDigits } from '@/shared/format/number';
import './DecimalText.css';

export interface DecimalTextProps extends Omit<TextProps, 'children'> {
  /** Рядок `decimal` (або число); порожнє — `empty`. */
  readonly value: string | number | null | undefined;
  /** Що показати, коли значення немає. */
  readonly empty?: string;
  /** Значущих цифр. */
  readonly significant?: number;
}

/**
 * Десяткове для читання в таблицях і картках (людина 25.11): те саме подання, що й `DecimalInput` без фокуса, точне
 * значення — у підказці (`title`), цифри табличні. Нечислове показується як є.
 */
export function DecimalText({
  value,
  empty = '—',
  significant = ReadableSignificantDigits,
  ...rest
}: DecimalTextProps): JSX.Element {
  if (value === null || value === undefined || value === '') {
    return (
      <Text span inherit {...rest}>
        {empty}
      </Text>
    );
  }
  const readable = formatDecimalReadable(value, significant);
  return (
    <Text
      span
      inherit
      {...rest}
      className="ecr-tabular"
      title={readable?.rounded === true ? readable.exact : undefined}
      data-rounded={readable?.rounded === true ? '' : undefined}
    >
      {readable?.text ?? String(value)}
    </Text>
  );
}
