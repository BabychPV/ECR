import { useId, type JSX, type ReactNode } from 'react';
import { Alert, Group, Paper, Stack, Text, Title } from '@mantine/core';
import type { PipelineStep } from '@/features/pipeline/pipelineSteps';
import { PipelineStepBadge } from '@/features/pipeline/PipelineStepBadge';
import { formatNumber } from '@/shared/format/number';
import { t } from '@/shared/i18n';

/**
 * Рамка одного кроку конвеєра: номер, назва, що робить крок, скільки
 * реальних точок із нього виходить і власний вміст кроку (перегляд чи
 * редагування наявним компонентом).
 *
 * ⛔ Крок, що звузив набір до нуля, підсвічується рамкою І текстом причини:
 * самого кольору недостатньо (`ФВ-14.16`, колір не єдиний носій змісту).
 */
export function PipelineStepCard({
  step,
  index,
  title,
  hint,
  zeroHint,
  children,
}: {
  readonly step: PipelineStep;
  readonly index: number;
  readonly title: string;
  readonly hint: string;

  /** Що перевірити, якщо набір звузився до нуля саме тут. */
  readonly zeroHint?: string | undefined;
  readonly children?: ReactNode;
}): JSX.Element {
  const narrowed = step.state === 'zero';

  // ⚠ `<section>` без імені не є орієнтиром: читач не пропонує його в
  // переліку областей, і п'ять кроків зливаються в один потік тексту.
  const titleId = useId();

  return (
    <Paper
      component="section"
      aria-labelledby={titleId}
      withBorder
      p="md"
      data-step={step.key}
      data-narrowed={String(narrowed)}
      bd={narrowed ? '2px solid var(--ecr-danger)' : undefined}
    >
      <Stack gap="sm">
        <Group justify="space-between" wrap="nowrap" align="start">
          <Stack gap="xs">
            <Title order={2} size="h4" id={titleId}>
              {index}. {title}
            </Title>
            <Text size="sm" c="dimmed">
              {hint}
            </Text>
          </Stack>

          <Group gap="xs" wrap="nowrap">
            {step.points !== null && (
              <Text size="sm" fw={600} data-step-points={step.points}>
                {t('pipeline.points', { points: formatNumber(step.points) })}
              </Text>
            )}
            <PipelineStepBadge state={step.state} />
          </Group>
        </Group>

        {narrowed && zeroHint !== undefined && (
          <Alert color="statusError" title={t('pipeline.narrowedTitle')}>
            {zeroHint}
          </Alert>
        )}

        {children}
      </Stack>
    </Paper>
  );
}
