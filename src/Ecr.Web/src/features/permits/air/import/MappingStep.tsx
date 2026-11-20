import { useEffect, useId, useRef, useState, type JSX } from 'react';
import { Button, Checkbox, Select, Text, TextInput } from '@mantine/core';
import { serverMessage } from '@/features/registries/rc816/serverMessage';
import { formatNumber } from '@/shared/format/number';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import type { AirAnalysis, ImportProfile, ImportSpec } from '../airTypes';
import {
  fieldWithoutColumn,
  fileColumnOptions,
  lacksColumn,
  patternsToText,
  profileCodeFrom,
  profileCodeProblem,
  specFields,
  sortIssues,
  specProblem,
  textToPatterns,
  withField,
  withNewField,
  withoutField,
  type ProfileSaveConflict,
} from './importModel';
import { IssuesTable } from './IssuesTable';
import { fieldCounts, fieldRows, mappingVerdict, withPickedColumn } from './fieldRows';
import { FieldsTable } from './FieldsTable';
import { useColumnSamples } from './xlsxSamples';
import type { AddColumnProblem, NewColumnRequest } from './addColumn';
import { fieldLabel, NoColumnNames, type ColumnNames } from './fieldLabels';
import { InputOrder } from './inputOrder';
import { HeaderRowsForm, UnmappedColumns, type HeaderRows } from './LayoutControls';

const ParserOptions = ['Text', 'Decimal', 'Integer', 'Code', 'Choice', 'Date', 'Unit'] as const;
const LevelOptions = ['Parent', 'Child'] as const;

function parserOf(value: string | null | undefined): (typeof ParserOptions)[number] {
  return ParserOptions.find((option) => option === value) ?? 'Text';
}

function levelOf(value: string | null | undefined): (typeof LevelOptions)[number] {
  return LevelOptions.find((option) => option === value) ?? 'Child';
}

/** UX 20.11: the parser in plain words («Value type»); the value sent stays the parser's name. Literal keys for the seed guard. */
function parserLabel(parser: (typeof ParserOptions)[number]): string {
  switch (parser) {
    case 'Text':
      return t('permits.air.import.add.kind.text');
    case 'Decimal':
      return t('permits.air.import.add.kind.number');
    case 'Integer':
      return t('permits.air.import.add.kind.whole');
    case 'Code':
      return t('permits.air.import.parser.code');
    case 'Choice':
      return t('permits.air.import.parser.choice');
    case 'Date':
      return t('permits.air.import.add.kind.date');
    case 'Unit':
      return t('permits.air.import.parser.unit');
  }
}

const ParserData = (): { value: string; label: string }[] => ParserOptions.map((value) => ({ value, label: parserLabel(value) }));
const LevelData = (): { value: string; label: string }[] =>
  LevelOptions.map((value) => ({ value, label: value === 'Parent' ? t('permits.air.import.map.level.parent') : t('permits.air.import.map.level.child') }));

export interface MappingStepProps {
  readonly analysis: AirAnalysis;
  /** The chosen workbook: values under its columns are shown next to them (read in the browser); absent — no values. */
  readonly file?: File | null;
  /** «Edit profile» on the File step: the rules editor («Advanced») is shown open. */
  readonly openEditor?: boolean;
  /** The mapping in hand is not the one the file was last read with (the checks shown are of the previous reading). */
  readonly pending?: boolean;
  /** A reading of the file is under way right now (not delayed for the spinner): the column pickers wait. */
  readonly reading?: boolean;
  /** Збережений профіль (його специфікація — вихідна для редагування); `undefined`, поки читається. */
  readonly profile: ImportProfile | undefined;
  /** Редагована специфікація; `null` — працює профіль як є. */
  readonly spec: ImportSpec | null;
  readonly onSpec: (spec: ImportSpec | null) => void;
  /** Повторна розкладка файлу з редагованою специфікацією. */
  readonly onReanalyse: () => void;
  readonly reanalysing: boolean;
  /** Право `Registry.EditDefinition`. */
  readonly mayEditProfile: boolean;
  readonly onSaveProfile: (code: string, name: string) => void;
  /** Pass 3: «Save to profile …» — the edited mapping over the chosen (not built-in) profile, with its token. */
  readonly onResaveProfile?: () => void;
  readonly saving: boolean;
  readonly savedCode: string | null;
  /** The last save was refused for its code: taken (offer «Overwrite») or the built-in profile (only another code). */
  readonly saveConflict?: ProfileSaveConflict | null;
  readonly onOverwriteProfile?: () => void;
  readonly onChooseAnotherCode?: () => void;
  /** Sets the specification and re-reads the file with it in one step (header rows, a new column). */
  readonly onApplySpec: (spec: ImportSpec) => void;
  /** Rights `Permit.EditType` + `Permit.PublishType`: «Add as new column». */
  readonly mayAddColumn: boolean;
  readonly onAddColumn: (request: NewColumnRequest) => void;
  readonly addingColumn: boolean;
  /** Why the last «Add as new column» was refused before anything was written. */
  readonly addProblem?: AddColumnProblem;
  /** Names of the AIR type columns: labels of the added fields (`FILE_*`). */
  readonly columnNames?: ColumnNames;
}

