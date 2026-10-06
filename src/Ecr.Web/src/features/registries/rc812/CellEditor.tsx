import { Suspense, lazy, useRef, useState, type JSX, type KeyboardEvent } from 'react';
import { Select, TextInput } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { formatDateOnly, parseDateOnly } from '@/shared/format';
import { t } from '@/shared/i18n';
import { lookupLabel, useLookupOptions, useUnits } from './data';
import type { RegistryField } from './rowModel';

/**
 * Поле дати — за `import()`, як на `PeriodsPage`: `@mantine/dates` тягне `dayjs`, а редагує дату
 * не кожен, хто відкрив довідник.
 */
const DateInput = lazy(async () => ({ default: (await import('@/shared/dates/DateInputWithStyles')).DateInput }));

/** Куди перейти після підтвердження: `Enter` — униз, `Tab` — праворуч/ліворуч. */
export type CommitMove = 'down' | 'right' | 'left' | 'none';

export interface CellEditorProps {
  readonly field: RegistryField;
  /** Підпис для читача екрана: назва поля й номер рядка. */
  readonly label: string;
  readonly value: string | null;
  readonly display: string;
  /** Код довідника-цілі для `Lookup`; `null` — ціль невідома. */
  readonly lookupCode: string | null;
  readonly asOf: string | null;
  readonly onCommit: (value: string | null, display: string | undefined, move: CommitMove) => void;
  readonly onCancel: () => void;
}

/**
 * Типізований редактор комірки (§8.4 «Типізовані редактори»): число (кома — за правилами сервера,
 * `normalizeCellInput`),
 * так/ні, дата, пікер `Lookup` з пошуком (показує назву, не id), одиниця.
 */
export function CellEditor(props: CellEditorProps): JSX.Element {
  switch (props.field.dataType) {
    case 'Lookup':
      return <LookupEditor {...props} />;
    case 'Unit':
      return <UnitEditor {...props} />;
    case 'Bool':
      return (
        <ChoiceEditor
          {...props}
          data={[
            { value: 'true', label: t('registries.yes') },
            { value: 'false', label: t('registries.data.no') },
          ]}
        />
      );
    case 'Date':
      return <DateEditor {...props} />;
    default:
      return <TextEditor {...props} />;
  }
}

/** Клавіші підтвердження/скасування, спільні для всіх редакторів. */
function commitKeys(
  event: KeyboardEvent,
  commit: (move: CommitMove) => void,
  cancel: () => void,
): void {
  if (event.key === 'Escape') {
    event.preventDefault();
    event.stopPropagation();
    cancel();
  } else if (event.key === 'Enter') {
    event.preventDefault();
    event.stopPropagation();
    commit('down');
  } else if (event.key === 'Tab') {
    event.preventDefault();
    event.stopPropagation();
    commit(event.shiftKey ? 'left' : 'right');
  }
}

function TextEditor({ field, label, value, onCommit, onCancel }: CellEditorProps): JSX.Element {
  const [text, setText] = useState(value ?? '');
  const numeric = field.dataType === 'Int' || field.dataType === 'Decimal';

  // ⚠ Підтвердження рівно одне: `Enter` закриває редактор, і `blur` при його знятті не має
  // вдруге писати те саме значення (другий запис після переходу — уже в сусідню комірку).
  const done = useRef(false);
  const commit = (move: CommitMove): void => {
    if (done.current) return;
    done.current = true;
    onCommit(text, undefined, move);
  };

  return (
    <TextInput
      size="xs"
      autoFocus
      aria-label={label}
      value={text}
      inputMode={numeric ? 'decimal' : undefined}
      onChange={(event) => setText(event.currentTarget.value)}
      onKeyDown={(event) =>
        commitKeys(event, commit, () => {
          done.current = true;
          onCancel();
        })
      }
      onBlur={() => commit('none')}
    />
  );
}

function ChoiceEditor({
  label,
  value,
  onCommit,
  onCancel,
  data,
}: CellEditorProps & { readonly data: { value: string; label: string }[] }): JSX.Element {
  return (
    <Select
      size="xs"
      autoFocus
      defaultDropdownOpened
      aria-label={label}
      data={data}
      value={value}
      clearable
      onChange={(next) => onCommit(next, next === null ? undefined : data.find((d) => d.value === next)?.label, 'none')}
      onKeyDown={(event) => {
        if (event.key === 'Escape' || event.key === 'Tab') commitKeys(event, () => onCancel(), onCancel);
      }}
      onBlur={onCancel}
    />
  );
}

function UnitEditor(props: CellEditorProps): JSX.Element {
  const units = useUnits(true);
  const data = (units.data ?? []).map((unit) => ({ value: String(unit.id), label: unit.code }));
  if (props.value !== null && !data.some((d) => d.value === props.value)) {
    data.unshift({ value: props.value, label: props.display || props.value });
  }

  return <ChoiceEditor {...props} data={data} />;
}

function LookupEditor({ label, value, display, lookupCode, asOf, onCommit, onCancel }: CellEditorProps): JSX.Element {
  const [search, setSearch] = useState('');
  const [debounced] = useDebouncedValue(search, 250);
  const options = useLookupOptions(lookupCode, debounced, asOf);

  const data = (options.data?.items ?? []).map((row) => ({ value: String(row.id), label: lookupLabel(row) }));
  if (value !== null && !data.some((d) => d.value === value)) data.unshift({ value, label: display || value });

  return (
    <Select
      size="xs"
      autoFocus
      defaultDropdownOpened
      searchable
      clearable
      aria-label={label}
      data={data}
      value={value}
      // Фільтрує сервер (`q`), а не Select: інакше пошук ішов би лише по перших 50.
      filter={({ options: all }) => all}
      searchValue={search}
      onSearchChange={setSearch}
      nothingFoundMessage={options.isFetching ? t('common.loading') : t('registries.searchNoMatches')}
      onChange={(next) => onCommit(next, next === null ? undefined : data.find((d) => d.value === next)?.label, 'none')}
      onKeyDown={(event) => {
        if (event.key === 'Escape' || event.key === 'Tab') commitKeys(event, () => onCancel(), onCancel);
      }}
      onBlur={onCancel}
    />
  );
}

function DateEditor({ label, value, onCommit, onCancel }: CellEditorProps): JSX.Element {
  return (
    <Suspense fallback={null}>
      <DateInput
        size="xs"
        autoFocus
        aria-label={label}
        valueFormat="YYYY-MM-DD"
        clearable
        defaultValue={parseDateOnly(value)}
        onChange={(next) => onCommit(formatDateOnly(next), undefined, 'none')}
        onKeyDown={(event) => {
          if (event.key === 'Escape') commitKeys(event, () => onCancel(), onCancel);
        }}
        onBlur={onCancel}
      />
    </Suspense>
  );
}
