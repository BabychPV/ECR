import type { JSX } from 'react';
import { NativeSelect } from '@mantine/core';
import { lookupLabel, useLookupOptions } from '@/features/registries/rc812/data';
import { t } from '@/shared/i18n';

/** Довідник «Air permit areas» (Offshore / Onshore): район вузла NAE вибирається з нього, а не вводиться (людина 04.11). */
export const AreaRegistryCode = 'AIR_AREA';

export interface AreaSelectProps {
  readonly label: string;
  readonly value: string;
  readonly onChange: (code: string) => void;
  readonly description?: string | undefined;
  readonly error?: string | JSX.Element | undefined;
  /** Атрибут для e2e/тестів на самому select. */
  readonly 'data-nae-area'?: string | undefined;
}

/**
 * Вибір району зі списку. Порожній варіант - «без району». Значення, якого немає в довіднику (дані з часів, коли район
 * вводили текстом), лишається видимим окремим варіантом, щоб форма не підмінила його мовчки.
 */
export function AreaSelect({ label, value, onChange, description, error, ...rest }: AreaSelectProps): JSX.Element {
  const areas = useLookupOptions(AreaRegistryCode, '', null);
  const rows = areas.data?.items ?? [];
  const listed = rows.map((row) => ({ value: row.code, label: lookupLabel(row) }));
  const data = [
    { value: '', label: t('permits.nae.manual.areaNone') },
    ...listed,
    ...(value !== '' && !listed.some((option) => option.value === value) ? [{ value, label: value }] : []),
  ];

  return (
    <NativeSelect
      label={label}
      description={description}
      error={error}
      value={value}
      data={data}
      disabled={areas.isPending}
      onChange={(event) => onChange(event.currentTarget.value)}
      {...rest}
    />
  );
}