import { useState, type JSX } from 'react';
import { Badge, Group, Loader, Select, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { RegistryDefDto, RegistryEntryDto } from '@/api/types';
import { localized } from '@/shared/i18n/localized';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

/**
 * Конструктор реєстрів: схема, дані, темпоральність.
 *
 * ⚠ Вікно чинності показується завжди, навіть порожнє. Запис без вікна і
 * запис, чинний до минулого місяця, у списку виглядають однаково — і саме
 * друге робить рядки документів осиротілими (ФВ-8.13).
 */
export function RegistriesPage(): JSX.Element {
  const [code, setCode] = useState<string | null>(null);

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
            w={280}
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

      <ErrorAlert error={registries.error ?? entries.error} />

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

      {entries.isPending && code !== null ? (
        <Loader />
      ) : (
        code !== null && (
          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('registries.code')}</Table.Th>
                <Table.Th>{t('registries.name')}</Table.Th>
                <Table.Th>{t('registries.parent')}</Table.Th>
                <Table.Th>{t('registries.validity')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {(entries.data ?? []).map((entry) => (
                <Table.Tr key={entry.id}>
                  <Table.Td>{entry.code}</Table.Td>
                  <Table.Td>{entry.display}</Table.Td>
                  <Table.Td>{entry.parentEntryId ?? '—'}</Table.Td>
                  <Table.Td>
                    {(entry.validFrom ?? '…') + ' — ' + (entry.validTo ?? '…')}
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )
      )}
    </>
  );
}
