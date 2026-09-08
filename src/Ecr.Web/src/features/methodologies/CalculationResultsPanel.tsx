import type { JSX } from 'react';
import { Stack, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import type { CalculationResultDto, UnitRef } from '@/api/types';
import { apiFetch } from '@/api/client';
import { t } from '@/shared/i18n';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { calculationResults } from './api';

/**
 * Числа, які дав розрахунок методологій на цьому документі за цей період.
 *
 * ⛔ Окрема панель, а не колонка в сітці, і це `D-69`: результат методології
 * **не** потрапляє в `doc.CellValue` — інакше нічний перерахунок писав би
 * десятки мільйонів рядків у партиції документів і роздував журнал змін
 * комірок. У документ він приходить посиланням через `cfg.CalculationBinding`.
 *
 * ⛔ Доти побачити це число не було де ВЗАГАЛІ. Перерахунок завершувався
 * успіхом, значення лягало в `calc.CalculationResult` — і жоден екран його не
 * показував: єдиним способом переконатися, що методологія порахувала саме те,
 * лишався `SELECT` у базі.
 *
 * ⚠ Показуються числа АКТУАЛЬНОГО прогону, не останнього за часом: прогін, що
 * впав, лишає по собі частину рядків, і суміш двох версій методології на
 * екрані виглядала б цілком правдоподібно.
 */
export function CalculationResultsPanel({
  documentId,
  periodKey,
}: {
  readonly documentId: number;
  readonly periodKey: number;
}): JSX.Element {
  const results = useQuery({
    queryKey: ['calculation-results', documentId, periodKey],
    queryFn: () => calculationResults(documentId, periodKey),
  });

  const units = useQuery({
    queryKey: ['units'],
    queryFn: () => apiFetch<UnitRef[]>('/api/v1/units'),
    staleTime: 60 * 60 * 1000,
  });

  return (
    <Stack gap="xs">
      <Text fw={600}>{t('documents.calculationResults')}</Text>

      <AsyncBoundary<CalculationResultDto[]>
        isPending={results.isPending}
        error={results.error}
        data={results.data}
        isEmpty={(list) => list.length === 0}
        emptyTitle={t('documents.noCalculationResults')}
        emptyHint={t('documents.noCalculationResultsHint')}
        skeleton="table"
        onRetry={() => void results.refetch()}
      >
        {(list) => (
          <Table striped withTableBorder>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('documents.rowKey')}</Table.Th>
                <Table.Th>{t('documents.outputCode')}</Table.Th>
                <Table.Th>{t('documents.value')}</Table.Th>
                <Table.Th>{t('methodologies.outputUnit')}</Table.Th>
                <Table.Th>{t('methodologies.version')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {list.map((result) => (
                <Table.Tr
                  key={`${String(result.sourceRowKey)}:${result.outputCode}:${String(result.substanceEntryId)}`}
                >
                  <Table.Td>{result.sourceRowKey ?? '—'}</Table.Td>
                  <Table.Td>{result.outputCode}</Table.Td>
                  <Table.Td>{result.value}</Table.Td>
                  <Table.Td>
                    {(units.data ?? []).find((u) => u.id === result.unitId)?.code ?? result.unitId}
                  </Table.Td>
                  {/* ⚠ Версія методології поруч із числом обов'язкова: без неї
                      результат неможливо ані пояснити, ані відтворити. */}
                  <Table.Td>{result.methodologyVersionId}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>
    </Stack>
  );
}
