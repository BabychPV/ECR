import { lazy, Suspense, useState, type JSX } from 'react';
import { Button, Checkbox, Group, Stack, TextInput, Title } from '@mantine/core';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import type { components } from '@/api/schema';
import { coerce } from '@/features/grid/edits';
import { cellText, sameCellValue } from '@/features/grid/cellValue';
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
 * ⚠ Значення поля типу `Lookup` тут показується й редагується як сире число
 * `ValueRegistryEntryId` (той самий контракт, що комірка `Lookup` у сітці,
 * `LookupCellEditor.ts`), а не як назва запису довідника: `DocumentHeaderFieldDto`
 * не несе `lookupRegistryDefId` (на відміну від `HeaderFieldDefDto` адмінського
 * маршруту), тож клієнт не знає, ЯКИЙ довідник запитати за назвою запису.
 * Показ назви замість ідентифікатора — наступний крок, коли контракт віддасть
 * цей ідентифікатор і сюди (відкрите питання — у звіті).
 */

type DocumentHeaderDto = components['schemas']['DocumentHeaderDto'];
type DocumentHeaderField = components['schemas']['DocumentHeaderFieldDto'];
type PatchHeaderField = components['schemas']['PatchHeaderField'];

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

      <Stack gap="xs">
        {fields.map((field) => (
          <HeaderFieldInput
            key={field.code}
            field={field}
            value={draft[field.code]}
            disabled={!canEdit || save.isPending}
            onChange={(value) => setField(field.code, value)}
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
}: {
  field: DocumentHeaderField;
  value: unknown;
  disabled: boolean;
  onChange: (value: unknown) => void;
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

  // ⚠ `Lookup` — сире число `ValueRegistryEntryId`, не назва запису (коментар
  // угорі файлу: контракт не несе `lookupRegistryDefId` для полів шапки).
  const description = field.dataType === 'Lookup' ? t('document.header.lookupHint') : undefined;

  return (
    <TextInput
      label={label}
      description={description}
      disabled={disabled}
      value={typeof value === 'string' ? value : ''}
      onChange={(event) => onChange(event.currentTarget.value)}
      data-header-field={field.code}
    />
  );
}
