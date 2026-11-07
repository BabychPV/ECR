import { useId, useRef, useState, type JSX } from 'react';
import { Button, Checkbox, Select, Text, TextInput } from '@mantine/core';
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
  specProblem,
  textToPatterns,
  withField,
  withNewField,
  withoutField,
  type ProfileSaveConflict,
} from './importModel';
import { IssuesTable } from './IssuesTable';
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

export interface MappingStepProps {
  readonly analysis: AirAnalysis;
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
  const codeInput = useRef<HTMLInputElement>(null);
  const shownCode = codeEdited ? code : profileCodeFrom(name);
  const invalid = profileCodeProblem(shownCode);
  const missingName = name.trim() === '';
  // The refusal is about the code it was given: once the code is edited it no longer applies.
  const shownConflict = conflict !== null && conflict.code === shownCode ? conflict : null;
  const chooseAnother = (): void => {
    onChooseAnother();
    codeInput.current?.focus();
    codeInput.current?.select();
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
      </div>
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
                      data={[...LevelOptions]}
                      allowDeselect={false}
                      value={levelOf(field.level)}
                      onChange={(value) => onSpec(withField(spec, index, { level: levelOf(value) }))}
                    />
                  </td>
                  <td>
                    <Select
                      aria-label={t('permits.air.import.map.parserFor', { field: name })}
                      data={[...ParserOptions]}
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

function levelLabel(level: string): string {
  if (level === 'Parent') return t('permits.air.import.map.level.parent');
  if (level === 'Child') return t('permits.air.import.map.level.child');

  return level;
}

/**
 * Крок 2: розкладка файлу (блок шапки, змаплені й незмаплені колонки) і редактор відповідності `RegistryImportSpec`.
 * Редагується копія специфікації профілю; «Re-analyse» перечитує файл з нею. «Save as profile…» — одна форма поза
 * «Advanced»: зберігає поточну специфікацію (змінену або профілю) з аркушем, рядками шапки й доданими полями.
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
  const [seenSaved, setSeenSaved] = useState(savedCode);
  const base = baseSpec(spec, profile);

  // A save that went through closes the form (the status line below says which profile is now selected).
  if (savedCode !== seenSaved) {
    setSeenSaved(savedCode);
    if (savedCode !== null) setSaveOpen(false);
  }

  return (
    <div className="pi-page">
      {header !== null && (
        <HeaderRowsForm
          // A new analysis (other rows or sheet) restarts the form with what the server found.
          key={`${body.sheetName}-${String(header.headerFromRow)}-${String(header.headerToRow)}-${String(header.dataFromRow)}`}
          detected={{ headerFrom: header.headerFromRow, headerTo: header.headerToRow, dataFrom: header.dataFromRow }}
          sheets={body.sheetNames}
          sheet={body.sheetName}
          busy={reanalysing}
          disabled={base === null}
          onApply={(rows) => base !== null && onApplySpec(withHeaderRows(base, rows))}
        />
      )}
      {header !== null && (
        <Text size="sm" data-testid="air-header-report">
          {t('permits.air.import.map.header', {
            from: formatNumber(header.headerFromRow),
            to: formatNumber(header.headerToRow),
            data: formatNumber(header.dataFromRow),
            groups: formatNumber(body.stats.groupRows),
            skipped: formatNumber(body.stats.skippedRows),
          })}
        </Text>
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

      <div className="pi-panel">
        <div className="pa-scroll" role="region" aria-label={t('permits.air.import.map.mappedAria')} tabIndex={0}>
          <table className="pi-table">
            <thead>
              <tr>
                <th scope="col">{t('permits.air.import.map.field')}</th>
                <th scope="col">{t('permits.air.import.map.headerText')}</th>
                <th scope="col">{t('permits.air.import.map.matched')}</th>
                <th scope="col">{t('permits.air.import.map.level')}</th>
              </tr>
            </thead>
            <tbody>
              {(header?.mapped ?? []).map((column) => (
                <tr key={`${column.field}-${String(column.column)}`}>
                  <th scope="row">{fieldLabel(column.field, columnNames)}</th>
                  <td>{column.headerText}</td>
                  <td className="pa-code">{column.columnLetter}</td>
                  <td>{levelLabel(column.level)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {props.addProblem != null && (
        <Banner
          tone="warning"
          title={t('permits.air.import.add.problemTitle')}
          text={props.addProblem === 'draftOpen' ? t('permits.air.import.add.draftOpen') : t('permits.air.import.add.noTable')}
        />
      )}
      <UnmappedColumns columns={header?.unmapped ?? []} mayAdd={mayAddColumn && base !== null} onAdd={onAddColumn} busy={addingColumn} />

      <details className="pi-panel" open={spec !== null ? true : undefined}>
        <summary>{t('permits.air.import.advanced')}</summary>
        {spec === null ? (
          <div className="pi-actions">
            <Button variant="default" disabled={profile === undefined} onClick={() => profile !== undefined && onSpec(profile.spec)}>
              {t('permits.air.import.map.edit')}
            </Button>
          </div>
        ) : (
          <>
            <FieldEditor spec={spec} analysis={analysis} onSpec={onSpec} columnNames={columnNames} />
            {problem !== null && (
              <Banner
                tone="warning"
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

      {mayEditProfile && base !== null && (
        <div className="pi-actions">
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
