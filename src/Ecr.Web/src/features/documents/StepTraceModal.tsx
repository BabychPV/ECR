import type { JSX } from 'react';
import { Alert, Badge, Code, Group, Stack, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ResizableModal } from '@/shared/ui/ResizableModal';

/** Кроки рядка в актуальному прогоні (`D-565`). */
export type CalculationSteps = components['schemas']['CalculationStepsDto'];

/** Один крок рядка. */
export type CalculationStep = components['schemas']['CalculationStepDto'];

/** Що відкрити: рядок проблеми `StepError`. */
export interface StepTraceTarget {
  readonly documentId: number;
  readonly periodKey: number;
  readonly tableInstanceId: number;
  readonly rowKey: string;
  /** Підпис рядка в заголовку («Рядок 6 · вихід «Y»: #N/A»). */
  readonly label: string;
  /** Код виходу, що впав (з проблеми) — показується першим, якщо серед кроків немає явно невдалих. */
  readonly stepCode: string | null;
  readonly methodologyId: number | null;
}

/**
 * `GET /api/v1/documents/{id}/calculation-steps` — кроки рядка в актуальному прогоні (`D-565`). Право `Calculation.View`.
 *
 * Адреса записана поруч із викликом (так її шукає сторож споживачів дій сервера).
 */
export function getCalculationSteps(target: StepTraceTarget): Promise<CalculationSteps> {
  const query = new URLSearchParams({
    periodKey: String(target.periodKey),
    tableInstanceId: String(target.tableInstanceId),
    rowKey: target.rowKey,
  });

  return apiFetch<CalculationSteps>(
    `/api/v1/documents/${String(target.documentId)}/calculation-steps?${query.toString()}`,
  );
}

/** Кроки, які варто показати: ті, що впали; без таких — крок виходу з проблеми. */
export function stepsToShow(steps: readonly CalculationStep[], stepCode: string | null): readonly CalculationStep[] {
  const failed = steps.filter((step) => step.failed);
  if (failed.length > 0) return failed;

  return steps.filter((step) => stepCode !== null && step.stepCode.toLowerCase() === stepCode.toLowerCase());
}

/**
 * ✎ D-565 (PERF-03): «який крок упав і з якими входами» для проблеми рядка `StepError`.
 *
 * ⚠ Мінімальний перегляд, а не шухляда «Чому це число?» (FEATURE-HSE301-VIEW §10.3): кроки рядка з актуального прогону —
 * вираз, помилка й входи кроку. Кроків немає, бо прогін їх не записав, — чесне повідомлення про вимкнене трасування,
 * а не порожня таблиця.
 */
export function StepTraceModal({
  target,
  onClose,
}: {
  readonly target: StepTraceTarget | null;
  readonly onClose: () => void;
}): JSX.Element {
  const steps = useQuery({
    queryKey: [
      'document',
      target?.documentId,
      target?.periodKey,
      'calculation-steps',
      target?.tableInstanceId,
      target?.rowKey,
    ],
    queryFn: () => getCalculationSteps(target!),
    enabled: target !== null,
  });

  return (
    <ResizableModal
      opened={target !== null}
      onClose={onClose}
      title={target === null ? '' : t('document.stepTrace.title', { row: target.label })}
      size="lg"
      sizeKey="stepTrace"
    >
      <AsyncBoundary<CalculationSteps>
        isPending={steps.isPending}
        error={steps.error}
        data={steps.data}
        skeleton="table"
        onRetry={() => void steps.refetch()}
      >
        {(data) => {
          const shown = stepsToShow(data.steps, target?.stepCode ?? null);
          const notRecorded =
            target?.methodologyId !== null &&
            target?.methodologyId !== undefined &&
            (data.notRecordedMethodologyIds ?? []).includes(target.methodologyId);

          if (shown.length === 0) {
            return (
              <Alert color="statusInfo" data-testid="step-trace-none">
                {t(notRecorded ? 'document.stepTrace.notRecorded' : 'document.stepTrace.noSteps')}
              </Alert>
            );
          }

          return (
            <Stack gap="md" data-testid="step-trace-steps">
              {shown.map((step) => (
                <Stack key={step.id} gap="xs" data-testid="step-trace-step">
                  <Group gap="xs">
                    <Text fw={600} size="sm">
                      {step.stepCode}
                    </Text>
                    {step.error !== null && step.error !== undefined && (
                      <Badge color={step.failed ? 'statusError' : 'gray'} variant="light">
                        {step.error}
                      </Badge>
                    )}
                    {step.value !== null && step.value !== undefined && (
                      <Text size="sm" c="dimmed">
                        = {step.value}
                      </Text>
                    )}
                  </Group>
                  {step.expression !== null && step.expression !== undefined && (
                    <Code block data-testid="step-trace-expression">
                      {step.expression}
                    </Code>
                  )}
                  {step.inputs.length > 0 ? (
                    <Table withTableBorder striped data-testid="step-trace-inputs">
                      <Table.Thead>
                        <Table.Tr>
                          <Table.Th>{t('document.stepTrace.input')}</Table.Th>
                          <Table.Th>{t('document.stepTrace.value')}</Table.Th>
                          <Table.Th>{t('document.stepTrace.unit')}</Table.Th>
                        </Table.Tr>
                      </Table.Thead>
                      <Table.Tbody>
                        {step.inputs.map((input, index) => (
                          <Table.Tr key={`${input.kind}:${input.code}:${String(index)}`}>
                            <Table.Td>
                              <Text size="sm" ff="monospace">
                                {input.code}
                              </Text>
                            </Table.Td>
                            <Table.Td>{input.value ?? '—'}</Table.Td>
                            <Table.Td>{input.unit ?? ''}</Table.Td>
                          </Table.Tr>
                        ))}
                      </Table.Tbody>
                    </Table>
                  ) : (
                    <Text size="xs" c="dimmed">
                      {t('document.stepTrace.noInputs')}
                    </Text>
                  )}
                </Stack>
              ))}
              {data.truncated && (
                <Text size="xs" c="dimmed">
                  {t('document.stepTrace.truncated')}
                </Text>
              )}
            </Stack>
          );
        }}
      </AsyncBoundary>
    </ResizableModal>
  );
}
