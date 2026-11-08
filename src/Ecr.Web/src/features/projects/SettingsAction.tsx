import type { JSX, ReactNode } from 'react';
import { Box, Group, Text } from '@mantine/core';

/**
 * ✎ UAT project-settings (людина 08.11: кнопки «нічого не кажуть»): дія налаштувань проєкту — кнопка з повною назвою
 * і рядок опису поруч (на вузькому екрані — під нею). Опис пов'язаний із кнопкою через `aria-describedby`, тож
 * читалка оголошує його разом із кнопкою.
 */
export function SettingsAction({
  id,
  description,
  children,
}: {
  /** Ідентифікатор дії: `data-settings-action` і основа id опису. */
  readonly id: string;
  readonly description: string;
  /** Кнопка дії; отримує id опису для `aria-describedby`. */
  readonly children: (describedBy: string) => ReactNode;
}): JSX.Element {
  const descriptionId = `project-settings-action-${id}-description`;

  return (
    <Group gap="md" wrap="wrap" align="center" data-settings-action={id}>
      <Box miw={220}>{children(descriptionId)}</Box>
      <Text id={descriptionId} size="sm" c="dimmed" maw={560} style={{ flex: '1 1 240px' }}>
        {description}
      </Text>
    </Group>
  );
}
