import { useState, type JSX } from 'react';
import { Badge, Group, Loader, Select, Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { t } from '@/shared/i18n';

interface RegistryDef {
  id: number;
  code: string;
  name: string;
  /** Хто master для цих даних: `External`, `Hybrid`, `Ecr` (ФВ-8.9). */
  sourceKind: string;
  dataRevision: number;
}

interface RegistryEntry {
  id: number;
  code: string;
  name: string;
  parentEntryId: number | null;
  validFrom: string | null;
  validTo: string | null;
  isActive: boolean;
}

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
    queryFn: () => apiFetch<RegistryDef[]>('/api/v1/registries'),
  });

  const entries = useQuery({
    queryKey: ['registry-entries', code],
    queryFn: () =>
      apiFetch<{ items: RegistryEntry[] }>(
        `/api/v1/registries/${encodeURIComponent(code ?? '')}/entries?limit=200`,
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
              label: `${registry.name} (${registry.code})`,
            }))}
          />
        }
      />

      <ErrorAlert error={registries.error ?? entries.error} />

      {selected !== undefined && (
        <Group gap="xs" mb="sm">
          {/* ⚠ SourceKind видно поруч із даними: у реєстрі, де master —
              зовнішня система, правка руками або заборонена, або буде затерта
              наступним збором. Без цієї позначки це виглядало б як зникнення
              роботи. */}
          <Badge variant="light">{selected.sourceKind}</Badge>
          <Text size="xs" c="dimmed">
            rev {selected.dataRevision}
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
              {(entries.data?.items ?? []).map((entry) => (
                <Table.Tr key={entry.id} opacity={entry.isActive ? 1 : 0.5}>
                  <Table.Td>{entry.code}</Table.Td>
                  <Table.Td>{entry.name}</Table.Td>
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