/**
 * Catalog key of «the column for the field was not found» (`RegistryImportKeys.ColumnMissing`); an error for a required field.
 * It tells a missing column even before the profile (the rows' required flags) is read.
 */
const ColumnMissingKey = 'err.ECR-IMP-0422.regColumnMissing';

/** The specification header rows apply to: the edited one, else the profile's. */
function baseSpec(spec: ImportSpec | null, profile: ImportProfile | undefined): ImportSpec | null {
  return spec ?? profile?.spec ?? null;
}

/**
 * The specification with the layout chosen in «Where the table is in the file»: header rows and the first data row, and the
 * sheet when the workbook has several. `rowsAuto` (another sheet, rows untouched) drops explicit rows — auto-detection.
 */
export function withHeaderRows(spec: ImportSpec, rows: HeaderRows): ImportSpec {
  const sheet = rows.sheet === undefined ? {} : { sheet: rows.sheet };
  if (rows.rowsAuto === true) {
    const { header: _header, dataStartRow: _data, ...rest } = spec;

    return { ...rest, ...sheet };
  }

  return { ...spec, ...sheet, header: { fromRow: rows.headerFrom, toRow: rows.headerTo }, dataStartRow: rows.dataFrom };
}

/**
 * The one «Save as profile…» form: name, and a code suggested from it (editable, checked by `profileCodeProblem`). The profile
 * keeps the whole specification — sheet, header rows, data start row, all fields including added columns.
 *
 * UX 20.11: the user gives a name only; the code is generated from it (Cyrillic transliterated) and shown as a line. «Change
 * code» opens the code field; it also opens by itself when the generated code is not valid or the code was refused (taken /
 * built-in) — exactly when the user has to decide about it.
 */
