import { useState, type JSX } from 'react';
import { Badge, Button, Group, Modal, NumberInput, Select, Stack, Table, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { ConvertUnitRequest, ConvertUnitResponse, UnitRef } from '@/api/types';
import { createUnit, deleteUnit, unitReferences, unitUsage } from '@/features/units/api';
import { can, useSession } from '@/shared/session/useSession';
import { AsyncBoundary } from '@/shared/ui/AsyncBoundary';
import { PageHeader } from '@/shared/ui/PageHeader';
import { showApiError, showDone } from '@/shared/ui/notify';
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
  const session = useSession();
  const queryClient = useQueryClient();

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

  // ⛔ UI-аудит, lane 4: жоден обліковий запис, включно з повноправним
  // адміністратором, не мав шляху додати одиницю виміру — той самий клас
  // дефекту, що вже виправлений для довідників (`Q-200`).
  //
  // ⚠ Розмірність вибирається зі СПИСКУ, отриманого з уже завантажених
  // одиниць (унікальні пари `dimensionId`/`dimensionCode`), а не окремим
  // запитом: `GET /api/v1/dimensions` не існує в системі взагалі —
  // розмірності ніде не віддаються самі по собі, лише вкладені в кожну
  // одиницю. Судження виконавця: усі 11 розмінностей seed-у вже
  // представлені хоч однією одиницею, тож цього списку досить для форми;
  // заводити новий ендпоінт лише заради випадаючого списку означало б
  // розширювати контракт заради поля, яке й так має звідки взятися.
  const [creating, setCreating] = useState(false);
  const [newCode, setNewCode] = useState('');
  const [newSymbol, setNewSymbol] = useState('');
  const [newName, setNewName] = useState('');
  const [newDimensionId, setNewDimensionId] = useState<string | null>(null);
  const [newFactor, setNewFactor] = useState(1);
  const [newOffset, setNewOffset] = useState(0);

  const dimensions = Array.from(
    new Map(all.map((unit) => [unit.dimensionId, unit.dimensionCode])).entries(),
  ).sort(([, a], [, b]) => a.localeCompare(b));

  const create = useMutation({
    mutationFn: () =>
      createUnit({
        code: newCode,
        symbolL10n: { en: newSymbol },
        nameL10n: { en: newName },
        dimensionId: Number(newDimensionId ?? 0),
        factorToBase: newFactor,
        offsetToBase: newOffset,
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['units'] });
      setCreating(false);
      setNewCode('');
      setNewSymbol('');
      setNewName('');
      setNewDimensionId(null);
      setNewFactor(1);
      setNewOffset(0);
      showDone(t('units.created'));
    },
    onError: showApiError,
  });

  // Директива №15, BE-15: діалог видалення СПЕРШУ показує залежних, а на
  // відмову `ECR-UOM-0409` — перелік із самої відмови замість «повторити»:
  // повтор дав би ту саму відповідь.
  const canEdit = can(session.data, 'Uom.EditCatalog');
  const [deleting, setDeleting] = useState<UnitRef | null>(null);

  const usage = useQuery({
    queryKey: ['units', deleting?.id, 'usage'],
    queryFn: () => unitUsage(deleting?.id ?? 0),
    enabled: deleting !== null,
    staleTime: 0,
  });

  const remove = useMutation({
    // Відмову показує діалог, читаючи `remove.error` у рендері.
    meta: { handled: true },
    mutationFn: (unitId: number) => deleteUnit(unitId),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['units'] });
      setDeleting(null);
      showDone(t('units.deleted'));
    },
  });

  // ⚠ Відмова свіжіша за перелік, прочитаний при відкритті: посилання могло
  // з'явитися, поки діалог стояв відкритий.
  const dependents = unitReferences(remove.error) ?? usage.data;

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

            {can(session.data, 'Uom.EditCatalog') && (
              <Button size="xs" variant="default" onClick={() => setCreating(true)}>
                {t('units.new')}
              </Button>
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
                {canEdit && <Table.Th />}
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {[...list]
                .sort(
                  (a, b) =>
                    a.dimensionCode.localeCompare(b.dimensionCode) || a.code.localeCompare(b.code),
                )
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
                    {/* Q-297: до фіксу тут був голий `unit.dimensionId` — число
                        без жодного сенсу для людини, що дивиться на екран. */}
                    <Table.Td>{unit.dimensionCode}</Table.Td>
                    <Table.Td>{unit.factorToBase}</Table.Td>
                    <Table.Td>{unit.offsetToBase}</Table.Td>
                    {canEdit && (
                      <Table.Td>
                        <Button
                          size="compact-xs"
                          variant="subtle"
                          color="statusError"
                          aria-label={`${t('common.delete')} ${unit.code}`}
                          onClick={() => {
                            remove.reset();
                            setDeleting(unit);
                          }}
                        >
                          {t('common.delete')}
                        </Button>
                      </Table.Td>
                    )}
                  </Table.Tr>
                ))}
            </Table.Tbody>
          </Table>
        )}
      </AsyncBoundary>

      <Modal
        opened={deleting !== null}
        onClose={() => setDeleting(null)}
        title={`${t('common.delete')} ${deleting?.code ?? ''}`}
      >
        <Stack gap="sm">
          {usage.error !== null && remove.error === null && (
            <Text size="sm" c="statusError">
              {usage.error.message}
            </Text>
          )}

          {dependents !== undefined &&
            (dependents.total === 0 ? (
              <Text size="sm">{t('units.deleteUnused')}</Text>
            ) : (
              <>
                <Text size="sm">{t('units.deleteUsedIn', { total: dependents.total })}</Text>
                <Stack gap="xs" data-testid="unit-references">
                  {dependents.items.map((item) => (
                    <Text size="sm" key={`${item.kind}:${item.id}`}>
                      <Badge size="xs" variant="light" mr="xs">
                        {item.kind}
                      </Badge>
                      {item.label}
                    </Text>
                  ))}
                </Stack>
              </>
            ))}

          {/* Відмова іншого роду (403, 404, мережа) — текстом сервера. */}
          {remove.error !== null && unitReferences(remove.error) === null && (
            <Text size="sm" c="statusError">
              {remove.error.message}
            </Text>
          )}

          <Group justify="flex-end" mt="sm">
            <Button variant="default" onClick={() => setDeleting(null)}>
              {t('common.cancel')}
            </Button>
            {/* ⛔ Кнопки немає, доки є залежні: вона вела б у відому відмову. */}
            {dependents?.total === 0 && (
              <Button
                color="statusError"
                loading={remove.isPending}
                onClick={() => {
                  if (deleting !== null) {
                    remove.mutate(deleting.id);
                  }
                }}
              >
                {t('common.delete')}
              </Button>
            )}
          </Group>
        </Stack>
      </Modal>

      <Modal opened={creating} onClose={() => setCreating(false)} title={t('units.new')}>
        <Stack gap="sm">
          <TextInput
            label={t('units.newCode')}
            description={t('units.newCodeHint')}
            value={newCode}
            onChange={(event) => setNewCode(event.currentTarget.value)}
            data-autofocus
          />

          <TextInput
            label={t('units.symbol')}
            description={t('units.symbolHint')}
            value={newSymbol}
            onChange={(event) => setNewSymbol(event.currentTarget.value)}
          />

          <TextInput
            label={t('units.name')}
            value={newName}
            onChange={(event) => setNewName(event.currentTarget.value)}
          />

          <Select
            label={t('units.dimension')}
            data={dimensions.map(([id, code]) => ({ value: String(id), label: code }))}
            value={newDimensionId}
            onChange={setNewDimensionId}
          />

          <NumberInput
            label={t('units.factor')}
            description={t('units.factorHint')}
            value={newFactor}
            onChange={(next) => setNewFactor(typeof next === 'number' ? next : newFactor)}
          />

          <NumberInput
            label={t('units.offset')}
            description={t('units.offsetHint')}
            value={newOffset}
            onChange={(next) => setNewOffset(typeof next === 'number' ? next : newOffset)}
          />

          <Group justify="flex-end" mt="sm">
            <Button variant="default" onClick={() => setCreating(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              disabled={
                newCode.trim().length === 0 ||
                newSymbol.trim().length === 0 ||
                newName.trim().length === 0 ||
                newDimensionId === null
              }
              loading={create.isPending}
              onClick={() => create.mutate()}
            >
              {t('units.new')}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}
