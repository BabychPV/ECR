import { useId, useState, type JSX } from 'react';
import { Button, Checkbox, Select, Text, TextInput } from '@mantine/core';
import { formatNumber } from '@/shared/format/number';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import type { AirAnalysis, ImportProfile, ImportSpec } from '../airTypes';
import {
  patternsToText,
  profileCodeProblem,
  specFields,
  specProblem,
  textToPatterns,
  withField,
  withNewField,
  withoutField,
} from './importModel';
import { IssuesTable } from './IssuesTable';
import type { AddColumnProblem, NewColumnRequest } from './addColumn';
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
  /** Sets the specification and re-reads the file with it in one step (header rows, a new column). */
  readonly onApplySpec: (spec: ImportSpec) => void;
  /** Rights `Permit.EditType` + `Permit.PublishType`: «Add as new column». */
  readonly mayAddColumn: boolean;
  readonly onAddColumn: (request: NewColumnRequest) => void;
  readonly addingColumn: boolean;
  /** Why the last «Add as new column» was refused before anything was written. */
  readonly addProblem?: AddColumnProblem;
}

/** The specification header rows apply to: the edited one, else the profile's. */
function baseSpec(spec: ImportSpec | null, profile: ImportProfile | undefined): ImportSpec | null {
  return spec ?? profile?.spec ?? null;
}

export function withHeaderRows(spec: ImportSpec, rows: HeaderRows): ImportSpec {
  return { ...spec, header: { fromRow: rows.headerFrom, toRow: rows.headerTo }, dataStartRow: rows.dataFrom };
}

function SaveProfileForm({
  onSave,
  saving,
}: {
  readonly onSave: (code: string, name: string) => void;
  readonly saving: boolean;
}): JSX.Element {
  const [code, setCode] = useState('');
  const [name, setName] = useState('');
  const [touched, setTouched] = useState(false);
  const invalid = profileCodeProblem(code);

  return (
    <form
      className="pi-grid"
      onSubmit={(event) => {
        event.preventDefault();
        setTouched(true);
        if (!invalid && name.trim() !== '' && !saving) onSave(code, name.trim());
      }}
    >
      <TextInput
        label={t('permits.air.import.save.code')}
        description={t('permits.air.import.save.codeHint')}
        value={code}
        error={touched && invalid ? t('permits.air.import.save.codeInvalid') : null}
        onChange={(event) => setCode(event.currentTarget.value.toLocaleUpperCase('en-US'))}
      />
      <TextInput
        label={t('permits.air.import.save.name')}
        value={name}
        error={touched && name.trim() === '' ? t('permits.air.import.save.nameRequired') : null}
        onChange={(event) => setName(event.currentTarget.value)}
      />
      <Button type="submit" loading={saving} style={{ alignSelf: 'end' }}>
        {t('permits.air.import.save.submit')}
      </Button>
    </form>
  );
}

function FieldEditor({
  spec,
  analysis,
  onSpec,
}: {
  readonly spec: ImportSpec;
  readonly analysis: AirAnalysis;
  readonly onSpec: (spec: ImportSpec) => void;
}): JSX.Element {
  const fields = specFields(spec);
  const [newName, setNewName] = useState('');
  const mapped = new Map((analysis.analysis.header?.mapped ?? []).map((column) => [column.field, column.columnLetter] as const));
  const listId = useId();

  return (
    <div className="pi-panel">
      <div className="pa-scroll" role="region" aria-label={t('permits.air.import.map.editorAria')} tabIndex={0}>
        <table className="pi-table" aria-describedby={listId}>
          <thead>
            <tr>
              <th scope="col">{t('permits.air.import.map.field')}</th>
              <th scope="col">{t('permits.air.import.map.patterns')}</th>
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

              return (
                <tr key={`${name}-${String(index)}`}>
                  <td>
                    <TextInput
                      aria-label={t('permits.air.import.map.fieldName')}
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
        className="pi-actions"
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

/**
 * Крок 2: розкладка файлу (блок шапки, змаплені й незмаплені колонки) і редактор відповідності `RegistryImportSpec`.
 * Редагується копія специфікації профілю; «Re-analyse» перечитує файл з нею, «Save as profile» кладе її в профіль.
 */
export function MappingStep(props: MappingStepProps): JSX.Element {
  const { analysis, profile, spec, onSpec, onReanalyse, reanalysing, mayEditProfile, onSaveProfile, saving, savedCode } = props;
  const { onApplySpec, mayAddColumn, onAddColumn, addingColumn } = props;
  const body = analysis.analysis;
  const header = body.header;
  const problem = spec === null ? null : specProblem(spec);
  const [saveOpen, setSaveOpen] = useState(false);
  const base = baseSpec(spec, profile);

  return (
    <div className="pi-page">
      {header !== null && (
        <HeaderRowsForm
          // A new analysis (other rows found) restarts the form with what the server found.
          key={`${String(header.headerFromRow)}-${String(header.headerToRow)}-${String(header.dataFromRow)}`}
          detected={{ headerFrom: header.headerFromRow, headerTo: header.headerToRow, dataFrom: header.dataFromRow }}
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
                  <th scope="row">{column.field}</th>
                  <td>{column.headerText}</td>
                  <td className="pa-code">{column.columnLetter}</td>
                  <td>{column.level}</td>
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
          <Button
            variant="default"
            disabled={profile === undefined}
            onClick={() => profile !== undefined && onSpec(profile.spec)}
            style={{ alignSelf: 'start' }}
          >
            {t('permits.air.import.map.edit')}
          </Button>
      ) : (
        <>
          <FieldEditor spec={spec} analysis={analysis} onSpec={onSpec} />
          {problem !== null && (
            <Banner
              tone="warning"
              title={t('permits.air.import.map.problemTitle')}
              text={problem === 'nameRequired' ? t('permits.air.import.map.nameRequired') : t('permits.air.import.map.nameDuplicate')}
            />
          )}
          <div className="pi-actions">
            <Button variant="default" onClick={() => onSpec(null)}>
              {t('permits.air.import.map.discard')}
            </Button>
            <Button variant="default" loading={reanalysing} disabled={problem !== null} onClick={onReanalyse}>
              {t('permits.air.import.map.reanalyse')}
            </Button>
            {mayEditProfile && (
              <Button variant="default" disabled={problem !== null} onClick={() => setSaveOpen((open) => !open)} aria-expanded={saveOpen}>
                {t('permits.air.import.map.saveProfile')}
              </Button>
            )}
          </div>
          {saveOpen && mayEditProfile && <SaveProfileForm onSave={onSaveProfile} saving={saving} />}
        </>
      )}
      </details>
      {savedCode !== null && (
        <Text size="sm" role="status">
          {t('permits.air.import.save.done', { code: savedCode })}
        </Text>
      )}

      <IssuesTable issues={body.issues} dropped={body.droppedIssues} />
    </div>
  );
}
