import type { JSX, ReactNode } from 'react';
import { Group, Title } from '@mantine/core';

/** Заголовок сторінки з місцем для дій праворуч. */
export function PageHeader({
  title,
  actions,
}: {
  title: string;
  actions?: ReactNode;
}): JSX.Element {
  return (
    <Group justify="space-between" mb="md">
      <Title order={3}>{title}</Title>
      {actions}
    </Group>
  );
}
