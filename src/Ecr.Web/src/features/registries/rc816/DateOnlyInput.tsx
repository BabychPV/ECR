import { Suspense, lazy, type JSX } from 'react';
import { Skeleton } from '@mantine/core';
import { formatDateOnly, parseDateOnly } from '@/shared/format';

/**
 * `@mantine/dates` — за `import()` (`D-132`): дата в редакторі частин потрібна не кожному, хто
 * його відкрив, і чанк не має важити в бюджет маршруту. Той самий прийом, що в `CollectionRunsPanel`.
 */
const DateInput = lazy(async () => {
  const module = await import('@/shared/dates/DateInputWithStyles');
  return { default: module.DateInput };
});

/**
 * Поле бізнес-дати: значення — рядок `yyyy-MM-dd`, як його віддає і приймає сервер.
 *
 * ⛔ Не нативне `type="date"` (D15-09): воно бере формат з ОС, а не з локалі продукту.
 */
export function DateOnlyInput({
  value,
  onChange,
  label,
  ariaLabel,
  disabled,
  clearable,
}: {
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly label?: string | undefined;
  readonly ariaLabel?: string | undefined;
  readonly disabled?: boolean | undefined;
  readonly clearable?: boolean | undefined;
}): JSX.Element {
  return (
    <Suspense fallback={<Skeleton height={30} width={140} />}>
      <DateInput
        size="xs"
        miw={140}
        label={label}
        aria-label={ariaLabel}
        valueFormat="YYYY-MM-DD"
        clearable={clearable === true}
        disabled={disabled === true}
        value={parseDateOnly(value)}
        onChange={(next) => onChange(next === null ? '' : formatDateOnly(next))}
      />
    </Suspense>
  );
}
