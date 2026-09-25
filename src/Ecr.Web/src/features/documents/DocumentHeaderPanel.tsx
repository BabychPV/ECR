import { lazy, Suspense, useMemo, useState, type JSX } from 'react';
import { Button, Checkbox, Group, Select, Stack, TextInput, Title } from '@mantine/core';
import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { components } from '@/api/schema';
import type { RegistryDefDto, RegistryEntryDto, UnitRef } from '@/api/types';
import { coerce } from '@/features/grid/edits';
import { cellText, sameCellValue } from '@/features/grid/cellValue';
import { lookupCellDisplay } from '@/features/grid/LookupCellEditor';
import { unitCellDisplay } from '@/features/grid/UnitCellEditor';
import { localized } from '@/shared/i18n/localized';
import { t } from '@/shared/i18n';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { showDone } from '@/shared/ui/notify';

/**
 * Шапка документа: поля версії шаблону разом із поточними значеннями
 * (контракт від серверної сесії, `schema.d.ts`).
 *
 * ⛔ Визначення полів (які саме поля є у версії шаблону) заводить і редагує
 * ІНША сесія — адмінський редактор над `GET/PUT …/template-versions/{id}
 * /header-fields` (паралельна задача). Цей файл лише СПОЖИВАЄ значення
 * КОНКРЕТНОГО документа через `GET/PATCH …/documents/{id}/header` і не знає
 * структури версії напряму.
 *
 * ✎ Поле типу `Lookup` тепер показується й редагується повноцінним picker'ом
 * (`Select`), не сирим числом `ValueRegistryEntryId`: сервер (`4f167396`)
 * почав заповнювати `DocumentHeaderFieldDto.lookupRegistryDefId`, і клієнт
 * резолвить людську назву обраного запису ТИМ САМИМ шляхом, що вже діє для
 * Lookup-КОМІРОК сітки (`DocumentGrid.tsx` + `LookupCellEditor.ts`, директива
 * registry-lookup, PR A4): `GET /api/v1/registries` (ID → код), тоді
 * `GET /api/v1/registries/{code}/entries` (код → записи), і `entry.display`
 * як підпис. `lookupCellDisplay` (той самий файл, звідси лише ЧИТАЄТЬСЯ,
 * не редагується — умова задачі) дає той самий фолбек на сирий ідентифікатор
 * для запису, якого в довіднику більше немає.
 *
 * ⚠ `lookupRegistryDefId` формально й далі `null`-опційний у контракті
 * (поле не Lookup, або Lookup без довідника) — захисно деградує до старого
 * поводження: сире число текстом (нижче, `HeaderFieldInput`).
 */

type DocumentHeaderDto = components['schemas']['DocumentHeaderDto'];
type DocumentHeaderField = components['schemas']['DocumentHeaderFieldDto'];
type PatchHeaderField = components['schemas']['PatchHeaderField'];

/** Стабільне посилання на порожній перелік — поле без довідника (ще
 * завантажується/немає) не отримує новий масив на кожен рендер. */
const EmptyLookupEntries: readonly RegistryEntryDto[] = [];

/** Поле дати — за `import()`, той самий прийом, що `pages/admin/PeriodsPage.tsx`.
 *
 * ⛔ `@mantine/dates` тягне `dayjs`; панель і без того лінива (підключена
 * `React.lazy` у `DocumentPage.tsx`), але статичний імпорт цього модуля
 * усередині неї означав би, що дата вантажиться разом із рештою панелі для
 * КОЖНОГО документа — навіть того, у якого серед полів шапки дати немає
 * взагалі. */
const DateInput = lazy(async () => {
  const module = await import('@mantine/dates');

  return { default: module.DateInput };
});

/** Шапка документа: `GET /api/v1/documents/{id}/header` (право `Document.View`). */
function documentHeader(documentId: number): Promise<DocumentHeaderDto> {
  return apiFetch<DocumentHeaderDto>(`/api/v1/documents/${String(documentId)}/header`);
}

/**
 * Запис шапки: `PATCH /api/v1/documents/{id}/header` — грант `Write` на
 * проєкт документа, без окремого функціонального права (як `PATCH …/cells`).
 */
function patchDocumentHeader(
  documentId: number,
  fields: readonly PatchHeaderField[],
): Promise<DocumentHeaderDto> {
  return apiFetch<DocumentHeaderDto>(`/api/v1/documents/${String(documentId)}/header`, {
    method: 'PATCH',
    body: JSON.stringify({
      fields: [...fields],
    } satisfies components['schemas']['PatchDocumentHeaderRequest']),
  });
}

