import { useMemo, useState, type JSX } from 'react';
import { Alert, Button, Group, NativeSelect, NumberInput, Select, Stack, Textarea, TextInput } from '@mantine/core';
import type { OutOfWindowBehavior, PeriodAccessRuleKind, RowKind, TemplateStructureDto } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { ConfirmModal } from '@/shared/ui/ConfirmModal';
import { outOfWindowLabel, periodRuleKindLabel, rowKindLabel } from './enumLabels';
import {
  OutOfWindowBehaviors,
  PeriodAccessRuleKinds,
  RowKinds,
  whyCannotCreatePeriodAccessRule,
  type CreatePeriodAccessRuleDraft,
  type PeriodAccessRuleBlocker,
  type UpdatePeriodAccessRuleDraft,
} from './periodAccessRule';

/** Роль у переліку вибору; `null` у пропі — перелік ролей недоступний. */
export interface RoleOption {
  readonly id: number;
  readonly label: string;
}

interface Option {
  readonly value: string;
  readonly label: string;
}

/**
 * Варіанти вибору аркуша, таблиці й колонки-джерела зі структури версії
 * (X-15, четвертий раунд UX).
 *
 * ⛔ Доти всі чотири прив'язки правила вводилися СИРИМИ ідентифікаторами:
 * людина мала знати `SheetDefId` чи `ColumnDefId`, яких екран структури ніде
 * не показує. Тепер — назва й код, а ідентифікатор лишається внутрішньою
 * справою форми.
 *
 * ⚠ Таблиці звужуються вибраним аркушем: правило з аркушем і таблицею ІНШОГО
 * аркуша не діяло б ніде, а сервер прийняв би його мовчки.
 */
export function structureOptions(
  structure: TemplateStructureDto | undefined,
  sheetDefId: number | null,
): { sheets: Option[]; tables: Option[]; lookupColumns: Option[] } {
  const sheets = [...(structure?.sheets ?? [])].sort((a, b) => a.ordinal - b.ordinal);

  return {
    sheets: sheets.map((sheet) => ({
      value: String(sheet.id),
      label: `${localized(sheet.nameL10n) || sheet.code} (${sheet.code})`,
    })),
    tables: sheets
      .filter((sheet) => sheetDefId === null || sheet.id === sheetDefId)
      .flatMap((sheet) =>
        sheet.tables.map((table) => ({
          value: String(table.id),
          label: `${sheet.code} · ${localized(table.nameL10n) || table.code} (${table.code})`,
        })),
      ),
    lookupColumns: sheets.flatMap((sheet) =>
      sheet.tables.flatMap((table) =>
        table.columns
          .filter((column) => column.dataType === 'Lookup')
          .map((column) => ({
            value: String(column.id),
            label: `${table.code}.${column.code} — ${localized(column.headerL10n) || column.code}`,
          })),
      ),
    ),
  };
}

const toId = (value: string | null): number | null => (value === null ? null : Number(value));
const fromId = (id: number | null): string | null => (id === null ? null : String(id));

/**
 * Прив'язка правила — аркуш, таблиця, роль — однаково для обох форм.
 *
 * ⚠ Ролі: `null` — перелік ролей недоступний (немає права
 * `Security.ManageRoles` або запит відмовив). Тоді лишається числове поле з
 * тим самим підписом: гірше за вибір, але не глухий кут.
 */
