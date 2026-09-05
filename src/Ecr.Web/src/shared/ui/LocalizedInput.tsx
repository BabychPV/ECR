import type { JSX } from 'react';
import { Stack, TextInput } from '@mantine/core';
import { useLanguages } from '@/shared/i18n/useLanguages';

/** Назва мовами каталогу: код мови → текст. */
export type LocalizedValue = Record<string, string>;

/**
 * Поле локалізованої назви — по рядку на кожну мову реєстру.
 *
 * ⛔ Мови беруться з `GET /api/v1/languages`, а не з константи. Колонка
 * `…L10n` — це об'єкт саме тому, що «додати мову = запис у реєстр, а не
 * колонка в двадцяти таблицях» (`ФВ-2.2`); зашитий список у формі повернув би
 * ту саму жорсткість на крок раніше.
 *
 * ⚠ Порожні мови **не потрапляють у запит**: `{"en":"Water","ru":""}` — це не
 * «російської немає», а «російською називається порожньо», і `localized()`
 * поверне за такою назвою англійську лише завдяки тому, що перевіряє довжину.
 * Не надсилати порожнє чесніше, ніж покладатися на це.
 */
export function LocalizedInput({
  label,
  description,
  value,
  onChange,
}: {
  label: string;
  description?: string | undefined;
  value: LocalizedValue;
  onChange: (next: LocalizedValue) => void;
}): JSX.Element {
  const languages = useLanguages();

  return (
    <Stack gap="xs">
      {(languages.data ?? []).map((language) => (
        <TextInput
          key={language.code}
          label={`${label} · ${language.nameNative}`}
          description={language.isDefault ? description : undefined}
          value={value[language.code] ?? ''}
          onChange={(event) => {
            const next = { ...value };
            const text = event.currentTarget.value;

            if (text.length === 0) {
              delete next[language.code];
            } else {
              next[language.code] = text;
            }

            onChange(next);
          }}
        />
      ))}
    </Stack>
  );
}

/** Чи заповнена назва хоча б однією мовою. */
export function hasAnyText(value: LocalizedValue): boolean {
  return Object.values(value).some((text) => text.trim().length > 0);
}