/** Дата без часу з `DateInput` → `"YYYY-MM-DD"` МІСЦЕВИМИ складниками.
 *
 * ⛔ НЕ `toISOString().slice(0, 10)`: той читає дату як UTC-північ і в
 * від'ємному зсуві зсуває календарний день на добу (той самий клас дефекту,
 * що описаний у `shared/format/datetime.ts` для зворотного напрямку). */
function isoDateOf(date: Date): string {
  const year = String(date.getFullYear()).padStart(4, '0');
  const month = String(date.getMonth() + 1).padStart(2, '0');
  const day = String(date.getDate()).padStart(2, '0');

  return `${year}-${month}-${day}`;
}

/** `"YYYY-MM-DD"` → `Date` МІСЦЕВОЇ півночі; що завгодно інше — `null`. */
function dateOf(value: string): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (match === null) return null;

  const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));

  return Number.isNaN(date.getTime()) ? null : date;
}

/**
 * Чернетка одного поля: для `Bool`/`Date` — значення в домені контракту
 * (`boolean | null`, `string | null`); для решти типів — СИРИЙ ТЕКСТ поля
 * вводу, до приведення (`coerce`, `features/grid/edits.ts`, — той самий
 * приймач, що й для правки комірки сітки того самого типу).
 *
 * ⚠ Текст, а не приведене значення: приведення на кожне натискання клавіші
 * означало б, що поле `Decimal` губить проміжний ввід («1.» ще не число) —
 * той самий аргумент, що для комірок сітки (`edits.ts`, `captureEdit`
 * приводить лише при ЗАХОПЛЕННІ правки, не на кожен символ).
 */
type Draft = Record<string, unknown>;

function draftOf(fields: readonly DocumentHeaderField[]): Draft {
  const draft: Draft = {};

  for (const field of fields) {
    if (field.dataType === 'Bool') {
      draft[field.code] = field.value === true ? true : field.value === false ? false : null;
    } else if (field.dataType === 'Date') {
      draft[field.code] = typeof field.value === 'string' ? field.value : null;
    } else {
      draft[field.code] = cellText(field.value);
    }
  }

  return draft;
}

/** Чернетку поля — до значення в домені контракту (те, що йде в PATCH і в порівняння). */
function effectiveValueOf(field: DocumentHeaderField, raw: unknown): unknown {
  if (field.dataType === 'Bool' || field.dataType === 'Date') return raw;

  return coerce(typeof raw === 'string' ? raw : '', field.dataType);
}

/**
 * Чи значення поля справді змінилося.
 *
 * ⛔ Порожній рядок і `null` зведені НАВМИСНО: `DocumentHeaderFieldDto.value`
 * сам не розрізняє «ще не заповнили» й «явно стерли» (коментар контракту), а
 * початкова чернетка текстового поля без значення — порожній рядок
 * (`cellText(null) === ''`). Без цього зведення непорушене порожнє поле
 * вважалося б «зміненим» на кожному відкритті панелі.
 */
function sameHeaderValue(a: unknown, b: unknown): boolean {
  const normalize = (value: unknown): unknown => (value === '' ? null : value);

  return sameCellValue(normalize(a), normalize(b));
}

/** Тіло `PatchHeaderField` для одного зміненого поля. */
function patchFieldOf(field: DocumentHeaderField, value: unknown): PatchHeaderField {
  return value === null
    ? { code: field.code, isEmpty: true, value: null }
    : { code: field.code, isEmpty: false, value };
}

/**
 * Поля з відповіді — захищено від будь-якої форми, крім очікуваної.
 *
 * ⛔ Не педантизм: у ТЕСТОВИХ заглушках клієнта URL `…/documents/{id}/header`
 * — підрядок уже наявного `url.includes('…/documents/{id}')`
 * (`DocumentPage.staleValidation.test.tsx` і, ймовірно, інші файли сторінки,
 * що мокають fetch тим самим прийомом), тож немокнутий запит шапки тихо
 * отримує тіло `DocumentSummary` — об'єкт БЕЗ `fields`. `for…of undefined`
 * ламав увесь `DocumentPage` без межі помилки (знайдено повним прогоном
 * `npm test`, не тестом самої панелі — той мокає `/header` явно). Правити
 * кожен чужий тестовий файл поза дозволеним списком заборонено умовами
 * задачі; захист тут — єдине місце, що лікує всі такі файли одразу.
 */