function TargetFields({
  structure,
  roles,
  disabled,
  sheetDefId,
  tableDefId,
  roleId,
  withHints,
  onChange,
}: {
  structure: TemplateStructureDto | undefined;
  roles: readonly RoleOption[] | null;
  disabled: boolean;
  sheetDefId: number | null;
  tableDefId: number | null;
  roleId: number | null;
  withHints: boolean;
  onChange: (next: { sheetDefId: number | null; tableDefId: number | null; roleId: number | null }) => void;
}): JSX.Element {
  const options = useMemo(() => structureOptions(structure, sheetDefId), [structure, sheetDefId]);
  const roleOptions = useMemo(
    () => (roles ?? []).map((role) => ({ value: String(role.id), label: role.label })),
    [roles],
  );

  return (
    <>
      <Select
        label={t('periodRules.sheet')}
        description={withHints ? t('periodRules.sheetDefIdHint') : undefined}
        disabled={disabled}
        searchable
        clearable
        data={options.sheets}
        value={fromId(sheetDefId)}
        onChange={(value) => {
          const nextSheet = toId(value);
          // ⚠ Таблиця іншого аркуша скидається разом зі зміною аркуша — див.
          // `structureOptions`.
          const keepTable =
            tableDefId !== null &&
            nextSheet !== null &&
            (structure?.sheets.find((s) => s.id === nextSheet)?.tables.some((tb) => tb.id === tableDefId) ?? false);
          onChange({ sheetDefId: nextSheet, tableDefId: nextSheet === null || keepTable ? tableDefId : null, roleId });
        }}
      />

      <Select
        label={t('periodRules.table')}
        description={withHints ? t('periodRules.tableDefIdHint') : undefined}
        disabled={disabled}
        searchable
        clearable
        data={options.tables}
        value={fromId(tableDefId)}
        onChange={(value) => onChange({ sheetDefId, tableDefId: toId(value), roleId })}
      />

      {roles === null ? (
        <NumberInput
          label={t('periodRules.roleId')}
          description={withHints ? t('periodRules.roleIdHint') : undefined}
          disabled={disabled}
          value={roleId ?? ''}
          onChange={(value) => onChange({ sheetDefId, tableDefId, roleId: typeof value === 'number' ? value : null })}
        />
      ) : (
        <Select
          label={t('periodRules.role')}
          description={withHints ? t('periodRules.roleIdHint') : undefined}
          disabled={disabled}
          searchable
          clearable
          data={roleOptions}
          value={fromId(roleId)}
          onChange={(value) => onChange({ sheetDefId, tableDefId, roleId: toId(value) })}
        />
      )}
    </>
  );
}

/**
 * Форма нового правила доступу до періоду (`ФВ-2.15`, W5.4) — за зразком
 * `SheetEditor`.
 *
 * ⚠ Вид правила (`RuleKind`) і його специфічний параметр задаються ЛИШЕ тут,
 * при створенні: `PUT …/{id}` (див. `PeriodAccessRuleManager` нижче) їх не
 * приймає — той самий поділ, що в `SheetEditor` між кодом (незмінним) і
 * рештою полів.
 */
