import type { JSX, ReactNode } from 'react';
import { Badge, Group, Stack, Text, UnstyledButton } from '@mantine/core';
import { useSectionCollapsed } from './myTasksCollapse';

/**
 * ✎ UAT my-tasks-collapse: блок шухляди «My tasks», що згортається.
 *
 * Заголовок — справжня кнопка (`aria-expanded`, `aria-controls`): Enter і Space працюють нативно, фокус лишається на
 * ній. Тіло не демонтується, а ховається (`hidden`) — стан рядків (розгорнута причина, група місяців) не губиться.
 * Назва регіону — лише текст заголовка (`aria-labelledby` на ньому, без лічильника).
 */
export function MyTasksSection({
  id,
  title,
  count,
  children,
}: {
  /** Ідентифікатор блоку: `data-testid`, основа id заголовка й тіла, ключ сховища. */
  readonly id: string;
  readonly title: string;
  /** Видиме число елементів («50+», коли є ще сторінки). */
  readonly count: number | string;
  readonly children: ReactNode;
}): JSX.Element {
  const [collapsed, toggle] = useSectionCollapsed(id);
  const bodyId = `${id}-body`;

  return (
    <Stack gap="xs" mb="md" component="section" aria-labelledby={`${id}-title`} data-testid={id}>
      <UnstyledButton
        onClick={toggle}
        aria-expanded={!collapsed}
        aria-controls={bodyId}
        data-section-toggle={id}
      >
        <Group gap="xs" wrap="nowrap">
          <Chevron collapsed={collapsed} />
          <Text span id={`${id}-title`} fw={600} size="sm">
            {title}
          </Text>
          <Badge size="sm" variant="light" color="gray" data-section-count="">
            {count}
          </Badge>
        </Group>
      </UnstyledButton>
      <div id={bodyId} hidden={collapsed}>
        <Stack gap="xs">{children}</Stack>
      </div>
    </Stack>
  );
}

function Chevron({ collapsed }: { readonly collapsed: boolean }): JSX.Element {
  return (
    <svg
      width={14}
      height={14}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={2}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
      style={{ transform: collapsed ? 'rotate(-90deg)' : undefined, flexShrink: 0 }}
    >
      <path d="m6 9 6 6 6-6" />
    </svg>
  );
}
