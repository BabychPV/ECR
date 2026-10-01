import type { JSX } from 'react';
import { Alert, Code, Skeleton, Stack, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import type { CalculationResultDto, RegistryDefDto, UnitRef } from '@/api/types';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import { formatDecimal } from '@/shared/format';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { calculationResults } from './api';
import { calculationResultsKey } from './calculationResultsKey';

/**
 * Коди довідників, змінених після прогону (RT-25), без повторів і впорядковані.
 *
 * ⚠ Сервер кладе той самий перелік у кожен рядок (свіжість — на документ, не на число), тож
 * без зведення банер повторив би кожен довідник стільки разів, скільки чисел у панелі.
 */
export function changedRegistriesOf(list: readonly CalculationResultDto[]): string[] {
  return [...new Set(list.flatMap((result) => result.changedRegistries ?? []))].sort();
}

/**
 * Назва довідника для банера застарілості: назва мовою користувача з каталогу довідників, а код —
 * лише запасний варіант (каталог ще їде, недоступний без права або довідника там немає).
 */
export function registryDisplayName(code: string, registries: readonly RegistryDefDto[] | undefined): string {
  const name = localized(registries?.find((registry) => registry.code === code)?.nameL10n);
  return name === '' ? code : name;
}

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
  // ⛔ F-02 (четвертий раунд UX): ключ — ПІД префіксом `['document', id,
  // period]`. Саме його інвалідує завершений перерахунок (`SheetActions`) і
  // кожна зміна робочого процесу; доти панель жила під окремим ключем, і після
  // перерахунку показувала старі числа до перезавантаження сторінки.
  const results = useQuery({
    queryKey: calculationResultsKey(documentId, periodKey),
    queryFn: () => calculationResults(documentId, periodKey),
  });

  // ⚠ Каталог довідників — лише коли банеру є кого називати, і під спільним ключем переліку
  // (його вже читають сітка й шапка документа), щоб не множити запитів. Відмова (немає
  // `Registry.View`) — не помилка панелі: банер покаже код.
  const changedCodes = changedRegistriesOf(results.data ?? []);
  const registries = useQuery({
    queryKey: queryKeys.registries.list(),
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
    enabled: changedCodes.length > 0,
    retry: false,
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
          <Stack gap="xs">
            {/*
              ⛔ Директива D15 §0, правило L10. Одиниця бралася як
              `(units.data ?? []).find(…)?.code ?? result.unitId`, тож при
              відмові `GET /api/v1/units` у колонці одиниці друкувалося ГОЛЕ
              ЧИСЛО — ідентифікатор поруч із порахованим значенням. Це гірше
              за порожню колонку: число читається як одиниця («7»), і звірка
              показника йде не в тій розмірності. Для екрана, який існує саме
              щоб ДОВЕСТИ, що методологія порахувала те саме, двозначність тут
              коштує найдорожче.
            */}
            {/*
              ⛔ F-05: входи змінилися після прогону — числа вже не відповідають
              даним. Доти панель показувала їх як чинні, і документ подавали з
              результатами, що рахували інші входи.
            */}
            {/*
              ⛔ RT-25: причиною застарілості буває не лише введення, а й правка довідника,
              який читає методологія (`changedRegistries`). Без назви довідника людина шукала
              б зміну в документі, якої там немає.
            */}
            {(list.some((result) => result.isStale) || changedRegistriesOf(list).length > 0) && (
              <Alert color="statusWarning" data-results-stale="" title={t('documents.calculationResultsStale')}>
                {t('documents.calculationResultsStaleHint')}
                {changedRegistriesOf(list).map((code) => (
                  <Text key={code} size="sm" mt="xs" data-results-stale-registry={code}>
                    {t('calculation.staleRegistry', { name: registryDisplayName(code, registries.data) })}
                  </Text>
                ))}
              </Alert>
            )}

            {units.error !== null && (
              <ErrorAlert error={units.error} onRetry={() => void units.refetch()} />
            )}

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
                    {/* ⚠ F-21: без 16 хвостових нулів сховища (`2.5000000000000000`). */}
                    <Table.Td>{formatDecimal(result.value) ?? String(result.value)}</Table.Td>
                    <Table.Td>
                      {/*
                        ⚠ Три різні стани, а не один: довідник ще їде (місце
                        тримає скелет), довідник є і код знайшовся (код), і
                        довідник є, але такого `unitId` у ньому немає — тоді
                        ідентифікатор показано ЯК ІДЕНТИФІКАТОР (`<Code>`), а
                        не текстом, який можна сплутати з позначкою одиниці.
                      */}
                      {units.isPending ? (
                        <Skeleton height={12} width={40} radius="sm" data-units="pending" />
                      ) : (
                        (units.data?.find((u) => u.id === result.unitId)?.code ?? (
                          <Code>{result.unitId}</Code>
                        ))
                      )}
                    </Table.Td>
                    {/* ⚠ Версія методології поруч із числом обов'язкова: без
                        неї результат неможливо ані пояснити, ані відтворити. */}
                    {/* ⚠ F-21: номер версії й код методології, а не ідентифікатор. */}
                    <Table.Td>
                      {result.methodologyVersion === null || result.methodologyVersion === undefined
                        ? String(result.methodologyVersionId)
                        : `${result.methodologyCode ?? ''} ${result.methodologyVersion}`.trim()}
                    </Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Stack>
        )}
      </AsyncBoundary>
    </Stack>
  );
}