export function PeriodAccessRuleEditor({
  draft,
  structure,
  roles,
  disabled,
  saving,
  onChange,
  onSubmit,
}: {
  draft: CreatePeriodAccessRuleDraft;
  structure: TemplateStructureDto | undefined;
  roles: readonly RoleOption[] | null;
  disabled: boolean;
  saving: boolean;
  onChange: (next: CreatePeriodAccessRuleDraft) => void;
  onSubmit: () => void;
}): JSX.Element {
  const blocker = whyCannotCreatePeriodAccessRule(draft);
  const { lookupColumns } = useMemo(() => structureOptions(structure, null), [structure]);

  return (
    <Stack gap="sm">
      <NativeSelect
        label={t('periodRules.kind')}
        description={t('periodRules.kindHint')}
        data={PeriodAccessRuleKinds.map((kind) => ({ value: kind, label: periodRuleKindLabel(kind) }))}
        disabled={disabled}
        value={draft.ruleKind}
        onChange={(event) =>
          onChange({ ...draft, ruleKind: event.currentTarget.value as PeriodAccessRuleKind })
        }
      />

      <NativeSelect
        label={t('periodRules.outOfWindow')}
        description={t('periodRules.outOfWindowHint')}
        data={OutOfWindowBehaviors.map((b) => ({ value: b, label: outOfWindowLabel(b) }))}
        disabled={disabled}
        value={draft.onOutOfWindow}
        onChange={(event) =>
          onChange({ ...draft, onOutOfWindow: event.currentTarget.value as Exclude<OutOfWindowBehavior, 'Hide'> })
        }
      />

      <TargetFields
        structure={structure}
        roles={roles}
        disabled={disabled}
        sheetDefId={draft.sheetDefId}
        tableDefId={draft.tableDefId}
        roleId={draft.roleId}
        withHints
        onChange={(target) => onChange({ ...draft, ...target })}
      />

      <NativeSelect
        label={t('periodRules.rowKind')}
        description={t('periodRules.rowKindHint')}
        data={[
          { value: '', label: t('periodRules.rowKindAny') },
          ...RowKinds.map((k) => ({ value: k, label: rowKindLabel(k) })),
        ]}
        disabled={disabled}
        value={draft.rowKind ?? ''}
        onChange={(event) =>
          onChange({
            ...draft,
            rowKind: event.currentTarget.value === '' ? null : (event.currentTarget.value as RowKind),
          })
        }
      />

      {draft.ruleKind === 'EditablePeriodOnly' && (
        <Group grow>
          <NumberInput
            label={t('periodRules.fromSequence')}
            disabled={disabled}
            value={draft.fromSequence ?? ''}
            onChange={(value) =>
              onChange({ ...draft, fromSequence: typeof value === 'number' ? value : null })
            }
          />
          <NumberInput
            label={t('periodRules.toSequence')}
            disabled={disabled}
            value={draft.toSequence ?? ''}
            onChange={(value) => onChange({ ...draft, toSequence: typeof value === 'number' ? value : null })}
          />
        </Group>
      )}

      {draft.ruleKind === 'SourceWindow' && (
        <Select
          label={t('periodRules.sourceColumn')}
          description={t('periodRules.sourceColumnDefIdHint')}
          disabled={disabled}
          searchable
          clearable
          nothingFoundMessage={t('periodRules.sourceColumnEmpty')}
          data={lookupColumns}
          value={fromId(draft.sourceColumnDefId)}
          onChange={(value) => onChange({ ...draft, sourceColumnDefId: toId(value) })}
        />
      )}

      {draft.ruleKind === 'RelativeWindow' && (
        <NumberInput
          label={t('periodRules.relativeOffset')}
          description={t('periodRules.relativeOffsetHint')}
          disabled={disabled}
          value={draft.relativeOffset ?? ''}
          onChange={(value) =>
            onChange({ ...draft, relativeOffset: typeof value === 'number' ? value : null })
          }
        />
      )}

      {draft.ruleKind === 'Expression' && (
        <Textarea
          label={t('periodRules.condition')}
          description={t('periodRules.conditionHint')}
          autosize
          minRows={2}
          disabled={disabled}
          value={draft.conditionExpr}
          onChange={(event) => onChange({ ...draft, conditionExpr: event.currentTarget.value })}
        />
      )}

      {blocker !== null && <Alert color="statusWarning">{createBlockerLabel(blocker)}</Alert>}

      <Group justify="flex-end">
        <Button disabled={disabled || blocker !== null} loading={saving} onClick={onSubmit}>
          {t('periodRules.add')}
        </Button>
      </Group>
    </Stack>
  );
}

/**
 * Форма правки й видалення НАЯВНОГО правила за його `id`.
 *
 * ⛔ Без цієї форми `PUT`/`DELETE …/period-access-rules/{id}` лишалися б
 * недосяжними з інтерфейсу: сервер поки не віддає перелік готових правил
 * версії (лише матрицю доступу — `AccessMatrix` — і оцінку по клітинках, а не
 * самі рядки з їхніми `id`). Тому `id` тут вводить людина: показаний одразу
 * після створення правила вище або взятий із журналу структурних змін.
 * Це чесна деградація, а не вигадана структура: коли з'явиться перелік
 * правил, ця форма адресуватиметься з нього, а не текстовим полем.
 *
 * ⛔ X-15/R-06 (четвертий раунд UX): «Remove rule» і «Save rule» ділили ОДИН
 * `loading` — крутилися обидві кнопки, хоч би яку натиснули, — а видалення
 * йшло одразу, без підтвердження. Тепер у кожної кнопки власний стан, і
 * видалення питає.
 */