function fieldsOf(dto: DocumentHeaderDto | null | undefined): readonly DocumentHeaderField[] {
  return dto !== null && dto !== undefined && Array.isArray(dto.fields) ? dto.fields : [];
}

export interface DocumentHeaderPanelProps {
  /** Документ, чию шапку показуємо. */
  readonly documentId: number;

  /**
   * Той самий грант `Write` на проєкт, яким сервер приймає `PATCH …/header` —
   * без окремого функціонального права, дзеркало `hasProjectWriteGrant`
   * (`BusinessKeyChangeAction.tsx`), яке рахує сервер через
   * `IAccessDecisionService` для `PATCH …/cells`.
   */
  readonly canEdit: boolean;
}

/**
 * Панель шапки документа на `DocumentPage.tsx`: значення полів версії
 * шаблону (`GET …/header`) з можливістю правки (`PATCH …/header`).
 *
 * ⛔ Порожній перелік полів — НЕ помилка (комент контракту), а «у цього
 * документа шапки немає»: панель тоді не малюється ЗОВСІМ, а не показує
 * порожню картку із заголовком і без жодного поля (`D15-06`).
 */
export function DocumentHeaderPanel({
  documentId,
  canEdit,
}: DocumentHeaderPanelProps): JSX.Element | null {
  const queryClient = useQueryClient();

  const header = useQuery({
    queryKey: ['document-header', documentId],
    queryFn: () => documentHeader(documentId),
  });

  // ⛔ Той самий резолв, що `DocumentGrid.tsx` для Lookup-колонок сітки
  // (директива registry-lookup, PR A4): `lookupRegistryDefId` — це ID
  // довідника, а записи адресуються КОДОМ (`GET …/registries/{code}/entries`),
  // тож резолв іде у два кроки — перелік довідників (ID → код) один раз,
  // потім по одному запиту записів НА ДОВІДНИК (кілька полів шапки можуть
  // ділити той самий довідник).
  const lookupRegistryDefIds = useMemo(
    () => [
      ...new Set(
        fieldsOf(header.data)
          .filter(
            (field) =>
              field.dataType === 'Lookup' &&
              field.lookupRegistryDefId !== null &&
              field.lookupRegistryDefId !== undefined,
          )
          .map((field) => field.lookupRegistryDefId as number),
      ),
    ],
    [header.data],
  );

  const registriesList = useQuery({
    queryKey: queryKeys.registries.list(),
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
    enabled: lookupRegistryDefIds.length > 0,
  });

  const lookupRegistryCodes = useMemo(() => {
    const byId = new Map(
      (registriesList.data ?? []).map((registry) => [registry.id, registry] as const),
    );
    return lookupRegistryDefIds
      .map((id) => {
        const registry = byId.get(id);
        return { id, code: registry?.code, isTemporal: registry?.isTemporal ?? false };
      })
      .filter(
        (entry): entry is { id: number; code: string; isTemporal: boolean } =>
          entry.code !== undefined,
      );
  }, [lookupRegistryDefIds, registriesList.data]);

  /*
   * ⛔ Дефект живого прогону: `GET …/entries` вимагав `asOf` БЕЗУМОВНО, і цей
   * піцкер його не надсилав НІКОЛИ — сервер (фікс у `GetRegistryEntriesHandler.cs`,
   * той самий PR) відмовляв `422` для КОЖНОГО довідника, включно з
   * нетемпоральним. Тепер `asOf` іде лише для ТЕМПОРАЛЬНОГО довідника.
   *
   * ⚠ Дата — сьогоднішня КЛІЄНТА, а НЕ дата періоду документа. Різниця з
   * `DocumentGrid.tsx` (там — кінець періоду) не смак: `DocumentHeaderPanel`
   * отримує лише `documentId`/`canEdit` (`DocumentHeaderPanelProps` вище) —
   * `DocumentPage.tsx` не передає `periodKey`, і сам ендпоінт шапки
   * (`GET …/documents/{id}/header`) період не приймає: шапка належить
   * ДОКУМЕНТУ, а не конкретному періоду (на відміну від таблиць аркушів,
   * `ФВ-3.6`). Провести `periodKey` сюди означало б розширити
   * `DocumentPage.tsx` — файл поза дозволеним списком цієї задачі. Якщо
   * шапка колись отримає дату періоду — замінити тут одним рядком.
   */
  const lookupAsOf = isoDateOf(new Date());

  const lookupEntriesQueries = useQueries({
    queries: lookupRegistryCodes.map(({ code, isTemporal }) => {
      const asOf = isTemporal ? lookupAsOf : null;

      // ⚠ Базовий шлях — ОКРЕМИЙ шаблонний рядок, без `?asOf=` усередині:
      // `EndpointCoverageTests.Кожна_адреса_яку_викликає_клієнт_існує_на_сервері`
      // бере ВЕСЬ вміст МІЖ парою лапок як адресу — рядок запиту в тому
      // самому літералі виглядав би для неї окремим неіснуючим маршрутом.
      const baseUrl = `/api/v1/registries/${encodeURIComponent(code)}/entries`;

      return {
        queryKey: [...queryKeys.registries.entries(code), asOf],
        queryFn: () =>
          apiFetch<RegistryEntryDto[]>(
            asOf === null ? baseUrl : `${baseUrl}?asOf=${asOf}`,
          ),
      };
    }),
  });

  const lookupEntriesByRegistryId = useMemo(() => {
    const map = new Map<number, readonly RegistryEntryDto[]>();
    lookupRegistryCodes.forEach(({ id }, index) => {
      const entries = lookupEntriesQueries[index]?.data;
      if (entries !== undefined) map.set(id, entries);
    });

    return map;
  }, [lookupRegistryCodes, lookupEntriesQueries]);

  // ⚠ Чи довідник конкретного поля ще завантажується — окремо від глобальної
  // `registriesList.isPending`, бо кожен запис довідника йде своїм запитом
  // (`lookupEntriesQueries`). Реєстр, якого немає серед `lookupRegistryCodes`
  // (перелік довідників довантажився, а САМ довідник у ньому відсутній —
  // видалили) НЕ вважається «завантажується»: дані вже остаточні, просто
  // порожні.
  const lookupPendingByRegistryId = useMemo(() => {
    const map = new Map<number, boolean>();
    lookupRegistryCodes.forEach(({ id }, index) => {
      map.set(id, lookupEntriesQueries[index]?.isPending ?? false);
    });

    return map;
  }, [lookupRegistryCodes, lookupEntriesQueries]);

  /*
   * ⚠ Той самий банер, що `DocumentGrid.tsx` для Lookup-колонок сітки:
   * відмова довідника — НЕ те саме, що «поле без довідника» і не те саме, що
   * «довідник порожній» (`gridColumns`-коментар того файлу пояснює чому це
   * дорожчий клас помилки — шапку бачить оператор щодня, а не адміністратор).
   */
  const lookupError =
    lookupRegistryDefIds.length > 0
      ? (registriesList.error ?? lookupEntriesQueries.find((query) => query.error !== null)?.error ?? null)
      : null;

  const refetchLookups = (): void => {
    void registriesList.refetch();
    lookupEntriesQueries.forEach((query) => void query.refetch());
  };

  /*
   * ⛔ `R-01`: поле шапки типу `Unit` було текстовим полем — `kg` набрати
   * можна, а зберегти ні: сервер чекає ідентифікатор одиниці. Перелік одиниць
   * — той самий запит (`['units']`), що й у сітки, і лише тоді, коли таке
   * поле справді є.
   */
  const hasUnitFields = fieldsOf(header.data).some((field) => field.dataType === 'Unit');
  const units = useQuery({
    queryKey: ['units'],
    queryFn: () => apiFetch<UnitRef[]>('/api/v1/units'),
    enabled: hasUnitFields,
    staleTime: 60 * 60 * 1000,
  });

  const [draft, setDraft] = useState<Draft>({});
  const [loadedFor, setLoadedFor] = useState<number | null>(null);

  // ⚠ Наповнюється при зміні відповіді, а не в ефекті (той самий прийом, що
  // `RegistryEntryEditor.tsx`): ефект дав би зайвий рендер із порожньою
  // чернеткою, і поля на мить показали б порожні значення.
  //
  // ⛔ `fieldsOf` (не сире `header.data.fields`) — захист від форми відповіді,
  // якої компонент не очікував: коментар над `fieldsOf` описує, чому саме.
  if (header.data !== undefined && loadedFor !== documentId) {
    setLoadedFor(documentId);
    setDraft(draftOf(fieldsOf(header.data)));
  }

  const save = useMutation({
    mutationFn: (fields: readonly PatchHeaderField[]) => patchDocumentHeader(documentId, fields),
    onSuccess: (result) => {
      queryClient.setQueryData(['document-header', documentId], result);
      setDraft(draftOf(fieldsOf(result)));
      showDone(t('document.header.saved'));
    },
  });

  // ⚠ Поки триває перший запит — нічого: сторінка вже показує кілька
  // одночасних завантажень (`summary`/`tables`/`validation`), і ще один
  // скелет тут додав би шуму без інформації (той самий вибір, що
  // `SheetFillSummary`).
  if (header.isPending) return null;

  if (header.error !== undefined && header.error !== null) {
    return <ErrorAlert error={header.error} onRetry={() => void header.refetch()} />;
  }

  const fields = fieldsOf(header.data);

  // ⛔ Мутаційний доказ задачі: порожній перелік полів — панелі НЕМАЄ взагалі.
  if (fields.length === 0) return null;

  const dirty = fields.filter(
    (field) => !sameHeaderValue(effectiveValueOf(field, draft[field.code]), field.value),
  );

  function setField(code: string, value: unknown): void {
    setDraft((current) => ({ ...current, [code]: value }));
  }

  function resetDraft(): void {
    setDraft(draftOf(fieldsOf(header.data)));
  }

  return (
    <Stack gap="xs" data-testid="document-header-panel">
      <Title order={4}>{t('document.header.title')}</Title>

      {lookupError !== null && <ErrorAlert error={lookupError} onRetry={refetchLookups} />}
      {hasUnitFields && units.error !== null && (
        <ErrorAlert error={units.error} onRetry={() => void units.refetch()} />
      )}

      <Stack gap="xs">
        {fields.map((field) => (
          <HeaderFieldInput
            key={field.code}
            field={field}
            value={draft[field.code]}
            disabled={!canEdit || save.isPending}
            onChange={(value) => setField(field.code, value)}
            lookupEntries={
              field.lookupRegistryDefId === null || field.lookupRegistryDefId === undefined
                ? EmptyLookupEntries
                : (lookupEntriesByRegistryId.get(field.lookupRegistryDefId) ?? EmptyLookupEntries)
            }
            lookupPending={
              field.lookupRegistryDefId === null || field.lookupRegistryDefId === undefined
                ? false
                : (lookupPendingByRegistryId.get(field.lookupRegistryDefId) ?? registriesList.isPending)
            }
            units={units.data ?? null}
          />
        ))}
      </Stack>

      {canEdit && (
        <Group gap="xs">
          <Button
            size="xs"
            disabled={dirty.length === 0}
            loading={save.isPending}
            onClick={() =>
              save.mutate(dirty.map((field) => patchFieldOf(field, effectiveValueOf(field, draft[field.code]))))
            }
          >
            {t('common.save')}
          </Button>

          {dirty.length > 0 && (
            <Button size="xs" variant="default" disabled={save.isPending} onClick={resetDraft}>
              {t('common.cancel')}
            </Button>
          )}
        </Group>
      )}

      {save.error !== undefined && save.error !== null && <ErrorAlert error={save.error} />}
    </Stack>
  );
}

