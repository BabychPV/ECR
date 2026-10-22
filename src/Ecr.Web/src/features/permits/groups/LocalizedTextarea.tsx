import type { JSX } from 'react';
import { Skeleton, Stack, Textarea } from '@mantine/core';
import { useLanguages } from '@/shared/i18n/useLanguages';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';

/**
 * Багатомовний текст у `Textarea` - по полю на кожну мову реєстру `GET /api/v1/languages` (опис групи; `LocalizedInput`
 * з `shared/ui` дає однорядкове поле, а описові потрібно кілька рядків). Порожня мова у вихідну мапу не потрапляє.
 * Порядок станів - як у `LocalizedInput`: помилка → очікування → порожній реєстр → поля.
 */
export function LocalizedTextarea({
  label,
  description,
  value,
  maxLength,
  disabled = false,
  error,
  onChange,
}: {
  readonly label: string;
  readonly description?: string | undefined;
  readonly value: Readonly<Record<string, string>>;
  readonly maxLength: number;
  readonly disabled?: boolean;
  readonly error?: string | undefined;
  readonly onChange: (next: Record<string, string>) => void;
}): JSX.Element {
  const languages = useLanguages();

  if (languages.error !== null) {
    return <ErrorAlert error={languages.error} onRetry={() => void languages.refetch()} />;
  }
  if (languages.isPending) return <Skeleton height={60} radius="sm" data-localized-input="pending" />;

  const list = languages.data ?? [];
  if (list.length === 0) return <></>;

  return (
    <Stack gap="xs">
      {list.map((language, index) => (
        <Textarea
          key={language.code}
          label={`${label} · ${language.nameNative}`}
          description={language.isDefault ? description : undefined}
          value={value[language.code] ?? ''}
          autosize
          minRows={2}
          maxRows={6}
          maxLength={maxLength}
          disabled={disabled}
          error={index === 0 ? error : undefined}
          onChange={(event) => {
            const next = { ...value };
            const text = event.currentTarget.value;
            if (text.length === 0) delete next[language.code];
            else next[language.code] = text;
            onChange(next);
          }}
        />
      ))}
    </Stack>
  );
}
