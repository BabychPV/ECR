import { lazy, Suspense, useMemo, useState, type JSX } from 'react';
import { Anchor, Badge, Button, Group, Modal, Select, Stack, Text, TextInput } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { UnitRef } from '@/api/types';
import { createUnit, deleteUnit, unitReferences, unitUsage } from '@/features/units/api';
import { UnitEditModal } from '@/features/units/UnitEditModal';
import { UsageKindLabel } from '@/features/usage/UsageKindLabel';
import { decimalEquals, formatDecimal, normalizeDecimal } from '@/shared/format';
import { can, useSession } from '@/shared/session/useSession';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { DataTable, type DataTableColumn } from '@/shared/ui/DataTable';
import { useDetailPanel } from '@/shared/ui/DetailDrawer';
import { FilterBar } from '@/shared/ui/FilterBar';
import { ListPage } from '@/shared/ui/ListPage';
import type { StatItem } from '@/shared/ui/StatStrip';
import { TwoLine } from '@/shared/ui/TwoLine';
import { useUrlParamsSetter, useUrlState } from '@/shared/ui/useUrlState';
import { showApiError, showDone } from '@/shared/ui/notify';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { problemText } from '@/shared/ui/problemText';
import { usePendingLoading } from '@/features/common/usePendingLoading';

/*
 * ⚠ Шторка й конвертор — ЛІНИВІ чанки (UI-21): обидва закриті за
 * замовчуванням, і маршрут переліку не має за них платити.
 */
const UnitDetailBody = lazy(() => import('@/features/units/UnitDetailBody'));
const UnitConvertModal = lazy(() => import('@/features/units/UnitConvertModal'));

/** Значення `?panel=` шторки одиниці. */
export function unitPanelId(unitId: number): string {
  return `unit-${String(unitId)}`;
}

/** Показник смуги, що фільтрує: одиниці зі зсувом (температура). */
const OffsetStat = 'offset';

/** Показник смуги, що фільтрує: одиниці, яких не тримає жодна колонка чи поле. */
const UnusedStat = 'unused';

/**
 * Що людина бачить як одиницю: позначення мовою інтерфейсу (`°C`, `m³`), а не код (`degC`, `m3`).
 *
 * ⚠ `symbolL10n` у `UnitRef` — з UI-21 (41c0fbd5); старий сервер чи дублер без нього — код, щоб
 * рядок не лишився порожнім. Макет: колонка «Unit» = `twoLine(symbol, name)`.
 */
export function unitSymbol(unit: UnitRef): string {
  return localized({ values: unit.symbolL10n ?? {} }) || unit.code;
}

/**
 * Базова одиниця кожної розмірності.
 *
 * ⚠ Спершу — прапорець сервера `isBase` (LS, `UnitRef.isBase`): це він каже,
 * через яку одиницю йде конверсія. Розмірність без позначеної базової
 * (старий сервер, тест-дублер) — за значенням: множник 1 і зсув 0.
 *
 * ⛔ `decimalEquals`, не `Number(x) === 1`: множник приходить із масштабом
 * колонки (`"1.0000000000"`), а `Number` не відрізнив би базову одиницю від
 * такої, що відходить від неї на 17-му знаку. Кілька кандидатів (не мало б
 * статися) — перший за кодом, щоб відповідь не залежала від порядку масиву.
 */
