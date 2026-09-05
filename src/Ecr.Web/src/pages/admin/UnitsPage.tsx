import { useState, type JSX } from 'react';
import { Badge, Button, Group, NumberInput, Select, Table, Text } from '@mantine/core';
import { useMutation, useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { ConvertUnitRequest, ConvertUnitResponse, UnitRef } from '@/api/types';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { showApiError } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';

/**
 * Довідник одиниць і конвертор (`ФВ-16.1`, `ФВ-16.2`, `ФВ-16.5`).
 *
 * ⛔ Ані переліку, ані конвертора в інтерфейсі не було. `GET /api/v1/units` не
 * викликав ніхто, а `POST /api/v1/units/convert` стояв у переліку звільнень
 * сторожа з поясненням «числа конвертуються там, де їх вводять» — і це було
 * **неправдою**: жодне місце клієнта нічого не конвертувало. Звільнення, яке
 * стверджує неіснуючу поведінку, гірше за відсутність кнопки: воно закриває
 * питання замість відповіді.
 *
 * ⚠ Питання «в яких одиницях можна писати формулу» виникає щоразу, коли її
 * пишуть, і відповіді на нього не було ніде, крім SQL по `uom.Unit`.
 *
 * ⛔ Конверсія можлива **лише в межах однієї розмірності**. Перехід
 * «маса ↔ об'єм» — не конверсія, а контекстний коефіцієнт (щільність), і він
 * живе в константах методології (`ФВ-16.5`, `D-75`). Тому вибір цільової
 * одиниці звужений до тієї самої розмірності: пропонувати перехід, який
 * сервер відхилить, означало б обіцяти неможливе.
 */
export function UnitsPage(): JSX.Element {
  const [value, setValue] = useState(1);
  const [fromUnit, setFromUnit] = useState<string | null>(null);
  const [toUnit, setToUnit] = useState<string | null>(null);
  const [result, setResult] = useState<ConvertUnitResponse | null>(null);

  const units = useQuery({
    queryKey: ['units'],
    queryFn: () => apiFetch<UnitRef[]>('/api/v1/units'),

    // Довідник одиниць міняється раз на роки — новою розмірністю, не правкою.
    staleTime: 60 * 60 * 1000,
  });

  const all = units.data ?? [];
  const source = all.find((unit) => unit.code === fromUnit);

  // ⛔ Лише та сама розмірність. Сервер відхилить решту `ECR-UOM-0422`, і
  // показувати такий вибір означало б вести користувача у відмову.
  const targets = source === undefined ? [] : all.filter((u) => u.dimensionId === source.dimensionId);

  const convert = useMutation({
    mutationFn: () =>
      apiFetch<ConvertUnitResponse>('/api/v1/units/convert', {
        method: 'POST',
        body: JSON.stringify({
          value,
          fromUnit: fromUnit ?? '',
          toUnit: toUnit ?? '',
        } satisfies ConvertUnitRequest),
      }),
    onSuccess: setResult,
    onError: showApiError,
  });

  return (
    <>
      <PageHeader
        title={t('units.title')}
        actions={
          <Group gap="xs" align="end">
            <NumberInput
              size="xs"
              miw={120}
              label={t('units.value')}
              value={value}
              onChange={(next) => setValue(typeof next === 'number' ? next : value)}
            />

            <Select
              size="xs"
              miw={130}
              label={t('units.from')}
              data={all.map((unit) => ({ value: unit.code, label: unit.code }))}
              value={fromUnit}
              onChange={(next) => {
                setFromUnit(next);

                // Цільова одиниця належала іншій розмірності — вибір більше
                // не має сенсу, і лишити його означало б надіслати завідомо
                // відхилений запит.
                setToUnit(null);
                setResult(null);
              }}
              searchable
            />

            <Select
              size="xs"
              miw={130}
              label={t('units.to')}
              description={source === undefined ? t('units.pickFrom') : undefined}
              data={targets.map((unit) => ({ value: unit.code, label: unit.code }))}
              value={toUnit}
              onChange={setToUnit}
              searchable
            />

            <Button
              size="xs"
              disabled={fromUnit === null || toUnit === null}
              loading={convert.isPending}
              onClick={() => convert.mutate()}
            >
              {t('units.convert')}
            </Button>

            {result !== null && (
              <Text size="sm" fw={600}>
                {result.value} {result.unit}
              </Text>
            )}
          </Group>
        }
      />

      <AsyncBoundary<UnitRef[]>
        isPending={units.isPending}
        error={units.error}
        data={units.data}
        isEmpty={(list) => list.length === 0}
        emptyTitle={t('units.empty')}
        emptyHint={t('units.emptyHint')}
        skeleton="table"
        onRetry={() => void units.refetch()}
      >
        {(list) => (
          <Table striped className="ecr-sticky-head">
            <Table.Thead>
              <Table.Tr>
                <Table.Th>{t('units.code')}</Table.Th>
                <Table.Th>{t('units.dimension')}</Table.Th>
                <Table.Th>{t('units.factor')}</Table.Th>
                <Table.Th>{t('units.offset')}</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {[...list]
                .sort((a, b) => a.dimensionId - b.dimensionId || a.code.localeCompare(b.code))
                .map((unit) => (
                  <Table.Tr key={unit.id}>
                    <Table.Td>
                      {unit.code}
                      {/* ⚠ Базова одиниця розмірності видно окремо: саме через
                          неї йде кожна конверсія, і множник решти — це
                          множник ДО НЕЇ. */}
                      {unit.factorToBase === 1 && unit.offsetToBase === 0 && (
                        <Badge ml="xs" size="xs" variant="light">
                          {t('units.base')}
                        </Badge>
                      )}
                    </Table.Td>
                    <Table.Td>{unit.dimensionId}</Table.Td>
                    <Table.Td>{unit.factorToBase}</Table.Td>
                    <Table.Td>{unit.offsetToBase}</Table.Td>
                  </Table.Tr>
                ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>
    </>
  );
}
