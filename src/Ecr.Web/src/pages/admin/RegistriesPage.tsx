import type { JSX } from 'react';
import { Badge, Group, Select, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { RegistryDefDto, RegistryEntryDto } from '@/api/types';
import { localized } from '@/shared/i18n/localized';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { useUrlState } from '@/shared/ui/useUrlState';
import { t } from '@/shared/i18n';

/**
 * Конструктор реєстрів: схема, дані, темпоральність.
 *
 * ⚠ Вікно чинності показується завжди, навіть порожнє. Запис без вікна і
 * запис, чинний до минулого місяця, у списку виглядають однаково — і саме
 * друге робить рядки документів осиротілими (ФВ-8.13).
 */
export function RegistriesPage(): JSX.Element {
  const [code, setCode] = useUrlState('code');

  const registries = useQuery({
    queryKey: ['registries'],
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
  });

  const entries = useQuery({
    queryKey: ['registry-entries', code],
    queryFn: () =>
      apiFetch<RegistryEntryDto[]>(
        `/api/v1/registries/${encodeURIComponent(code ?? '')}/entries`,
      ),
    enabled: code !== null,
  });

  const selected = registries.data?.find((registry) => registry.code === code);

  return (
    <>
      <PageHeader
        title={t('registries.title')}
        actions={
          <Select
            size="xs"
            miw={220}
            label={t('registries.title')}
            placeholder={t('registries.pick')}
            value={code}
            onChange={setCode}
            data={(registries.data ?? []).map((registry) => ({
              value: registry.code,
              label: `${localized(registry.nameL10n)} (${registry.code})`,
            }))}
          />
        }
      />

      {/*
       * ⛔ Помилка переліку довідників показується ОКРЕМО від помилки записів:
       * недоступний перелік лишає порожнім сам вибір, і мовчазна порожнеча в
       * ньому виглядає як «довідників немає».
       */}
      <AsyncBoundary<RegistryDefDto[]>
        isPending={registries.isPending}
        error={registries.error}
        data={registries.data}
        isEmpty={(all) => all.length === 0}
        emptyTitle={t('registries.empty')}
        emptyHint={t('registries.emptyHint')}
        onRetry={() => void registries.refetch()}
      >
        {() => null}
      </AsyncBoundary>

      {selected !== undefined && (
        <Group gap="xs" mb="sm">
          {/* ⚠ Ознаки довідника видно поруч із даними: у темпоральному
              запис має вікно чинності, і рядки документів, що на нього
              посилаються, стають осиротілими поза цим вікном (ФВ-8.13). */}
          {selected.isTemporal && <Badge variant="light">{t('registries.temporal')}</Badge>}
          {selected.isHierarchical && <Badge variant="light">{t('registries.hierarchical')}</Badge>}
          <Text size="xs" c="dimmed">
            {t('registries.fields', { count: selected.fields.length })}
          </Text>
        </Group>
      )}

      {/*
       * ⚠ Доки довідник не обрано, `data` — `undefined`, і обгортка показує
       * порожній стан із підказкою «оберіть довідник». Це не «даних немає»:
       * запиту ще не було, і сказати про це чесніше, ніж малювати порожню
       * таблицю з заголовками.
       */}
      <AsyncBoundary<RegistryEntryDto[]>
        isPending={code !== null && entries.isPending}
        error={entries.error}
        data={code === null ? undefined : entries.data}
        isEmpty={(all) => all.length === 0}
        emptyTitle={code === null ? t('registries.pick') : t('registries.noEntries')}
        emptyHint={code === null ? t('registries.pickHint') : t('registries.noEntriesHint')}
        skeleton="table"
        onRetry={() => void entries.refetch()}
      >
        {(all) => (
          <Table striped highlightOnHover className="ecr-sticky-head">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('registries.code')}</Table.Th>
                <Table.Th>{t('registries.name')}</Table.Th>
                <Table.Th>{t('registries.parent')}</Table.Th>
                <Table.Th>{t('registries.validity')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {all.map((entry) => (
                <Table.Tr key={entry.id}>
                  <Table.Td>{entry.code}</Table.Td>
                  <Table.Td>{entry.display}</Table.Td>
                  <Table.Td>{entry.parentEntryId ?? '—'}</Table.Td>
                  <Table.Td>{(entry.validFrom ?? '…') + ' — ' + (entry.validTo ?? '…')}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>
    </>
  );
}