/** Один рядок панелі: підпис поля й компонент вводу за `dataType`. */
function HeaderFieldInput({
  field,
  value,
  disabled,
  onChange,
  lookupEntries,
  lookupPending,
  units,
}: {
  field: DocumentHeaderField;
  value: unknown;
  disabled: boolean;
  onChange: (value: unknown) => void;
  /** Записи довідника поля (лише для `dataType === 'Lookup'` із заданим `lookupRegistryDefId`). */
  lookupEntries: readonly RegistryEntryDto[];
  /** Чи довідник ЦЬОГО поля ще завантажується (окремий запит на довідник). */
  lookupPending: boolean;
  /** Перелік одиниць для полів `Unit`; `null` — ще не приїхав. */
  units: readonly UnitRef[] | null;
}): JSX.Element {
  const label = `${localized(field.label)}${field.isRequired ? ' *' : ''}`;

  if (field.dataType === 'Bool') {
    return (
      <Checkbox
        label={label}
        disabled={disabled}
        checked={value === true}
        indeterminate={value !== true && value !== false}
        onChange={(event) => onChange(event.currentTarget.checked)}
        data-header-field={field.code}
      />
    );
  }

  if (field.dataType === 'Date') {
    return (
      <Suspense fallback={null}>
        <DateInput
          label={label}
          valueFormat="YYYY-MM-DD"
          clearable
          disabled={disabled}
          value={typeof value === 'string' ? dateOf(value) : null}
          onChange={(next) => onChange(next === null ? null : isoDateOf(next))}
          data-header-field={field.code}
        />
      </Suspense>
    );
  }

  if (field.dataType === 'Unit') {
    // ⛔ `R-01`: одиниця — вибір зі списку за кодом, а не номер текстом.
    // Чернетка — рядок ідентифікатора, як і в `Lookup`; числом його робить
    // `coerce(raw, 'Unit')` при збереженні (`edits.ts`).
    const selectedId = typeof value === 'string' && value.trim().length > 0 ? Number(value) : null;
    const selectedIdValid = selectedId !== null && Number.isFinite(selectedId);
    const known = selectedIdValid && (units ?? []).some((unit) => unit.id === selectedId);
    const options = [
      ...(units ?? []).map((unit) => ({ value: String(unit.id), label: `${unit.code} · ${unit.dimensionCode}` })),
      // ⚠ Одиниця, якої в переліку немає, лишається видимою — дані є.
      ...(selectedIdValid && !known
        ? [{ value: String(selectedId), label: unitCellDisplay(selectedId, units ?? []) }]
        : []),
    ];

    return (
      <Select
        label={label}
        disabled={disabled || units === null}
        searchable
        clearable
        nothingFoundMessage={units === null ? t('grid.listLoading') : t('grid.listNothingFound')}
        data={options}
        value={selectedIdValid ? String(selectedId) : null}
        onChange={(next) => onChange(next ?? '')}
        data-header-field={field.code}
      />
    );
  }

  if (field.dataType === 'Lookup') {
    // ⛔ Захисний фолбек: контракт (`4f167396`) заповнює
    // `lookupRegistryDefId` для КОЖНОГО поля `Lookup`, але DTO лишає його
    // `null`-опційним — поле без довідника деградує до старого поводження
    // (сире число текстом), а не падає й не ховає дані.
    if (field.lookupRegistryDefId === null || field.lookupRegistryDefId === undefined) {
      return (
        <TextInput
          label={label}
          description={t('document.header.lookupHint')}
          disabled={disabled}
          value={typeof value === 'string' ? value : ''}
          onChange={(event) => onChange(event.currentTarget.value)}
          data-header-field={field.code}
        />
      );
    }

    // ⚠ Чернетка Lookup-поля — той самий РЯДОК, що для Int/Decimal (`Draft`,
    // коментар угорі файлу): `effectiveValueOf` уже приводить його до числа
    // через `coerce(raw, 'Lookup')` при збереженні (`edits.ts`, той самий
    // приймач, що й `LookupCellEditor` шле в `PatchCells`). Тут лишається
    // лише перевести рядок ⇄ вибір `Select`.
    const selectedId = typeof value === 'string' && value.trim().length > 0 ? Number(value) : null;
    const selectedIdValid = selectedId !== null && Number.isFinite(selectedId);

    // ⛔ Той самий резолв, що `LookupCellEditor.ts` для комірки сітки: запис,
    // якого серед завантажених `entries` немає (видалили з довідника,
    // застарілий кеш), — фолбек на сирий ідентифікатор ТЕКСТОМ
    // (`lookupCellDisplay`, той самий фолбек, повторений тут), а не порожнеча
    // й не падіння. Mantine `Select` показує підпис лише для значення, яке Є
    // в `data` — тому такий запис і додається синтетичною опцією.
    const known = selectedIdValid && lookupEntries.some((entry) => entry.id === selectedId);
    const options = [
      ...lookupEntries.map((entry) => ({ value: String(entry.id), label: entry.display })),
      ...(selectedIdValid && !known
        ? [{ value: String(selectedId), label: lookupCellDisplay(selectedId, lookupEntries) }]
        : []),
    ];

    return (
      <Select
        label={label}
        disabled={disabled || lookupPending}
        searchable
        clearable
        nothingFoundMessage={
          lookupPending ? t('document.header.lookupLoading') : t('document.header.lookupEmpty')
        }
        data={options}
        value={selectedIdValid ? String(selectedId) : null}
        // ⛔ Порожній вибір (X у `clearable`) — це «прибрати вибір», не
        // «нічого не сталося»: `onChange('')` таки викликається (той самий
        // аргумент, що в `LookupCellEditor.render`: `save(null)` на порожній
        // опції), інакше очистити раз обране поле стало б неможливим.
        onChange={(next) => onChange(next ?? '')}
        data-header-field={field.code}
      />
    );
  }

  return (
    <TextInput
      label={label}
      disabled={disabled}
      value={typeof value === 'string' ? value : ''}
      onChange={(event) => onChange(event.currentTarget.value)}
      data-header-field={field.code}
    />
  );
}
