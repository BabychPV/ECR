import { Suspense, lazy, useState, type JSX } from 'react';
import { Button, Group, Stack, Table, Tabs, Text } from '@mantine/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import type { RegistryDefDto } from '@/api/types';
import { getRegistryRows, type RegistryRow } from '@/features/registries/rows/api';
import { RegistryEntryEditor, ValidityEditor } from '@/features/registries/RegistryEntryEditor';
import { formatDateOnly } from '@/shared/format';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { DetailDrawer } from '@/shared/ui/DetailDrawer';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { KeyValue } from '@/shared/ui/KeyValue';
import { rowsKey } from './data';
import type { RegistryField } from './rowModel';

const DateInput = lazy(async () => ({ default: (await import('@/shared/dates/DateInputWithStyles')).DateInput }));

/** Журнал змін запису (RT-15) — окремим чанком, лише коли відкрили вкладку «Історія». */
const EntryHistoryLog = lazy(() => import('./EntryHistoryLog'));

/** Значення шторки `?panel=` для запису. */
export const entryPanelId = (id: number): string => `entry-${String(id)}`;

/**
 * Кінець обраного дня за місцевим часом як момент UTC — `asOfUtc` для «значень станом на».
 *
 * ⚠ Кінець дня, а не північ: «станом на 27 вересня» читається як «якими вони були того дня»,
 * тобто з усіма правками, зробленими 27-го.
 */
export function endOfLocalDayUtc(day: Date): string {
  return new Date(day.getFullYear(), day.getMonth(), day.getDate(), 23, 59, 59, 999).toISOString();
}

export interface EntryDrawerProps {
  readonly registry: RegistryDefDto;
  readonly row: RegistryRow;
  readonly fields: readonly RegistryField[];
  readonly asOf: string | null;
  readonly readOnly: boolean;
}

/**
 * Шторка запису (§8.5): подробиці, журнал змін і значення «станом на» момент системного часу.
 *
 * Вкладка «Історія» — журнал змін (хто, коли, що, «було» → «стало»; `GET …/entries/{id}/history`,
 * RT-15), а під ним — значення запису станом на обраний день (`GET …/rows?asOfUtc=`) поруч із
 * поточними.
 */
