import type { JSX } from 'react';
import { Stack, Text, Title } from '@mantine/core';
import type { ApprovalRouteScope } from './approvalRoutesApi';
import { ScopedApprovalRouteEditor } from './ScopedApprovalRouteEditor';

/**
 * ✎ B155: розділ сторінки з маршрутом області — заголовок `h2`, пояснення під ним (`aria-describedby` розділу) і
 * редактор. Картка шаблону (`{kind:'template'}`) і сторінка безпеки (`{kind:'default'}`).
 */
export function ApprovalRouteSection({
  id,
  title,
  hint,
  scope,
}: {
  readonly id: string;
  readonly title: string;
  readonly hint: string;
  readonly scope: ApprovalRouteScope;
}): JSX.Element {
  const headingId = `approval-route-${id}`;
  const hintId = `${headingId}-hint`;

  return (
    <Stack
      component="section"
      gap="xs"
      aria-labelledby={headingId}
      aria-describedby={hintId}
      data-testid={`approval-route-section-${id}`}
      maw={880}
    >
      <Title order={2} size="h4" id={headingId}>
        {title}
      </Title>
      <Text size="sm" c="dimmed" id={hintId}>
        {hint}
      </Text>
      <ScopedApprovalRouteEditor scope={scope} />
    </Stack>
  );
}