export function baseUnits(units: readonly UnitRef[]): ReadonlyMap<number, string> {
  const bases = new Map<number, string>();
  const byCode = [...units].sort((a, b) => a.code.localeCompare(b.code));

  for (const unit of byCode) {
    if (unit.isBase === true && !bases.has(unit.dimensionId)) bases.set(unit.dimensionId, unit.code);
  }

  for (const unit of byCode) {
    if (bases.has(unit.dimensionId)) continue;
    if (decimalEquals(unit.factorToBase, '1') && decimalEquals(unit.offsetToBase, '0')) {
      bases.set(unit.dimensionId, unit.code);
    }
  }

  return bases;
}

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

  const units = useQuery({
    queryKey: ['units'],
    queryFn: () => apiFetch<UnitRef[]>('/api/v1/units'),

    // Довідник одиниць міняється раз на роки — новою розмірністю, не правкою.
    staleTime: 60 * 60 * 1000,
  });

  const all = useMemo(() => units.data ?? [], [units.data]);
  const [panel, setPanel] = useDetailPanel();

  // UI-21: «Check a conversion» — друга дія шапки, діалогом; `null` — закритий,
  // рядок — одиниця «From», з якої його відкрили (шторка), `''` — без неї.
  const [converting, setConverting] = useState<string | null>(null);

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

  const createLoading = usePendingLoading(create.isPending);

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

      // Шторка видаленої одиниці не має що показувати — і `?panel=` теж.
      setPanel(null);
      showDone(t('units.deleted'));
    },
  });

  // ⚠ Відмова свіжіша за перелік, прочитаний при відкритті: посилання могло
  // з'явитися, поки діалог стояв відкритий.
  const dependents = unitReferences(remove.error) ?? usage.data;

  const bases = baseUnits(all);
  const byCode = new Map(all.map((unit) => [unit.code, unit]));
  // Базова одиниця — тим самим позначенням, що й у першій колонці (`°C`, а не `degC`).
  const baseSymbol = (dimensionId: number): string | null => {
    const code = bases.get(dimensionId);
    if (code === undefined) return null;

    const base = byCode.get(code);
    return base === undefined ? code : unitSymbol(base);
  };
  const [params] = useSearchParams();
  const [stat, setStat] = useUrlState('stat');
  const setParams = useUrlParamsSetter();
  const query = (params.get('q') ?? '').trim().toLowerCase();
  const dimension = params.get('dimension');

  const dimensionOptions = Array.from(new Set(all.map((unit) => unit.dimensionCode)))
    .sort((a, b) => a.localeCompare(b))
    .map((code) => ({ value: code, label: code }));

  // ⚠ Фільтр — на клієнті: `GET /units` віддає весь довідник одним масивом.
  const shown =
    units.data === undefined
      ? undefined
      : units.data.filter(
          (unit) =>
            (query.length === 0 ||
              unit.code.toLowerCase().includes(query) ||
              unitSymbol(unit).toLowerCase().includes(query) ||
              unit.dimensionCode.toLowerCase().includes(query)) &&
            (dimension === null || unit.dimensionCode === dimension) &&
            (stat !== OffsetStat || !decimalEquals(unit.offsetToBase, '0')) &&
            (stat !== UnusedStat || unit.usedIn === 0),
        );

  const filtered = query.length > 0 || dimension !== null || stat !== null;

  /*
   * ⚠ `usedIn` — лише для того, хто має `Uom.EditCatalog`; інакше `null`
   * («не знаю», а не «ніде»). Тому колонка «Used in» і показник «not used
   * anywhere» з'являються, лише коли сервер віддав число КОЖНІЙ одиниці:
   * лічба по частині видавала б невідоме за нуль (`D15-06`).
   */
  const usageKnown = all.length > 0 && all.every((unit) => unit.usedIn !== null && unit.usedIn !== undefined);

  const baseStats: readonly [StatItem, StatItem, StatItem] = [
    { id: 'units', label: t('units.statUnits'), value: all.length, filter: false },
    { id: 'dimensions', label: t('units.statDimensions'), value: dimensionOptions.length, filter: false },
    {
      id: OffsetStat,
      label: t('units.statOffset'),
      value: all.filter((unit) => !decimalEquals(unit.offsetToBase, '0')).length,
    },
  ];
  const stats: readonly [StatItem, StatItem, StatItem] | readonly [StatItem, StatItem, StatItem, StatItem] =
    usageKnown
      ? [...baseStats, { id: UnusedStat, label: t('units.statUnused'), value: all.filter((unit) => unit.usedIn === 0).length }]
      : baseStats;

  const openUnit = all.find((unit) => unitPanelId(unit.id) === panel);

  /*
   * ⛔ Обидві десяткові колонки — `num` + `formatDecimal` БЕЗ стелі (UI-21):
   * хвостові нулі масштабу колонки (`1000.000000000000000000`) зрізані,
   * тисячі розділені мовою інтерфейсу, а значущі знаки — ВСІ. Стеля дробової
   * частини переліку (три знаки, `CellFractionCeiling`) тут не годиться:
   * множник `0.4535923700` поїхав би на екран як `0.454`, а він і є
   * відповіддю, по яку сюди приходять (`e470777a`). Рядок не проходить через
   * `Number` ніде.
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
      sortValue: (unit) => unitSymbol(unit),

      // ⚠ Код — ще й ПОСИЛАННЯ на шторку: клац по рядку (`onRowClick`) не має
      // клавіатурного шляху, а кнопка в першій клітинці — має (`Tab`, `Enter`).
      // Назва одиниці другим рядком (макет: `twoLine(symbol, name)`) —
      // `nameL10n` мовою інтерфейсу; немає назви — другого рядка немає.
      render: (unit) => (
        <TwoLine
          primary={
            <Anchor
              component="button"
              type="button"
              size="sm"
              data-unit-open={unit.code}
              onClick={(event) => {
                event.stopPropagation();
                setPanel(unitPanelId(unit.id));
              }}
            >
              {unitSymbol(unit)}
            </Anchor>
          }
          secondary={localized({ values: unit.nameL10n ?? {} }) || undefined}
        />
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
      // UI-21: базова одиниця розмірності — окремою колонкою (макет «Base
      // unit»), а не значком у коді: саме через неї йде кожна конверсія, і
      // множник решти — це множник ДО НЕЇ.
      key: 'base',
      label: t('units.baseUnit'),
      sortValue: (unit) => bases.get(unit.dimensionId) ?? null,
      render: (unit) =>
        bases.get(unit.dimensionId) === unit.code ? (
          // ⚠ Макет: приглушене слово («is the base»), а не капітельний бейдж «BASE» (звірка
          // batch-4 з макетом, п.19): це не стан, а відповідь на питання колонки.
          <Text span size="sm" c="dimmed" data-unit-base="">
            {t('units.base')}
          </Text>
        ) : (
          baseSymbol(unit.dimensionId)
        ),
    },
    {
      key: 'factorToBase',
      label: t('units.factor'),
      num: true,
      render: (unit) => formatDecimal(unit.factorToBase) ?? unit.factorToBase,
    },
    {
      key: 'offsetToBase',
      label: t('units.offset'),
      num: true,
      // ⚠ Нульовий зсув — не дані, а відсутність: клітинка порожня (макет).
      render: (unit) =>
        decimalEquals(unit.offsetToBase, '0') ? null : (formatDecimal(unit.offsetToBase) ?? unit.offsetToBase),
    },
    ...(usageKnown
      ? [
          {
            key: 'usedIn',
            label: t('units.usedIn'),
            num: true,
            // ⚠ Скільки колонок шаблонів і полів довідників тримає одиницю.
            render: (unit: UnitRef) => (unit.usedIn === null || unit.usedIn === undefined ? '—' : String(unit.usedIn)),
            sortValue: (unit: UnitRef) => unit.usedIn ?? -1,
          } satisfies DataTableColumn<UnitRef>,
        ]
      : []),
  ];

  return (
    <ListPage
      header={{
        title: t('units.title'),
        // Пояснення сторінки ЗАМІСТЬ пояснення маршруту, а не другим рядком під ним (batch-2-a, дефект 3).
        description: t('units.description'),
        primary: canEdit ? { label: t('units.new'), onClick: () => setCreating(true) } : undefined,
        secondary: [{ label: t('units.checkConversion'), onClick: () => setConverting('') }],
      }}
      stats={
        units.data === undefined
          ? undefined
          : { label: t('units.statsLabel'), items: stats, active: stat, onSelect: setStat }
      }
      filters={
        <FilterBar
          search={{ label: t('units.search'), placeholder: t('units.searchPlaceholder') }}
          filters={[{ id: 'dimension', label: t('units.dimension'), options: dimensionOptions }]}
          clearLabel={t('filters.clear')}
        />
      }
      table={
        /*
         * ⛔ `DataTable` тримає стани (завантаження, відмова, порожньо) сам —
         * зовнішньої обгортки станів тут немає.
         *
         * ⛔ `rows` — відфільтрований `units.data`: `undefined` лишається
         * `undefined` («запиту ще не робили» ≠ «порожньо»).
         *
         * ⚠ Порядок при відкритті — розмірність, усередині неї код
         * (`defaultSort`), тож шапка «Dimension» на старті каже правду
         * `aria-sort="ascending"`.
         */
        <DataTable<UnitRef>
          columns={columns}
          rows={shown}
          rowKey={(unit) => String(unit.id)}
          defaultSort={{ key: 'dimensionCode', direction: 'asc' }}
          isPending={units.isPending}
          error={units.error}
          emptyTitle={t('units.empty')}
          emptyHint={t('units.emptyHint')}
          filtered={filtered}
          noMatchTitle={t('units.noMatch')}
          // ⛔ ОДИН перехід на всі параметри: кілька `setSearchParams` поспіль
          // в одному обробнику губили зміни (`useUrlState.ts`).
          onClearFilters={() => setParams({ q: null, dimension: null, stat: null })}
          clearFiltersLabel={t('filters.clear')}
          onRetry={() => void units.refetch()}
          onRowClick={(unit) => setPanel(unitPanelId(unit.id))}
          selectedKey={openUnit === undefined ? undefined : String(openUnit.id)}
        />
      }
      detail={
        openUnit === undefined
          ? undefined
          : {
              panelId: unitPanelId(openUnit.id),
              title: openUnit.code,
              subtitle: openUnit.dimensionCode,
              closeLabel: t('common.close'),
              // ⚠ Правка й видалення — тут, а не кнопками в рядку (UI-21,
              // макет: `Delete…` ліворуч, `Convert…`, `Edit` головною).
              footer: (
                <>
                  {canEdit && (
                    <Button
                      // Руйнівна дія панелі — обвідна (`docs/design/ui-conventions.md`).
                      variant="outline"
                      color="statusError"
                      mr="auto"
                      onClick={() => {
                        remove.reset();
                        setDeleting(openUnit);
                      }}
                    >
                      {t('common.delete')}
                    </Button>
                  )}
                  <Button variant="default" onClick={() => setConverting(openUnit.code)}>
                    {t('units.convert')}
                  </Button>
                  {canEdit && <Button onClick={() => setEditing(openUnit)}>{t('units.edit')}</Button>}
                </>
              ),
              children: (
                <Suspense fallback={<Text size="sm" c="dimmed">{t('common.loading')}</Text>}>
                  <UnitDetailBody
                    unit={openUnit}
                    baseCode={bases.get(openUnit.dimensionId) ?? null}
                    canEdit={canEdit}
                  />
                </Suspense>
              ),
            }
      }
    >
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
              {problemText(usage.error).detail ?? problemText(usage.error).title}
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

          {/* Відмова іншого роду (403, 404, мережа) — текстом сервера, якщо він
              локалізований, інакше назвою з каталогу (`ФВ-14.9a`). */}
          {remove.error !== null && unitReferences(remove.error) === null && (
            <Text size="sm" c="statusError">
              {problemText(remove.error).detail ?? problemText(remove.error).title}
            </Text>
          )}
        </Stack>
      </ConfirmModal>

      <UnitEditModal unitId={editing?.id ?? null} code={editing?.code ?? ''} onClose={() => setEditing(null)} />

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
              loading={createLoading}
              onClick={() => {
                if (create.isPending) return;
                create.mutate();
              }}
            >
              {t('units.new')}
            </Button>
          </Group>
        </Stack>
      </Modal>

      {converting !== null && (
        <Suspense fallback={null}>
          <UnitConvertModal
            units={all}
            initialFrom={converting === '' ? null : converting}
            onClose={() => setConverting(null)}
          />
        </Suspense>
      )}
    </ListPage>
  );
}
