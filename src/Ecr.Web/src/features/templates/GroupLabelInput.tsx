import { useState, type JSX } from 'react';
import { Button, Collapse, Skeleton, Stack, Text, TextInput } from '@mantine/core';
import { language as currentLanguage, t } from '@/shared/i18n';
import { useLanguages } from '@/shared/i18n/useLanguages';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import type { LocalizedValue } from '@/shared/ui/LocalizedInput';

/**
 * Назва групи заголовка: поле ПОТОЧНОЇ мови інтерфейсу на виду, решта мов — згорнуто під «Other languages».
 * Той самий словник мов, що в `LocalizedInput` (реєстр `GET /api/v1/languages`), але без трьох-чотирьох
 * полів на кожну групу дерева: діалог із шістьма групами інакше займає кілька екранів.
 *
 * ⚠ Порожня мова не потрапляє у значення (як у `LocalizedInput`): `{"en":"X","ru":""}` — це не «російською
 * порожньо». Згорнуті поля, у яких вже є текст, показують лічильник на кнопці.
 */
export function GroupLabelInput({
  value,
  onChange,
}: {
  readonly value: LocalizedValue;
  readonly onChange: (next: LocalizedValue) => void;
}): JSX.Element {
  const languages = useLanguages();
  const [open, setOpen] = useState(false);

  if (languages.error !== null) {
    return <ErrorAlert error={languages.error} onRetry={() => void languages.refetch()} />;
  }

  if (languages.isPending) return <Skeleton height={60} radius="sm" data-localized-input="pending" />;

  const list = languages.data ?? [];

  if (list.length === 0) {
    return (
      <Text size="sm" c="dimmed" data-localized-input="empty">
        {t('state.emptyTitle')}
      </Text>
    );
  }

  const current = list.find((item) => item.code === currentLanguage()) ?? list.find((item) => item.isDefault) ?? list[0];
  const others = list.filter((item) => item.code !== current?.code);
  const filled = others.filter((item) => (value[item.code] ?? '').trim().length > 0).length;

  const field = (code: string, nameNative: string, primary: boolean): JSX.Element => (
    <TextInput
      key={code}
      label={`${t('colgroups.label')} · ${nameNative}`}
      value={value[code] ?? ''}
      data-group-label={primary ? 'current' : code}
      onChange={(event) => {
        const next = { ...value };
        const text = event.currentTarget.value;

        if (text.length === 0) delete next[code];
        else next[code] = text;

        onChange(next);
      }}
    />
  );

  return (
    <Stack gap="xs">
      {current !== undefined && field(current.code, current.nameNative, true)}

      {others.length > 0 && (
        <>
          <Button
            variant="subtle"
            size="xs"
            style={{ alignSelf: 'flex-start' }}
            aria-expanded={open}
            data-testid="group-other-languages"
            onClick={() => setOpen((was) => !was)}
          >
            {t('colgroups.otherLanguages')}
            {filled > 0 ? ` (${String(filled)})` : ''}
          </Button>
          <Collapse in={open}>
            <Stack gap="xs">{others.map((item) => field(item.code, item.nameNative, false))}</Stack>
          </Collapse>
        </>
      )}
    </Stack>
  );
}
