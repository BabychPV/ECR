import type { JSX } from 'react';
import { Badge, Code, Group, Stack, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import type { CalculationBindingDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { Banner } from '@/shared/ui/Banner';
import { toneFills } from '@/shared/ui/StatusBadge';
import { methodologyCoverage, type MethodologyCoverageDto } from './api';

/**
 * Позначка «нікуди не пише» — локальний ґап-бейдж, той самий прийом, що
 * `StateBadge` у `RuleCoveragePanel.tsx`: стан «вихід без активних прив'язок»
 * — властивість ЦЬОГО екрана, не стан сутності з набору (`ФВ-14.22`), тож
 * запису в `StatusBadge` для нього немає — беруться готові токени тону
 * (`toneFills.danger`), а не власний колір (`ФВ-14.11`).
 */
function NowhereBadge(): JSX.Element {
  const fill = toneFills.danger;

  return (
    <Badge
      size="sm"
      miw="fit-content"
      variant="default"
      bg={fill.bg}
      c={fill.text}
      data-coverage-gap="true"
    >
      {t('methodologies.outputCoverageNowhere')}
    </Badge>
  );
}

/** Куди лягають активні прив'язки одного виходу — код `таблиця.колонка`. */
function BindingList({
  bindings,
}: {
  readonly bindings: readonly CalculationBindingDto[];
}): JSX.Element {
  return (
    <Group gap="xs">
      {bindings.map((binding) => (
        <Code key={binding.id}>{`${String(binding.tableDefId)}.${String(binding.columnDefId)}`}</Code>
      ))}
    </Group>
  );
}

/**
 * Панель покриття версії «виходи → колонки» (`BE-25`).
 *
 * ⛔ Це НЕ те саме, що `MethodologyRuleCoveragePanel` (`ФВ-13.4`, `ФВ-13.9`,
 * `RuleCoveragePanel.tsx`) — та матриця відповідає на питання «що взагалі
 * буде порахованим» (рядки реальних даних × правила). Ця відповідає на інше:
 * «куди лягає порахований вихід». Для кожного оголошеного виходу версії —
 * перелік його АКТИВНИХ прив'язок; порожній перелік означає, що вихід
 * рахується, але не лягає нікуди (`L10`, ґап-стан). Окремо — активні
 * прив'язки, чий `outputCode` ця версія НЕ оголошує (`waitingBindings`):
 * колонка чекає на число, якого не буде ніколи, доки виходу не заведуть.
 *
 * ⚠ Права не питає: як і `MethodologyRuleCoveragePanel`, вона нічого не
 * змінює, а `Calculation.View` уже є в кожного, хто дійшов до цього екрана
 * (перелік методологій, з якого сюди переходять, вимагає його сам).
 */
export function MethodologyCoveragePanel({
  methodologyId,
  versionId,
}: {
  readonly methodologyId: number;
  readonly versionId: number;
}): JSX.Element {
  const coverage = useQuery({
    queryKey: ['methodologies', 'coverage', methodologyId, versionId],
    queryFn: () => methodologyCoverage(methodologyId, versionId),
  });

  return (
    <>
      <Text fw={600}>{t('methodologies.outputCoverage')}</Text>
      <Text size="sm" c="dimmed">
        {t('methodologies.outputCoverageHint')}
      </Text>

      <AsyncBoundary<MethodologyCoverageDto>
        isPending={coverage.isPending}
        error={coverage.error}
        data={coverage.data}
        isEmpty={(dto) => dto.outputs.length === 0 && dto.waitingBindings.length === 0}
        emptyTitle={t('methodologies.noOutputCoverage')}
        emptyHint={t('methodologies.noOutputCoverageHint')}
        skeleton="table"
        onRetry={() => void coverage.refetch()}
      >
        {(dto) => (
          <Stack gap="md">
            {dto.outputs.length > 0 && (
              <Table striped withTableBorder>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('methodologies.outputCode')}</Table.Th>
                    <Table.Th>{t('methodologies.outputCoverageBindings')}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {dto.outputs.map((output) => (
                    <Table.Tr key={output.code} data-output-row={output.code}>
                      <Table.Td>
                        <Code>{output.code}</Code>
                      </Table.Td>
                      <Table.Td>
                        {output.bindings.length === 0 ? (
                          <NowhereBadge />
                        ) : (
                          <BindingList bindings={output.bindings} />
                        )}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}

            {/* ⛔ Блок МАЛЮЄТЬСЯ лише коли є що показати (`D15-06`): порожній
                `waitingBindings` означає, що всі активні прив'язки лягають на
                оголошені виходи, — нормальний стан, а не прогалина, про яку
                варто попереджати смугою. */}
            {dto.waitingBindings.length > 0 && (
              <Stack gap="xs" data-testid="coverage-waiting">
                <Banner
                  tone="warning"
                  testId="coverage-waiting-banner"
                  title={t('methodologies.outputCoverageWaiting')}
                  text={t('methodologies.outputCoverageWaitingHint')}
                />

                <Table striped withTableBorder>
                  <Table.Thead>
                    <Table.Tr>
                      <Table.Th>{t('methodologies.outputCode')}</Table.Th>
                      <Table.Th>{t('methodologies.tableDefId')}</Table.Th>
                      <Table.Th>{t('methodologies.columnDefId')}</Table.Th>
                    </Table.Tr>
                  </Table.Thead>
                  <Table.Tbody>
                    {dto.waitingBindings.map((binding) => (
                      <Table.Tr key={binding.id} data-waiting-row={binding.outputCode}>
                        <Table.Td>
                          <Code>{binding.outputCode}</Code>
                        </Table.Td>
                        <Table.Td>{binding.tableDefId}</Table.Td>
                        <Table.Td>{binding.columnDefId}</Table.Td>
                      </Table.Tr>
                    ))}
                  </Table.Tbody>
                </Table>
              </Stack>
            )}
          </Stack>
        )}
      </AsyncBoundary>
    </>
  );
}