function SaveProfileForm({
  onSave,
  onCancel,
  saving,
  conflict,
  onOverwrite,
  onChooseAnother,
}: {
  readonly onSave: (code: string, name: string) => void;
  readonly onCancel: () => void;
  readonly saving: boolean;
  readonly conflict: ProfileSaveConflict | null;
  readonly onOverwrite: () => void;
  readonly onChooseAnother: () => void;
}): JSX.Element {
  const [name, setName] = useState('');
  const [code, setCode] = useState('');
  const [codeEdited, setCodeEdited] = useState(false);
  const [touched, setTouched] = useState(false);
  const [codeOpen, setCodeOpen] = useState(false);
  const [focusCode, setFocusCode] = useState(false);
  const codeInput = useRef<HTMLInputElement>(null);
  const shownCode = codeEdited ? code : profileCodeFrom(name);
  const invalid = profileCodeProblem(shownCode);
  const missingName = name.trim() === '';
  // The refusal is about the code it was given: once the code is edited it no longer applies.
  const shownConflict = conflict !== null && conflict.code === shownCode ? conflict : null;
  const showCode = codeOpen || shownConflict !== null || (touched && invalid && !missingName);
  useEffect(() => {
    if (!focusCode || !showCode) return;
    setFocusCode(false);
    codeInput.current?.focus();
    codeInput.current?.select();
  }, [focusCode, showCode]);
  const chooseAnother = (): void => {
    onChooseAnother();
    // The refusal goes away with the choice: the field stays open and takes the focus.
    setCodeOpen(true);
    setFocusCode(true);
  };

  return (
    <form
      className="pi-panel"
      aria-label={t('permits.air.import.save.title')}
      onSubmit={(event) => {
        event.preventDefault();
        setTouched(true);
        if (!invalid && !missingName && !saving) onSave(shownCode, name.trim());
      }}
    >
      <Text fw={600}>{t('permits.air.import.save.title')}</Text>
      <Text size="sm" c="dimmed">
        {t('permits.air.import.save.hint')}
      </Text>
      <div className="pi-grid">
        <TextInput
          label={t('permits.air.import.save.name')}
          value={name}
          error={touched && missingName ? t('permits.air.import.save.nameRequired') : null}
          onChange={(event) => setName(event.currentTarget.value)}
          data-autofocus
        />
        {showCode && (
          <TextInput
            ref={codeInput}
            label={t('permits.air.import.save.code')}
            description={t('permits.air.import.save.codeHint')}
            inputWrapperOrder={InputOrder}
            value={shownCode}
            error={touched && invalid ? t('permits.air.import.save.codeInvalid') : null}
            onChange={(event) => {
              setCodeEdited(true);
              setCode(event.currentTarget.value.toLocaleUpperCase('en-US'));
            }}
          />
        )}
      </div>
      {!showCode && (
        <div className="pi-toolbar">
          <Text size="sm" c="dimmed" data-testid="air-profile-code">
            {shownCode === '' ? t('permits.air.import.save.codeLater') : t('permits.air.import.save.codeShown', { code: shownCode })}
          </Text>
          <Button
            variant="subtle"
            size="compact-sm"
            onClick={() => {
              setCodeOpen(true);
              setFocusCode(true);
            }}
          >
            {t('permits.air.import.save.changeCode')}
          </Button>
        </div>
      )}
      {shownConflict !== null && (
        <Banner
          tone="warning"
          testId="air-profile-conflict"
          title={
            shownConflict.kind === 'taken' ? t('permits.air.import.save.codeTakenTitle') : t('permits.air.import.save.builtInTitle')
          }
          text={
            shownConflict.kind === 'taken'
              ? t('permits.air.import.save.codeTaken', { code: shownConflict.code })
              : t('permits.air.import.save.builtIn', { code: shownConflict.code })
          }
        />
      )}
      {shownConflict !== null && (
        <div className="pi-actions">
          {shownConflict.kind === 'taken' && (
            <Button variant="default" loading={saving} onClick={onOverwrite}>
              {t('permits.air.import.save.overwrite')}
            </Button>
          )}
          <Button variant="default" onClick={chooseAnother}>
            {t('permits.air.import.save.chooseAnother')}
          </Button>
        </div>
      )}
      <div className="pi-actions">
        <Button variant="default" onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button type="submit" loading={saving}>
          {t('permits.air.import.save.submit')}
        </Button>
      </div>
    </form>
  );
}