export function PeriodAccessRuleManager({
  ruleId,
  draft,
  structure,
  roles,
  disabled,
  saving,
  deleting,
  onRuleIdChange,
  onChange,
  onSave,
  onDelete,
}: {
  ruleId: number | null;
  draft: UpdatePeriodAccessRuleDraft;
  structure: TemplateStructureDto | undefined;
  roles: readonly RoleOption[] | null;
  disabled: boolean;
  saving: boolean;
  deleting: boolean;
  onRuleIdChange: (id: number | null) => void;
  onChange: (next: UpdatePeriodAccessRuleDraft) => void;
  onSave: () => void;
  onDelete: () => void;
}): JSX.Element {
  const [confirming, setConfirming] = useState(false);
  const busy = saving || deleting;

  return (
    <Stack gap="sm">
      <TextInput
        label={t('periodRules.manageId')}
        description={t('periodRules.manageIdHint')}
        value={ruleId ?? ''}
        disabled={disabled}
        onChange={(event) => {
          // ⛔ Аудит 2026-09-16 §10.8: тут стояло `Number(value)`, і нечисловий
          // ввід ставав `NaN`. Доступність кнопок нижче питає `ruleId === null`,
          // а `NaN === null` — `false`: «Зберегти» і «Видалити» ставали
          // активними з ідентифікатором, якого не існує, і вели в
          // `PUT/DELETE …/period-access-rules/NaN` — адресу, на яку сервер
          // відповідає 400/404. Кнопка обіцяла дію, яку неможливо виконати.
          //
          // ⚠ Вимога саме ЦІЛОГО додатного: `12.5` і `0` — теж не
          // ідентифікатори, і пускати їх далі означало б лише відкласти ту саму
          // відмову сервера. Нечисловий ввід стирається з поля одразу — для
          // поля, у яке вводять ID, це чесніше за «NaN» на екрані.
          const parsed = Number(event.currentTarget.value.trim());

          onRuleIdChange(Number.isInteger(parsed) && parsed > 0 ? parsed : null);
        }}
      />

      <NativeSelect
        label={t('periodRules.outOfWindow')}
        data={OutOfWindowBehaviors.map((b) => ({ value: b, label: outOfWindowLabel(b) }))}
        disabled={disabled || ruleId === null}
        value={draft.onOutOfWindow}
        onChange={(event) =>
          onChange({ ...draft, onOutOfWindow: event.currentTarget.value as Exclude<OutOfWindowBehavior, 'Hide'> })
        }
      />

      <TargetFields
        structure={structure}
        roles={roles}
        disabled={disabled || ruleId === null}
        sheetDefId={draft.sheetDefId}
        tableDefId={draft.tableDefId}
        roleId={draft.roleId}
        withHints={false}
        onChange={(target) => onChange({ ...draft, ...target })}
      />

      <Group justify="flex-end">
        <Button
          variant="default"
          color="statusError"
          disabled={disabled || ruleId === null || saving}
          loading={deleting}
          onClick={() => setConfirming(true)}
        >
          {t('periodRules.delete')}
        </Button>
        <Button disabled={disabled || ruleId === null || deleting} loading={saving} onClick={onSave}>
          {t('periodRules.save')}
        </Button>
      </Group>

      <ConfirmModal
        opened={confirming && ruleId !== null}
        title={t('periodRules.deleteTitle', { id: ruleId ?? '' })}
        text={t('periodRules.deleteText')}
        verb={t('periodRules.delete')}
        isPending={deleting}
        confirmDisabled={busy && !deleting}
        onConfirm={() => {
          onDelete();
        }}
        onClose={() => setConfirming(false)}
      />
    </Stack>
  );
}

/** Підпис причини, з якої нове правило ще не можна завести. */
function createBlockerLabel(blocker: PeriodAccessRuleBlocker): string {
  switch (blocker) {
    case 'Target':
      return t('periodRules.errTarget');
    case 'SourceColumn':
      return t('periodRules.errSourceColumn');
    case 'RelativeOffset':
      return t('periodRules.errRelativeOffset');
    case 'Condition':
      return t('periodRules.errCondition');
    default:
      return blocker;
  }
}
