import { lazy, Suspense, type JSX } from 'react';
import { Input, Skeleton } from '@mantine/core';

// Поле дати — `@mantine/dates` разом зі стилями, лише за `import()` (бюджет маршруту, D-132; D15-09: не нативне `type="date"`).
const DateOnlyInput = lazy(async () => ({
  default: (await import('@/shared/dates/DateInputWithStyles')).DateOnlyInput,
}));

export interface DateFieldProps {
  readonly label: string;
  /** `yyyy-MM-dd`; порожньо — дати немає. */
  readonly value: string;
  readonly onChange: (value: string) => void;
  readonly description?: string | undefined;
  readonly error?: string | undefined;
  readonly required?: boolean | undefined;
  readonly disabled?: boolean | undefined;
}

/**
 * Поле бізнес-дати форм екрана дозволу: значення — рядок `yyyy-MM-dd`, як його віддає й приймає сервер.
 * `DateOnlyInput` ні помилки, ні `required` не знає, тож зірочка йде в підпис, а відмова — рядком під полем.
 *
 * ⛔ Скелет, поки вантажиться чанк дат, має `role="status"`: `aria-label` на голому `div` — axe serious
 * `aria-prohibited-attr` (PA-U-10, нестабільно: ловиться лише у вікні між першим рендером і чанком).
 */
export function DateField({ label, value, onChange, description, error, required, disabled }: DateFieldProps): JSX.Element {
  const shown = required === true ? `${label} *` : label;

  return (
    <div>
      <Suspense fallback={<Skeleton height="var(--ecr-ctl-height)" role="status" aria-label={label} />}>
        {disabled === true ? (
          <Input.Wrapper label={shown}>
            <Input readOnly value={value} />
          </Input.Wrapper>
        ) : (
          <DateOnlyInput label={shown} description={description} value={value} onChange={onChange} />
        )}
      </Suspense>
      {error !== undefined && <Input.Error mt="xs">{error}</Input.Error>}
    </div>
  );
}