function FieldEditor({
  spec,
  analysis,
  onSpec,
  columnNames,
}: {
  readonly spec: ImportSpec;
  readonly analysis: AirAnalysis;
  readonly onSpec: (spec: ImportSpec) => void;
  readonly columnNames: ColumnNames;
}): JSX.Element {
  const fields = specFields(spec);
  const [newName, setNewName] = useState('');
  const header = analysis.analysis.header;
  const mapped = new Map((header?.mapped ?? []).map((column) => [column.field, column.columnLetter] as const));
  // P1 UAT 07.11: a field added here had no way to point at a column (only header text); the picker lists the file's columns.
  const columnOptions = fileColumnOptions(
    [...(header?.mapped ?? []), ...(header?.unmapped ?? [])],
    fields.map((field) => field.column),
  );
  const listId = useId();

  return (
    <div className="pi-panel">
      <div className="pa-scroll" role="region" aria-label={t('permits.air.import.map.editorAria')} tabIndex={0}>
        <table className="pi-table" aria-describedby={listId}>
          <thead>
            <tr>
              <th scope="col">{t('permits.air.import.map.field')}</th>
              <th scope="col">{t('permits.air.import.map.patterns')}</th>
              <th scope="col">{t('permits.air.import.map.column')}</th>
              <th scope="col">{t('permits.air.import.map.level')}</th>
              <th scope="col">{t('permits.air.import.map.parser')}</th>
              <th scope="col">{t('permits.air.import.map.required')}</th>
              <th scope="col">{t('permits.air.import.map.matched')}</th>
              <th scope="col">
                <span className="pa-sr">{t('permits.air.import.map.remove')}</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {fields.map((field, index) => {
              const name = field.name ?? '';
              const column = mapped.get(name);
              const label = fieldLabel(name, columnNames);

              return (
                <tr key={`${name}-${String(index)}`}>
                  <td>
                    <TextInput
                      aria-label={t('permits.air.import.map.fieldName')}
                      description={label === name ? undefined : label}
                      inputWrapperOrder={InputOrder}
                      value={name}
                      onChange={(event) => onSpec(withField(spec, index, { name: event.currentTarget.value }))}
                    />
                  </td>
                  <td>
                    <TextInput
                      aria-label={t('permits.air.import.map.patternsFor', { field: name })}
                      className="pi-pattern"
                      value={patternsToText(field.patterns)}
                      onChange={(event) => onSpec(withField(spec, index, { patterns: textToPatterns(event.currentTarget.value) }))}
                    />
                  </td>
                  <td>
                    <Select
                      aria-label={t('permits.air.import.map.columnFor', { field: name })}
                      data={columnOptions}
                      clearable
                      searchable
                      placeholder={t('permits.air.import.map.columnAuto')}
                      value={(field.column ?? '').trim() === '' ? null : (field.column ?? '').trim().toUpperCase()}
                      error={lacksColumn(field) ? t('permits.air.import.map.pickColumn') : undefined}
                      onChange={(value) => onSpec(withField(spec, index, { column: value }))}
                    />
                  </td>
                  <td>
                    <Select
                      aria-label={t('permits.air.import.map.levelFor', { field: name })}
                      data={LevelData()}
                      allowDeselect={false}
                      value={levelOf(field.level)}
                      onChange={(value) => onSpec(withField(spec, index, { level: levelOf(value) }))}
                    />
                  </td>
                  <td>
                    <Select
                      aria-label={t('permits.air.import.map.parserFor', { field: name })}
                      data={ParserData()}
                      allowDeselect={false}
                      value={parserOf(field.parser)}
                      onChange={(value) => onSpec(withField(spec, index, { parser: parserOf(value) }))}
                    />
                  </td>
                  <td>
                    <Checkbox
                      aria-label={t('permits.air.import.map.requiredFor', { field: name })}
                      checked={field.required === true}
                      onChange={(event) => onSpec(withField(spec, index, { required: event.currentTarget.checked }))}
                    />
                  </td>
                  <td>
                    <span className="pa-code" data-matched={column !== undefined}>
                      {column ?? (field.required === true ? t('permits.air.import.map.notFound') : '—')}
                    </span>
                  </td>
                  <td>
                    <Button
                      variant="subtle"
                      size="xs"
                      aria-label={t('permits.air.import.map.removeFor', { field: name })}
                      onClick={() => onSpec(withoutField(spec, index))}
                    >
                      ✕
                    </Button>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <Text size="xs" c="dimmed" id={listId}>
        {t('permits.air.import.map.patternsHint')}
      </Text>
      <form
        className="pi-inline"
        onSubmit={(event) => {
          event.preventDefault();
          const name = newName.trim();
          if (name === '') return;
          onSpec(withNewField(spec, name));
          setNewName('');
        }}
      >
        <TextInput
          aria-label={t('permits.air.import.map.newName')}
          placeholder={t('permits.air.import.map.newName')}
          value={newName}
          onChange={(event) => setNewName(event.currentTarget.value)}
        />
        <Button type="submit" variant="default">
          {t('permits.air.import.map.add')}
        </Button>
      </form>
    </div>
  );
}

/** Why no source row was read: the first error (errors first, a summary without a row before the per-row ones). */
function noRowsReason(issues: AirAnalysis['analysis']['issues']): string {
  const first = sortIssues(issues).find((issue) => issue.severity === 'Error');

  return first === undefined ? t('permits.air.import.noRows.unknown') : serverMessage(first.messageKey, first.args ?? {});
}

/**
 * Крок 3 «Відповідність». UX 20.11 («the mapping interface looks a bit complicated»): типовий випадок — «перевір і далі».
 * Згори — вердикт (усе знайдено / бракує обов'язкових колонок / файл не піде далі), лічильники, далі таблиця полів
 * (`FieldsTable`: поле, обов'язкове чи ні, колонка файлу з прикладами значень, стан; обов'язкове незнайдене — вибір колонки
 * прямо в рядку, вибір одразу перечитує файл). Решта — згорнуто: колонки файлу, що не імпортуються («Add as new column»),
 * «Where the table is in the file» (аркуш, рядки шапки; само розкривається, коли не прочитано жодного джерела), «Advanced»
 * (редактор `RegistryImportSpec`: тексти заголовків, рівень, тип значення, обов'язковість, нові поля, «Re-analyse»).
 * «Save as profile…» — одна форма поза «Advanced»: зберігає поточну специфікацію (змінену або профілю) з аркушем, рядками
 * шапки й доданими полями; людина дає лише назву, код генерується.
 */
export function MappingStep(props: MappingStepProps): JSX.Element {
  const { analysis, profile, spec, onSpec, onReanalyse, reanalysing, mayEditProfile, onSaveProfile, saving, savedCode } = props;
  const { onApplySpec, mayAddColumn, onAddColumn, addingColumn } = props;
  const columnNames = props.columnNames ?? NoColumnNames;
  const body = analysis.analysis;
  const header = body.header;
  const problem = spec === null ? null : specProblem(spec);
  // A field without a column may be re-analysed (reported as «column not found»), not saved into a profile.
  const unpointed = spec === null ? null : fieldWithoutColumn(spec);
  const [saveOpen, setSaveOpen] = useState(false);
  const [resaveOpen, setResaveOpen] = useState(false);
  const [seenSaved, setSeenSaved] = useState(savedCode);
  const base = baseSpec(spec, profile);
  const unsaved = analysis.unsavedFields ?? [];
  const mayResave =
    mayEditProfile && spec !== null && profile !== undefined && !profile.builtIn && problem === null && unpointed === null;

  // The rules editor («Advanced»): closed by default; open when the user opens it or came here by «Edit profile».
  const [advancedOpen, setAdvancedOpen] = useState(props.openEditor === true);
  const [seenOpenEditor, setSeenOpenEditor] = useState(props.openEditor === true);
  if ((props.openEditor === true) !== seenOpenEditor) {
    setSeenOpenEditor(props.openEditor === true);
    if (props.openEditor === true) setAdvancedOpen(true);
  }
  const rows = fieldRows(base, header?.mapped ?? [], body.issues);
  const counts = fieldCounts(rows);
  const verdict = mappingVerdict(counts, body.canContinue);
  /*
   * No source was read although every required column was found: then the rows of the table (header, first data row) are the
   * likely cause. ⚠ Review 20.11 (M1): with a required column missing the server reads no row at all (empty stats,
   * `RegistryImportParser.Parse`) — that is the column's problem, said by the verdict and its row, not «no source».
   */
  const requiredMissing =
    counts.missing > 0 || body.issues.some((issue) => issue.severity === 'Error' && issue.messageKey === ColumnMissingKey);
  const noRows = header !== null && body.stats.parents === 0 && !requiredMissing;
  // «Where the table is in the file»: closed while the file reads; open by itself when no source was read (also after a re-read).
  const [layoutOpen, setLayoutOpen] = useState(noRows);
  const [seenNoRows, setSeenNoRows] = useState(noRows);
  if (noRows !== seenNoRows) {
    setSeenNoRows(noRows);
    if (noRows) setLayoutOpen(true);
  }
  // M4: an edit the profile cannot be saved with (a name missing or repeated, a field without any column) opens the rules
  // editor by itself — the reason and the way to fix it are never folded away.
  const editorProblem = problem !== null || unpointed !== null;
  const fileColumns = [...(header?.mapped ?? []), ...(header?.unmapped ?? [])];
  const samples = useColumnSamples(
    props.file,
    body.sheetName,
    header?.dataFromRow ?? 0,
    header === null ? [] : [...new Set(fileColumns.map((column) => column.column))].sort((a, b) => a - b),
  );

  // A save that went through closes the form (the status line below says which profile is now selected).
  if (savedCode !== seenSaved) {
    setSeenSaved(savedCode);
    if (savedCode !== null) {
      setSaveOpen(false);
      setResaveOpen(false);
    }
  }

  return (
    <div className="pi-page">
      {props.pending === true && (
        // L3: the checks below are of the previous reading — no «ready» for a mapping the file was not read with yet.
        <Banner
          tone="info"
          testId="air-mapping-verdict"
          title={t('permits.air.import.verdict.pendingTitle')}
          text={t('permits.air.import.verdict.pendingText')}
        />
      )}
      {props.pending !== true && verdict === 'missing' && (
        <Banner
          tone="warning"
          testId="air-mapping-verdict"
          title={t('permits.air.import.verdict.missingTitle', { count: formatNumber(counts.missing) })}
          text={t('permits.air.import.verdict.missingText')}
        />
      )}
      {props.pending !== true && verdict === 'blocked' && !noRows && (
        <Banner
          tone="danger"
          testId="air-mapping-verdict"
          title={t('permits.air.import.verdict.blockedTitle')}
          text={t('permits.air.import.verdict.blockedText')}
        />
      )}
      {props.pending !== true && verdict === 'ready' && (
        <Banner
          tone="success"
          testId="air-mapping-verdict"
          title={t('permits.air.import.verdict.readyTitle', {
            found: formatNumber(counts.found),
            total: formatNumber(counts.found + counts.notInFile),
          })}
          text={t('permits.air.import.verdict.readyText')}
        />
      )}
      <div className="pi-stats">
        <div className="pi-stat">
          {t('permits.air.import.stats.parents')}
          <b>{formatNumber(body.stats.parents)}</b>
        </div>
        <div className="pi-stat">
          {t('permits.air.import.stats.children')}
          <b>{formatNumber(body.stats.children)}</b>
        </div>
        <div className="pi-stat">
          {t('permits.air.import.stats.distinct')}
          <b>{formatNumber(body.stats.distinctChildKeys)}</b>
        </div>
      </div>
      {noRows && header !== null && (
        // Pass 3b (UAT P2-2): «0 / 0» with every column resolved said nothing; the first blocking finding says what to fix.
        <Banner
          tone="danger"
          testId="air-no-rows-reason"
          title={t('permits.air.import.noRows.title', {
            sheet: body.sheetName,
            from: formatNumber(header.headerFromRow),
            to: formatNumber(header.headerToRow),
          })}
          text={noRowsReason(body.issues)}
        />
      )}

      {header !== null && (
        <FieldsTable
          rows={rows}
          columns={fileColumns}
          samples={samples}
          columnNames={columnNames}
          mayPick={base !== null}
          busy={props.reading ?? reanalysing}
          onPick={(index, letter, owner) => base !== null && onApplySpec(withPickedColumn(base, index, letter, owner))}
        />
      )}

      {unsaved.length > 0 && (
        <Banner
          tone="warning"
          testId="air-unsaved-fields"
          title={t('permits.air.import.unsaved.title')}
          text={t('permits.air.import.unsaved.text', { fields: unsaved.map((name) => fieldLabel(name, columnNames)).join(', ') })}
        />
      )}
      {props.addProblem != null && (
        <Banner
          tone="warning"
          title={t('permits.air.import.add.problemTitle')}
          text={props.addProblem === 'draftOpen' ? t('permits.air.import.add.draftOpen') : t('permits.air.import.add.noTable')}
        />
      )}
      <UnmappedColumns
        columns={header?.unmapped ?? []}
        mayAdd={mayAddColumn && base !== null}
        onAdd={onAddColumn}
        busy={addingColumn}
        samples={samples}
      />

      {header !== null && (
        <details
          className="pi-panel"
          data-testid="air-layout"
          open={layoutOpen}
          onToggle={(event) => setLayoutOpen(event.currentTarget.open)}
        >
          <summary>
            {t('permits.air.import.rows.title')}
            {': '}
            {body.sheetNames.length > 1 && <span>{t('permits.air.import.rows.sheetRead', { sheet: body.sheetName })} · </span>}
            <span data-testid="air-header-report">
              {t('permits.air.import.map.header', {
                from: formatNumber(header.headerFromRow),
                to: formatNumber(header.headerToRow),
                data: formatNumber(header.dataFromRow),
                groups: formatNumber(body.stats.groupRows),
                skipped: formatNumber(body.stats.skippedRows),
              })}
            </span>
          </summary>
          <HeaderRowsForm
            // A new analysis (other rows or sheet) restarts the form with what the server found.
            key={`${body.sheetName}-${String(header.headerFromRow)}-${String(header.headerToRow)}-${String(header.dataFromRow)}`}
            detected={{ headerFrom: header.headerFromRow, headerTo: header.headerToRow, dataFrom: header.dataFromRow }}
            sheets={body.sheetNames}
            sheet={body.sheetName}
            busy={reanalysing}
            disabled={base === null}
            onApply={(layout) => base !== null && onApplySpec(withHeaderRows(base, layout))}
          />
        </details>
      )}

      <details
        className="pi-panel"
        data-testid="air-advanced"
        open={advancedOpen || editorProblem}
        onToggle={(event) => setAdvancedOpen(event.currentTarget.open)}
      >
        <summary>{t('permits.air.import.advanced')}</summary>
        <Text size="sm" c="dimmed">
          {t('permits.air.import.advancedHint')}
        </Text>
        {spec === null ? (
          <div className="pi-actions">
            <Button variant="default" disabled={profile === undefined} onClick={() => profile !== undefined && onSpec(profile.spec)}>
              {t('permits.air.import.map.edit')}
            </Button>
          </div>
        ) : (
          <>
            <FieldEditor spec={spec} analysis={analysis} onSpec={onSpec} columnNames={columnNames} />
            <div className="pi-actions">
              <Button variant="default" onClick={() => onSpec(null)}>
                {t('permits.air.import.map.discard')}
              </Button>
              <Button variant="default" loading={reanalysing} disabled={problem !== null} onClick={onReanalyse}>
                {t('permits.air.import.map.reanalyse')}
              </Button>
            </div>
          </>
        )}
      </details>

      {/* M4: why «Save as profile…» is disabled stays next to it, outside the fold. */}
      {problem !== null && (
        <Banner
          tone="warning"
          testId="air-mapping-problem"
          title={t('permits.air.import.map.problemTitle')}
          text={problem === 'nameRequired' ? t('permits.air.import.map.nameRequired') : t('permits.air.import.map.nameDuplicate')}
        />
      )}
      {problem === null && unpointed !== null && (
        <Banner
          tone="warning"
          title={t('permits.air.import.map.problemTitle')}
          text={t('permits.air.import.map.columnRequired', { field: fieldLabel(unpointed, columnNames) })}
          testId="air-field-without-column"
        />
      )}
      {spec !== null && profile?.builtIn === true && (
        <Text size="sm" c="dimmed" data-testid="air-profile-builtin">
          {t('permits.air.import.profile.builtInNote', { code: profile.code })}
        </Text>
      )}
      {resaveOpen && mayResave && (
        <div className="pi-panel" data-testid="air-profile-resave-confirm">
          <Banner
            tone="warning"
            title={t('permits.air.import.save.resaveTitle', { code: profile.code })}
            text={t('permits.air.import.save.resaveText')}
          />
          <div className="pi-actions">
            <Button variant="default" onClick={() => setResaveOpen(false)}>
              {t('common.cancel')}
            </Button>
            <Button loading={saving} onClick={() => props.onResaveProfile?.()}>
              {t('permits.air.import.save.overwrite')}
            </Button>
          </div>
        </div>
      )}
      {mayEditProfile && base !== null && (
        <div className="pi-actions">
          {mayResave && !resaveOpen && props.onResaveProfile !== undefined && (
            <Button variant="default" onClick={() => setResaveOpen(true)}>
              {t('permits.air.import.save.resave', { code: profile.code })}
            </Button>
          )}
          <Button
            variant="default"
            disabled={problem !== null || unpointed !== null}
            onClick={() => setSaveOpen((open) => !open)}
            aria-expanded={saveOpen}
          >
            {t('permits.air.import.map.saveProfile')}
          </Button>
        </div>
      )}
      {saveOpen && mayEditProfile && base !== null && unpointed === null && (
        <SaveProfileForm
          onSave={onSaveProfile}
          onCancel={() => setSaveOpen(false)}
          saving={saving}
          conflict={props.saveConflict ?? null}
          onOverwrite={props.onOverwriteProfile ?? (() => undefined)}
          onChooseAnother={props.onChooseAnotherCode ?? (() => undefined)}
        />
      )}
      {savedCode !== null && (
        <Text size="sm" role="status">
          {t('permits.air.import.save.done', { code: savedCode })}
        </Text>
      )}

      <IssuesTable issues={body.issues} dropped={body.droppedIssues} columnNames={columnNames} />
    </div>
  );
}