export function EntryDrawer({ registry, row, fields, asOf, readOnly }: EntryDrawerProps): JSX.Element {
  const queryClient = useQueryClient();
  // D-212: нетемпоральний довідник не має календарної чинності — полів дат і дії «Чинність» немає.
  const temporal = registry.isTemporal || asOf !== null;
  const [day, setDay] = useState<Date | null>(null);
  const [editing, setEditing] = useState<'name' | 'validity' | null>(null);
  const [tab, setTab] = useState<string | null>('details');
  const asOfUtc = day === null ? null : endOfLocalDayUtc(day);

  // ⛔ Точний фільтр `id`, а не `q: row.code`: `q` — підрядок коду, назви й текстових полів із
  // лімітом, і короткий код (`N2`, `C1`) губився за 50 чужими рядками з меншим Id — шторка
  // казала «запису ще не було», що неправда (L9-08).
  const past = useQuery({
    queryKey: rowsKey(registry.code, asOf, `#${String(row.id)}`, asOfUtc),
    queryFn: () =>
      getRegistryRows(registry.code, { ...(asOf ? { asOf } : {}), ids: [row.id], ...(asOfUtc ? { asOfUtc } : {}), limit: 1 }),
    enabled: asOfUtc !== null,
  });
  const then = past.data?.items.find((item) => item.id === row.id);

  const refresh = (): void => {
    setEditing(null);
    void queryClient.invalidateQueries({ queryKey: ['registries'] });
  };

  return (
    <DetailDrawer
      panelId={entryPanelId(row.id)}
      title={row.display}
      subtitle={row.code}
      closeLabel={t('common.close')}
      size="lg"
    >
      <Tabs value={tab} onChange={setTab}>
        <Tabs.List>
          <Tabs.Tab value="details">{t('registries.data.details')}</Tabs.Tab>
          <Tabs.Tab value="history">{t('registries.tabHistory')}</Tabs.Tab>
        </Tabs.List>

        <Tabs.Panel value="details" pt="md">
          <Stack gap="md">
            <KeyValue
              items={[
                { label: t('registries.code'), value: row.code, mono: true },
                { label: t('registries.name'), value: row.display },
                ...(temporal
                  ? [
                      { label: t('registries.validFrom'), value: row.validFrom ?? '' },
                      { label: t('registries.validTo'), value: row.validTo ?? '' },
                    ]
                  : []),
                ...fields.map((field) => ({
                  label: localized(field.nameL10n) || field.code,
                  value: row.values[field.code]?.display ?? row.values[field.code]?.value ?? '',
                })),
              ]}
            />
            {!readOnly && (
              <Group gap="xs">
                <Button size="xs" variant="default" onClick={() => setEditing('name')}>
                  {t('registries.editEntry')}
                </Button>
                {temporal && (
                  <Button size="xs" variant="default" onClick={() => setEditing('validity')}>
                    {t('registries.validity')}
                  </Button>
                )}
              </Group>
            )}
          </Stack>
        </Tabs.Panel>

        <Tabs.Panel value="history" pt="md">
          <Stack gap="sm">
            <Text size="sm" fw={600}>{t('registries.entryHistory.title')}</Text>
            {/* ⚠ Лише на відкритій вкладці: `Tabs` тримає панелі змонтованими, і без умови чанк і запит
                журналу йшли б на кожне відкриття шторки. */}
            {tab === 'history' && (
              <Suspense fallback={<Text size="sm" c="dimmed">{t('common.loading')}</Text>}>
                <EntryHistoryLog registryCode={registry.code} entryId={row.id} fields={fields} />
              </Suspense>
            )}

            <Suspense fallback={null}>
              <DateInput
                size="xs"
                label={t('registries.data.valuesAsOf')}
                description={t('registries.data.valuesAsOfHint')}
                clearable
                value={day}
                onChange={setDay}
                maxDate={new Date()}
              />
            </Suspense>

            {past.error !== null && <ErrorAlert error={past.error} />}
            {asOfUtc !== null && past.isSuccess && then === undefined && (
              <Text size="sm" c="dimmed">
                {t('registries.data.notYetThen', { date: formatDateOnly(day) ?? '' })}
              </Text>
            )}
            {then !== undefined && (
              <Table withTableBorder aria-label={t('registries.data.valuesAsOf')}>
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>{t('registries.field')}</Table.Th>
                    <Table.Th>{formatDateOnly(day)}</Table.Th>
                    <Table.Th>{t('registries.data.now')}</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {fields.map((field) => {
                    const before = shown(then, field.code);
                    const now = shown(row, field.code);
                    const changed = before !== now;
                    return (
                      <Table.Tr key={field.code} data-changed={changed ? 'true' : undefined}>
                        <Table.Td>{localized(field.nameL10n) || field.code}</Table.Td>
                        <Table.Td>{before}</Table.Td>
                        <Table.Td fw={changed ? 600 : undefined}>
                          {now}
                          {changed && <Text span size="xs" c="dimmed">{` · ${t('registries.data.changedSince')}`}</Text>}
                        </Table.Td>
                      </Table.Tr>
                    );
                  })}
                </Table.Tbody>
              </Table>
            )}
          </Stack>
        </Tabs.Panel>
      </Tabs>

      <RegistryEntryEditor registry={registry} entry={row} opened={editing === 'name'} onClose={refresh} />
      <ValidityEditor registryCode={registry.code} entry={editing === 'validity' ? row : null} onClose={refresh} />
    </DetailDrawer>
  );
}

function shown(row: RegistryRow, field: string): string {
  const value = row.values[field];
  return value?.display ?? value?.value ?? '';
}
