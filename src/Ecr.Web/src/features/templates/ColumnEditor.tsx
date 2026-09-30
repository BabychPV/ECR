import { lazy, Suspense, useEffect, useMemo, type JSX } from 'react';
import { Alert, Button, Group, NumberInput, Select, Skeleton, Stack, Switch, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { apiFetch } from '@/api/client';
import { queryKeys } from '@/api/queryKeys';
import type { RegistryDefDto, UnitRef } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { LocalizedInput } from '@/shared/ui/LocalizedInput';
import {
  type ColumnBlocker,
  type ColumnDraft,
  columnTakesUnit,
  EditableColumnDataTypes,
  whyCannotSaveColumn,
} from './column';
import { dataTypeLabel } from './enumLabels';
import { emptyStyleDraft, styleDraftOf, type StyleDefDto } from './style';

/**
 * ⛔ Директива registry-lookup / cell-style, PR B1 — гейт бюджету `D-132`
 * (`TemplateVersionPage` — 252.3 КБ gzip проти межі 250, знайдено `client`
 * гейтом CI): статичний імпорт `StyleEditor.tsx` (кольори, рамка,
 * вирівнювання — кілька компонентів `@mantine/core`, які більше НІХТО в
 * застосунку не використовував) вкидав свою вагу в бібліотеку колонки
 * КОЖНОГО, хто відкриває конструктор шаблону, а не лише того, хто
 * увімкнув «Custom style». Той самий прийом, що вже рятує `DocumentGrid`
 * (`DocumentPage.tsx`, RevoGrid 79% ваги маршруту) і Monaco
 * (`ExpressionEditor.tsx`, 818 КБ): `lazy()` виносить чанк ЗА межі
 * статичного графа `check-bundle-budget.mjs` — браузер вантажить його лише
 * тоді, коли перемикач справді відкриває панель.
 */
const StyleEditor = lazy(async () => ({
  default: (await import('./StyleEditor')).StyleEditor,
}));

/**
 * Форма колонки — другий вертикальний зріз авторства структури шаблону
 * (`W5.2`), за зразком `SheetEditor.tsx` (`ФВ-2.1`, `W5.0`).
 *
 * ⚠ Форма нічого не вирішує про стан версії — так само, як `SheetEditor`:
 * `disabled` лише заважає заповнювати те, що однаково не збережеться
 * (`isEditable` рахує сервер, відхиляє домен, `ECR-TMPL-0409`).
 */
export function ColumnEditor({
  draft,
  disabled,
  saving,
  templateVersionId,
  onChange,
  onSubmit,
  onCancel,
}: {
  draft: ColumnDraft;
  disabled: boolean;
  saving: boolean;

  /**
   * Версія-чернетка (директива registry-lookup / cell-style, PR B1) —
   * потрібна лише для `GET …/styles` (перелік наявних стилів для
   * повторного використання); саму колонку зберігає викликач.
   */
  templateVersionId: number;
  onChange: (next: ColumnDraft) => void;
  onSubmit: () => void;
  onCancel: () => void;
}): JSX.Element {
  const blocker = whyCannotSaveColumn(draft);
  const isDecimal = draft.dataType === 'Decimal';
  const isLookup = draft.dataType === 'Lookup';
  const takesUnit = columnTakesUnit(draft.dataType);

  // ⛔ R-07: одиницю колонки не було де задати — форма її не показувала, а
  // `unitId` лише носився від сервера назад. Той самий ключ і строк
  // свіжості, що в `CalculationResultsPanel`: каталог одиниць міняється рідко.
  const units = useQuery({
    queryKey: ['units'],
    queryFn: () => apiFetch<UnitRef[]>('/api/v1/units'),
    staleTime: 60 * 60 * 1000,
    enabled: takesUnit,
  });

  const unitOptions = useMemo(
    () =>
      (units.data ?? []).map((unit) => ({
        value: String(unit.id),
        label: `${unit.code} (${unit.dimensionCode})`,
      })),
    [units.data],
  );

  // ⛔ Директива registry-lookup, PR A3: колонку `Lookup` конфігурували
  // сирим числовим `RegistryDefId` — автор шаблону мав знати ідентифікатор
  // напам'ять, узятий десь поза цим екраном. Запит лінивий (лише коли
  // форма відкрита на колонці Lookup), бо саме тоді список і потрібен.
  const registries = useQuery({
    queryKey: queryKeys.registries.list(),
    queryFn: () => apiFetch<RegistryDefDto[]>('/api/v1/registries'),
    enabled: isLookup,
  });

  // ⚠ Мемоізовано: `[]`-літерал у пропі `data` перебудовувався б щорендеру,
  // поки запит іще `pending`, — новий референс масиву на кожен рендер.
  const registryOptions = useMemo(
    () =>
      (registries.data ?? []).map((registry) => ({
        value: String(registry.id),
        label: `${localized(registry.nameL10n)} (${registry.code})`,
      })),
    [registries.data],
  );

  // ⛔ Директива registry-lookup / cell-style, PR B1: перелік стилів версії —
  // щоб увімкнення «власного стилю» на колонці, яка вже МАЄ `styleId`
  // (збережений раніше), відкривало форму з наявними значеннями, а не
  // порожньою чернеткою, яка мовчки перезаписала б стиль при збереженні.
  const styles = useQuery({
    queryKey: queryKeys.templates.stylesOf(templateVersionId),
    queryFn: () =>
      apiFetch<StyleDefDto[]>(`/api/v1/template-versions/${String(templateVersionId)}/styles`),
    enabled: draft.styleId !== null,
  });

  const existingStyle = styles.data?.find((s) => s.id === draft.styleId) ?? null;

  /**
   * ⛔ Директива D15 §0, правило L10: «стан стилю невідомий» — це НЕ «стилю
   * немає». Колонка вже має `styleId`, а перелік стилів або відмовлено, або
   * ще в дорозі: `existingStyle` тоді `null` — рівно те саме значення, що й у
   * колонки БЕЗ стилю. Увімкнення перемикача в цьому стані відкривало порожню
   * чернетку з кодом `${code}Style` — тобто саме той стиль, який форма й
   * створила колись, — і `saveColumn` (`columnApi.ts`) записував його першим
   * `PUT …/styles/{code}`, мовчки затираючи збережені значення дефолтами.
   * Той самий запобіжник, що в `PeriodsPage` (#452): доки не знаємо — не
   * пускаємо, і кажемо чому.
   *
   * ⚠ Збереження колонки при цьому НЕ блокується: `columnBody` везе
   * `styleId: draft.styleId`, тобто вже персистентний ідентифікатор, і поки
   * чернетки стилю немає (`draft.style === null`), `saveColumn` до
   * `…/styles/{code}` взагалі не звертається. Заблокувати ще й «Зберегти»
   * означало б відняти правку решти полів, не відвернувши жодного затирання.
   */
  const styleStateUnknown =
    draft.styleId !== null && (styles.error !== null || styles.data === undefined);

  // ⛔ Живий перегляд (не тест) знайшов реальну шорсткість: без цього ефекту
  // перемикач «власний стиль» показував ВИМКНЕНО для колонки, яка НАСПРАВДІ
  // вже має стиль (`draft.styleId !== null`, просто ще не завантажений у
  // форму, `draft.style === null`) — той самий стан, що й «стилю немає
  // взагалі», хоча дані різні. Автозаповнення панелі, щойно перелік стилів
  // версії довантажиться, прибирає цю двозначність: перемикач ЗАВЖДИ
  // відповідає факту «колонка має стиль», а не «форму щойно відкрили».
  useEffect(() => {
    if (draft.style === null && existingStyle !== null) {
      onChange({ ...draft, style: styleDraftOf(existingStyle) });
    }
    // ⚠ Лише `existingStyle`: `draft`/`onChange` у залежностях викликали б
    // цей ефект на КОЖНУ зміну поля форми (включно з тими, що заповнює сам
    // перегляд стилю), а не лише на довантаження списку стилів.
  }, [existingStyle]);

  return (
    <Stack gap="sm">
      <TextInput
        label={t('columns.code')}
        description={t('columns.codeHint')}
        value={draft.code}
        disabled={disabled || !draft.isNew}
        onChange={(event) => onChange({ ...draft, code: event.currentTarget.value })}
      />

      <LocalizedInput
        label={t('columns.header')}
        description={t('columns.headerHint')}
        value={draft.headerL10n}
        onChange={(headerL10n) => onChange({ ...draft, headerL10n })}
      />

      <Select
        label={t('columns.dataType')}
        description={t('columns.dataTypeHint')}
        // ⛔ X-16: у переліку стояли сирі значення переліку (`Decimal`, `Lookup`).
        data={EditableColumnDataTypes.map((type) => ({ value: type, label: dataTypeLabel(type) }))}
        value={draft.dataType}
        disabled={disabled || !draft.isNew}
        allowDeselect={false}
        onChange={(value) => {
          if (value !== null) onChange({ ...draft, dataType: value as ColumnDraft['dataType'] });
        }}
      />

      <NumberInput
        label={t('columns.ordinal')}
        description={t('columns.ordinalHint')}
        disabled={disabled}
        value={draft.ordinal ?? ''}
        onChange={(value) =>
          onChange({ ...draft, ordinal: typeof value === 'number' ? value : draft.ordinal })
        }
      />

      <Switch
        label={t('columns.required')}
        disabled={disabled}
        checked={draft.isRequired}
        onChange={(event) => onChange({ ...draft, isRequired: event.currentTarget.checked })}
      />

      <Switch
        label={t('columns.readOnly')}
        disabled={disabled}
        checked={draft.isReadOnly}
        onChange={(event) => onChange({ ...draft, isReadOnly: event.currentTarget.checked })}
      />

      <Switch
        label={t('columns.hidden')}
        description={t('columns.hiddenHint')}
        disabled={disabled}
        checked={draft.isHidden}
        onChange={(event) => onChange({ ...draft, isHidden: event.currentTarget.checked })}
      />

      <TextInput
        label={t('columns.displayFormat')}
        description={t('columns.displayFormatHint')}
        disabled={disabled}
        value={draft.displayFormat}
        onChange={(event) => onChange({ ...draft, displayFormat: event.currentTarget.value })}
      />

      <TextInput
        label={t('columns.defaultValue')}
        description={t('columns.defaultValueHint')}
        disabled={disabled}
        value={draft.defaultValue}
        onChange={(event) => onChange({ ...draft, defaultValue: event.currentTarget.value })}
      />

      {isDecimal && (
        <Group grow>
          <NumberInput
            label={t('columns.precision')}
            disabled={disabled}
            min={1}
            value={draft.precision ?? ''}
            onChange={(value) =>
              onChange({ ...draft, precision: typeof value === 'number' ? value : null })
            }
          />
          <NumberInput
            label={t('columns.scale')}
            disabled={disabled}
            min={0}
            value={draft.scale ?? ''}
            onChange={(value) => onChange({ ...draft, scale: typeof value === 'number' ? value : null })}
          />
        </Group>
      )}

      {takesUnit && units.error !== null && (
        <ErrorAlert error={units.error} onRetry={() => void units.refetch()} />
      )}

      {takesUnit && units.error === null && units.isPending && (
        <Skeleton height={60} radius="sm" data-unit-select="pending" />
      )}

      {takesUnit && units.error === null && !units.isPending && (
        <Select
          label={t('columns.unit')}
          description={t('columns.unitHint')}
          disabled={disabled}
          searchable
          // ⚠ Прибрати одиницю можна лише в НОВОЇ колонки: сервер читає
          // `unitId: null` як «не змінювати» (`SaveColumnDefHandler.ApplyOptionalFields`),
          // тож «очищене» поле наявної колонки після збереження мовчки
          // повернулося б — обіцянка, якої форма не виконає.
          clearable={draft.isNew}
          allowDeselect={draft.isNew}
          nothingFoundMessage={t('columns.unitEmpty')}
          data={unitOptions}
          value={draft.unitId === null ? null : String(draft.unitId)}
          onChange={(value) => onChange({ ...draft, unitId: value === null ? null : Number(value) })}
        />
      )}

      {/*
        ⛔ Директива D15 §0, правило L10: відмова `GET /api/v1/registries`
        давала порожній `Select` із підписом «довідників не знайдено» — автор
        шаблону читав це як «довідників не завели» і йшов заводити ще один.
        Порядок той самий, що в `AsyncBoundary` і в `LocalizedInput` (#444):
        `error` → `isPending` → дані. Елемент, для якого даних немає, не
        малюється зовсім (`D15-06`).
      */}
      {isLookup && registries.error !== null && (
        <ErrorAlert error={registries.error} onRetry={() => void registries.refetch()} />
      )}

      {/*
        ⚠ «Ще вантажиться» теж не «порожньо»: до цієї правки перелік у дорозі
        показував той самий `nothingFoundMessage`, що й насправді порожній
        довідник, — і встигав спокусити відкрити випадний список раніше, ніж
        приїдуть опції.
      */}
      {isLookup && registries.error === null && registries.isPending && (
        <Skeleton height={60} radius="sm" data-registry-select="pending" />
      )}

      {isLookup && registries.error === null && !registries.isPending && (
        <Select
          label={t('columns.lookupRegistryDefId')}
          description={t('columns.lookupRegistryDefIdHint')}
          disabled={disabled}
          searchable
          nothingFoundMessage={t('columns.lookupRegistryDefIdEmpty')}
          data={registryOptions}
          // ⚠ Рядок, не число: Mantine `Select` завжди працює з текстовим
          // `value`. Наявні колонки з уже заданим (сирим) ідентифікатором
          // і далі показують правильно вибраний довідник — досить, щоб він
          // був серед завантажених `data` (зворотна сумісність, не міграція
          // даних).
          value={draft.lookupRegistryDefId === null ? null : String(draft.lookupRegistryDefId)}
          onChange={(value) =>
            onChange({ ...draft, lookupRegistryDefId: value === null ? null : Number(value) })
          }
        />
      )}

      {/* ⛔ Причина, з якої перемикач нижче недоступний, має бути видимою —
          інакше «не натискається» читається як поломка форми. */}
      {styleStateUnknown && styles.error !== null && (
        <ErrorAlert error={styles.error} onRetry={() => void styles.refetch()} />
      )}

      <Switch
        label={t('columns.customStyle')}
        description={t('columns.customStyleHint')}
        disabled={disabled || styleStateUnknown}
        checked={draft.style !== null}
        onChange={(event) => {
          if (event.currentTarget.checked) {
            // ⚠ Стиль, що вже прив'язаний до колонки, відкривається З ЙОГО
            // значеннями (`existingStyle`), а не порожньою чернеткою: інакше
            // збереження мовчки перезаписало б наявний стиль дефолтами.
            onChange({
              ...draft,
              style: existingStyle === null ? emptyStyleDraft(`${draft.code}Style`) : styleDraftOf(existingStyle),
            });
          } else {
            // ⛔ Вимкнути перемикач — це ВІДЧЕПИТИ стиль від колонки, а не
            // лише згорнути форму: `styleId: null` тут і в `columnBody`
            // (`column.ts`) — те саме поле, яке піде в PUT колонки.
            onChange({ ...draft, style: null, styleId: null });
          }
        }}
      />

      {draft.style !== null && (
        <Suspense fallback={<Skeleton height={220} radius="sm" />}>
          <StyleEditor
            draft={draft.style}
            disabled={disabled}
            onChange={(style) => onChange({ ...draft, style })}
          />
        </Suspense>
      )}

      {blocker !== null && <Alert color="statusWarning">{blockerLabel(blocker)}</Alert>}

      <Group>
        <Button variant="default" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button disabled={disabled || blocker !== null} loading={saving} onClick={onSubmit}>
          {t('columns.save')}
        </Button>
      </Group>
    </Stack>
  );
}

/** Підпис причини, з якої зберегти ще не можна. */
function blockerLabel(blocker: ColumnBlocker): string {
  switch (blocker) {
    case 'CodeEmpty':
      return t('columns.errCode');
    case 'CodeInvalid':
      return t('columns.errCodeInvalid');
    case 'Header':
      return t('columns.errHeader');
    case 'Scale':
      return t('columns.errScale');

    /*
     * ⛔ Обидві причини стилю доти падали в `default` і показувалися ГОЛИМ
     * кодом (`StyleCode`, `StyleFontSize`): людина бачила слово з переліку
     * розробника замість речення про те, що саме виправити.
     *
     * ⚠ `default` лишається — але тепер він означає рівно «причина, якої
     * клієнт ще не знає», і код у ньому виглядає як пропуск, а не як
     * нормальний підпис.
     */
    case 'StyleCode':
      return t('columns.errStyleCode');
    case 'StyleFontSize':
      return t('columns.errStyleFontSize');
    default:
      return blocker;
  }
}
