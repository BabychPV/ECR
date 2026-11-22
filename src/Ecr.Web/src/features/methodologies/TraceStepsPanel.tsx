import type { JSX } from 'react';
import { Alert, Badge, Stack, Switch, Text } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { MethodologyTraceStepsDto } from '@/api/types';
import { queryKeys } from '@/api/queryKeys';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { showApiError, showDone } from '@/shared/ui/notify';
import { methodologyTraceSteps, saveMethodologyTraceSteps } from './api';
import { MethodologySection } from './MethodologySection';

/**
 * Прапор методології «трасувати проміжні кроки» (`D-565`, аудит PERF-03).
 *
 * ⛔ Вимкнено за замовчуванням, і екран мусить це КАЗАТИ, а не мовчати: доки прапор вимкнено, прогони методології не
 * пишуть кроків трейсу (`calc.CalculationStep`), тож пояснення «як пораховано» для чисел цього часу немає. Числа й
 * входи від прапора не залежать — тому він на методології, а не на версії, і вмикається без чернетки й публікації.
 *
 * ⚠ Право — `Calculation.EditFormula` (те, що править методології); без нього перемикач лише показує стан.
 */
export function MethodologyTraceStepsPanel({
  methodologyId,
  editable,
}: {
  readonly methodologyId: number;
  readonly editable: boolean;
}): JSX.Element {
  const queryClient = useQueryClient();
  const traceSteps = useQuery({
    queryKey: queryKeys.methodologies.traceSteps(methodologyId),
    queryFn: () => methodologyTraceSteps(methodologyId),
  });

  const save = useMutation({
    mutationFn: (enabled: boolean) => saveMethodologyTraceSteps(methodologyId, { enabled }),
    onSuccess: (saved) => {
      queryClient.setQueryData(queryKeys.methodologies.traceSteps(methodologyId), saved);
      showDone(t(saved.traceSteps ? 'methodologies.traceSteps.savedOn' : 'methodologies.traceSteps.savedOff'));
    },
    onError: showApiError,
  });

  const enabled = traceSteps.data?.traceSteps;

  return (
    <MethodologySection
      sectionId="traceSteps"
      title={t('methodologies.traceSteps.title')}
      defaultExpanded={false}
      actions={
        enabled !== undefined && (
          // ⚠ Стан видно й у згорнутій секції: «вимкнено» — найчастіша відповідь на «чому немає кроків».
          // ⚠ «Вимкнено» — outline, а не light: сірий light на фоні заголовка секції не проходив axe color-contrast (e2e).
          <Badge variant={enabled ? 'light' : 'outline'} color={enabled ? 'statusInfo' : 'gray'} data-testid="trace-steps-state">
            {t(enabled ? 'methodologies.traceSteps.on' : 'methodologies.traceSteps.off')}
          </Badge>
        )
      }
    >
      <AsyncBoundary<MethodologyTraceStepsDto>
        isPending={traceSteps.isPending}
        error={traceSteps.error}
        data={traceSteps.data}
        skeleton="table"
        onRetry={() => void traceSteps.refetch()}
      >
        {/* ⚠ Порожня відповідь (null) — як «вимкнено», а не TypeError: без цього межа помилок повторювала рендер нескінченно. */}
        {(state) => (
          <Stack gap="sm">
            <Text size="sm" c="dimmed">
              {t('methodologies.traceSteps.hint')}
            </Text>

            <Switch
              label={t('methodologies.traceSteps.label')}
              checked={state?.traceSteps === true}
              disabled={!editable || save.isPending}
              onChange={(event) => {
                if (save.isPending) return;
                save.mutate(event.currentTarget.checked);
              }}
            />

            {state?.traceSteps !== true && (
              <Alert color="statusInfo" data-testid="trace-steps-off-note">
                {t('methodologies.traceSteps.offNote')}
              </Alert>
            )}

            {!editable && (
              <Text size="xs" c="dimmed">
                {t('methodologies.traceSteps.readOnly')}
              </Text>
            )}
          </Stack>
        )}
      </AsyncBoundary>
    </MethodologySection>
  );
}
