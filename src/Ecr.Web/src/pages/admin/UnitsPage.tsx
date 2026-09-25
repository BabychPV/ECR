import { useState, type JSX } from 'react';
import { Badge, Button, Group, Modal, Select, Stack, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { ConvertUnitRequest, ConvertUnitResponse, UnitRef } from '@/api/types';
import { createUnit, deleteUnit, unitReferences, unitUsage } from '@/features/units/api';
import { UnitEditModal } from '@/features/units/UnitEditModal';
import { UsageKindLabel } from '@/features/usage/UsageKindLabel';
import { decimalEquals, normalizeDecimal } from '@/shared/format';
import { can, useSession } from '@/shared/session/useSession';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
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

  /*
   * ⛔ Усі три десяткові поля цього екрана — РЯДКИ, і вводяться теж рядком
   * (`TextInput`, не `NumberInput`). `NumberInput` Mantine повертає в
   * `onChange` `floatValue`, тобто проганяє введене через IEEE-754 ще до
   * стану компонента: множник `0.4535923700000000` втратив би хвіст просто
   * від того, що його надрукували. Контракт віддає й приймає `decimal`
   * рядком саме тому (`e470777a`), і на клієнті цей рядок ніде не
   * перетворюється на число — ані туди, ані назад.
   */
  const [value, setValue] = useState('1');
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
          value: value.trim(),
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
  const [newFactor, setNewFactor] = useState('1');
  const [newOffset, setNewOffset] = useState('0');

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

        // ⚠ Рядок іде як є (після `trim`). `Number(newFactor)` тут коштував
        // би 16-го знака множника, а саме він і відрізняє точний коефіцієнт
        // від округленого.
        factorToBase: newFactor.trim(),
        offsetToBase: newOffset.trim(),
      }),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['units'] });
      setCreating(false);
      setNewCode('');
      setNewSymbol('');
      setNewName('');
      setNewDimensionId(null);
      setNewFactor('1');
      setNewOffset('0');
      showDone(t('units.created'));
    },
    onError: showApiError,
  });

  // Директива №15, BE-15: діалог видалення СПЕРШУ показує залежних, а на
  // відмову `ECR-UOM-0409` — перелік із самої відмови замість «повторити»:
  // повтор дав би ту саму відповідь.
  const canEdit = can(session.data, 'Uom.EditCatalog');
  const [deleting, setDeleting] = useState<UnitRef | null>(null);
  const [editing, setEditing] = useState<UnitRef | null>(null);

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

  /*
   * ⛔ Обидві десяткові колонки — `num` + `exact`: рядок сервера показується
   * дослівно. Без `exact` клітинка `num` малюється зі стелею дробової частини
   * переліку (три знаки), і множник `0.4535923700` поїхав би на екран як
   * `0.454`. На довіднику одиниць множник — це і є те, заради чого контракт
   * перевели на рядок (`e470777a`): округлити його означає стерти відповідь,
   * по яку сюди приходять.
   *
   * ⚠ Сам `num` лишається, і не заради вирівнювання: він вмикає ЧИСЛОВЕ
   * порівняння десяткових рядків (`compareDecimals`). Без нього колонка
   * сортувалася б колатором, тобто `'0.001'` стояло б після `'0.0001'`, а
   * `'10'` — перед `'9'`.
   */
  const columns: readonly DataTableColumn<UnitRef>[] = [
    {
      key: 'code',
      label: t('units.code'),
      render: (unit) => (
        <>
          {unit.code}
          {/* ⚠ Базова одиниця розмірності видно окремо: саме через неї йде
              кожна конверсія, і множник решти — це множник ДО НЕЇ.

              ⛔ Порівняння — `decimalEquals`, не `Number(x) === 1`. Множник
              приходить із масштабом колонки (`"1.0000000000"`), тож рівність
              рядків тут не працює; а `Number` не відрізнив би базову одиницю
              від такої, що відходить від неї на 17-му знаку. */}
          {decimalEquals(unit.factorToBase, '1') && decimalEquals(unit.offsetToBase, '0') && (
            <Badge ml="xs" size="xs" variant="light">
              {t('units.base')}
            </Badge>
          )}
        </>
      ),
    },
    {
      // Q-297: до фіксу тут був голий `unit.dimensionId` — число без жодного
      // сенсу для людини, що дивиться на екран.
      key: 'dimensionCode',
      label: t('units.dimension'),

      // ⚠ Розмірність ГРУПУЄ, а всередині групи порядок задає код — і при
      // відкритті (`defaultSort` нижче), і після клацання по шапці.
      sortValue: (unit) => [unit.dimensionCode, unit.code],
    },
    {
      key: 'factorToBase',
      label: t('units.factor'),
      num: true,
      exact: true,
    },
    {
      key: 'offsetToBase',
      label: t('units.offset'),
      num: true,
      exact: true,
    },
    // ⚠ Колонка дій з'являється лише з правом — рівно як і до переїзду; шапка в
    // неї порожня, а `sortable: false` тому, що в кнопки немає скалярного
    // значення і сортування за нею мовчки не робило б нічого.
    ...(canEdit
      ? [
          {
            key: 'actions',
            label: '',
            sortable: false,
            render: (unit: UnitRef) => (
              <Group gap="xs" wrap="nowrap">
                <Button
                  size="compact-xs"
                  variant="subtle"
                  aria-label={`${t('units.edit')} ${unit.code}`}
                  onClick={() => setEditing(unit)}
                >
                  {t('units.edit')}
                </Button>
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
              </Group>
            ),
          } satisfies DataTableColumn<UnitRef>,
        ]
      : []),
  ];

  return (
    <>
      <PageHeader
        title={t('units.title')}
        actions={
          <Group gap="xs" align="end">
            <TextInput
              size="xs"
              miw={120}
              inputMode="decimal"
              label={t('units.value')}
              value={value}
              onChange={(event) => setValue(event.currentTarget.value)}
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
              // ⚠ Поле тепер текстове, тож «не число» стало можливим станом:
              // кнопка, яка веде у відому відмову сервера, гірша за вимкнену.
              disabled={fromUnit === null || toUnit === null || normalizeDecimal(value) === null}
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

      {/*
       * ⛔ `DataTable` замінює `AsyncBoundary` + `<Table>` РАЗОМ, а не лише
       * розмітку: обгортку станів набір тримає всередині себе (`DataTable.tsx`
       * — той самий `AsyncBoundary`, `skeleton="table"`). Лишити зовнішню поруч
       * означало б два перемикачі станів на одну таблицю — саме ту розбіжність,
       * заради усунення якої таблиця й стала компонентом. Правило «відмова ≠
       * порожньо» від цього не слабшає: воно переїхало разом із обгорткою.
       *
       * ⚠ Сортування шапкою прийшло з набором, і його тут не було: чотири
       * перші колонки стали клікабельними. Порядок при відкритті — той самий,
       * що й до переїзду (розмірність, усередині неї код), але тепер його
       * задає `defaultSort`, а не пресортований масив: шапка «Dimension» на
       * старті каже `aria-sort="ascending"`, тобто правду про порядок рядків.
       *
       * ⛔ `rows` — сам `units.data`: `undefined` мусить лишатися `undefined`,
       * `?? []` перетворило б «запиту ще не робили» на «порожньо».
       *
       * ⚠ `clearFiltersLabel`/`showMoreLabel` цьому екрану передавати НЕМА
       * куди: фільтрів у нього немає (тож немає й `onClearFilters`), а
       * `GET /api/v1/units` віддає довідник одним масивом без курсора (тож
       * немає `total`/`onShowMore`). Обидві кнопки в такому разі не
       * малюються зовсім, і проп до них був би мертвим.
       */}
      <DataTable<UnitRef>
        columns={columns}
        rows={units.data}
        rowKey={(unit) => String(unit.id)}
        defaultSort={{ key: 'dimensionCode', direction: 'asc' }}
        isPending={units.isPending}
        error={units.error}
        emptyTitle={t('units.empty')}
        emptyHint={t('units.emptyHint')}
        onRetry={() => void units.refetch()}
      />

      {/*
       * ⛔ X-23: тут стояв голий `<Modal>` — фокус падав на хрестик (Enter
       * закривав, а не скасовував, і навпаки), заголовок «Remove kg» не казав,
       * що саме це одиниця, а доки йшла перевірка «де використано», діалог
       * ~2 с стояв ПОРОЖНІМ. Тепер — `ConfirmModal` (фокус на «Cancel», назва
       * об'єкта в заголовку) і видимий стан перевірки.
       *
       * ⚠ Кнопка підтвердження недоступна, доки не відомо, що залежних немає:
       * вона вела б у відому відмову `409`.
       */}
      <ConfirmModal
        opened={deleting !== null}
        title={t('units.deleteTitle', { code: deleting?.code ?? '' })}
        verb={t('common.delete')}
        confirmDisabled={dependents?.total !== 0}
        isPending={remove.isPending}
        onConfirm={() => {
          if (deleting !== null) {
            remove.mutate(deleting.id);
          }
        }}
        onClose={() => setDeleting(null)}
      >
        <Stack gap="sm">
          {usage.error !== null && remove.error === null && (
            <Text size="sm" c="statusError">
              {usage.error.message}
            </Text>
          )}

          {dependents === undefined && usage.error === null && (
            <Text size="sm" c="dimmed" data-testid="unit-usage-pending">
              {t('units.deleteChecking')}
            </Text>
          )}

          {dependents !== undefined &&
            (dependents.total === 0 ? (
              <Text size="sm">{t('units.deleteUnused')}</Text>
            ) : (
              <>
                <Text size="sm">{t('units.deleteUsedIn', { total: dependents.total })}</Text>
                <Stack gap="xs" data-testid="unit-references">
                  {/* ⛔ R-20: рядок був `<Text>` (тобто `<p>`) з `<Badge>` (`<div>`)
                      усередині — недійсна розмітка, про яку React кричав у консоль. */}
                  {dependents.items.map((item) => (
                    <Group gap="xs" wrap="nowrap" key={`${item.kind}:${item.id}`}>
                      <Badge size="xs" variant="light">
                        <UsageKindLabel kind={item.kind} />
                      </Badge>
                      <Text size="sm">{item.label}</Text>
                    </Group>
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
        </Stack>
      </ConfirmModal>

      <UnitEditModal unitId={editing?.id ?? null} code={editing?.code ?? ''} onClose={() => setEditing(null)} />

      <Modal opened={creating}onClose={() => setCreating(false)} title={t('units.new')}>
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

          <TextInput
            label={t('units.factor')}
            description={t('units.factorHint')}
            inputMode="decimal"
            value={newFactor}
            onChange={(event) => setNewFactor(event.currentTarget.value)}
          />

          <TextInput
            label={t('units.offset')}
            description={t('units.offsetHint')}
            inputMode="decimal"
            value={newOffset}
            onChange={(event) => setNewOffset(event.currentTarget.value)}
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
                newDimensionId === null ||
                // ⚠ Наслідок переходу на текстове поле: те, що `NumberInput`
                // не давав ввести взагалі, тепер треба перевірити самому.
                normalizeDecimal(newFactor) === null ||
                normalizeDecimal(newOffset) === null
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
