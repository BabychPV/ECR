import { useMemo, useState, type JSX } from 'react';
import { Button, Checkbox, FileInput, Group, Modal, NativeSelect, Progress, Stack, Text, Textarea } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { parseBlock } from '@/features/registries/rc812/rowModel';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { t } from '@/shared/i18n';
import { can, useSession } from '@/shared/session/useSession';
import { Banner } from '@/shared/ui/Banner';
import { useRestoreFocus } from '@/shared/ui/useRestoreFocus';
import { showApiError, showDone } from '@/shared/ui/notify';
import type { Permit } from '../api/types';
import { MaxPastedRows } from '../lib/pasteRows';
import { getImportMapping, isWorkbook, readImportWorkbook, resolveImportLookups, saveImportMapping, type LookupResult } from './importApi';
import { lookupRequest, serverResolver } from './importLookups';
import {
  autoMapping,
  looksLikeHeader,
  mappedLines,
  mappingProblems,
  mappingToSave,
  orderMapping,
  type ColumnMapping,
} from './importMapping';
import {
  delimitedToTabs,
  isPlanClean,
  LookupReasons,
  MaxImportFileBytes,
  MaxImportFileMb,
  planRows,
  runImport,
  unresolvedGroups,
  type BatchRun,
  type ImportIssue,
  type ImportRow,
  type UnresolvedGroup,
} from './importRows';
import type { PermitField } from './model';
import { permitKeys, usePickerData } from './usePermitData';
import './permit.css';

export interface PasteDialogProps {
  readonly permit: Permit;
  readonly registryCode: string;
  readonly tableName: string;
  readonly fields: readonly PermitField[];
  /** Відкрито кнопкою «Import file…»: файл — перша дія. */
  readonly fromFile: boolean;
  readonly onClose: () => void;
}

/** Скільки зауважень показувати списком (решта — «ще N»). */
const ShownIssues = 20;

/** Найбільше стовпців файлу в зіставленні. */
const MaxFileColumns = 128;

/** Право зберегти зіставлення для всіх (сервер: `PUT …/import-mapping`). */
const MappingPermission = 'Permit.EditType';

const NoLookups: LookupResult = { columns: [], derived: [] };

/** Текст файлу. `FileReader`, а не `File.text()`: так читає кожен браузер і тестове середовище. */
function readText(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(typeof reader.result === 'string' ? reader.result : '');
    reader.onerror = () => reject(reader.error ?? new Error('read failed'));
    reader.readAsText(file);
  });
}

function issueText(issue: ImportIssue, labelOf: (code: string) => string): string {
  if (issue.reason === 'limit') return t('permits.page.paste.issueLimit', { max: MaxPastedRows });
  if (issue.reason === 'duplicateKey') {
    const columns = issue.column.split(',').map(labelOf).join(' + ');
    return `${t('permits.page.paste.issueWhere', { row: issue.row, column: columns })}: ${t('permits.page.row.duplicate', { columns })}`;
  }
  const where = t('permits.page.paste.issueWhere', { row: issue.row, column: labelOf(issue.column) });
  switch (issue.reason) {
    case 'number':
      return `${where}: ${t('permits.page.paste.issueNumber', { text: issue.text })}`;
    case 'bool':
      return `${where}: ${t('permits.page.paste.issueBool', { text: issue.text })}`;
    case 'date':
      return `${where}: ${t('permits.page.paste.issueDate', { text: issue.text })}`;
    case 'integer':
      return `${where}: ${t('permits.page.paste.issueInteger', { text: issue.text })}`;
    default:
      return `${where}: ${t('permits.page.paste.issueUnknown', { text: issue.text })}`;
  }
}

/** Рядок переліку нерозв'язаних значень (до запису): стовпець, значення, причина, скільки рядків. */
function unresolvedText(group: UnresolvedGroup, labelOf: (code: string) => string): string {
  const column = labelOf(group.column);
  if (group.reason === 'notDerived') {
    // Текст групи - причина сервера: у джерела немає регіону/району в дереві, жоден запис не підходить, або кілька.
    const reason =
      group.text === 'noScope'
        ? t('permits.page.paste.derivedNoScope')
        : group.text === 'ambiguous'
          ? t('permits.page.paste.derivedAmbiguous')
          : t('permits.page.paste.derivedNoMatch');
    return t('permits.page.paste.unresolvedDerived', { column, reason, rows: group.rows });
  }
  const reason =
    group.reason === 'ambiguous'
      ? t('permits.page.paste.reasonAmbiguous')
      : group.reason === 'notSource'
        ? t('permits.page.paste.reasonNotSource')
        : t('permits.page.paste.reasonUnknown');
  return t('permits.page.paste.unresolvedItem', { column, text: group.text, reason, rows: group.rows });
}

/**
 * «Paste from Excel…» і «Import file…» (мокап `paste-rows`): блок із буфера (табуляції), CSV або `.xlsx` (аркуш читає сервер) →
 * зіставлення стовпців файлу зі стовпцями таблиці → значення посилань за кодом чи назвою (сервер) і Region/Area з джерела НАЕ →
 * перевірка сервером (`dryRun`) → запис шматками з прогресом і підсумком помилок.
 *
 * Зіставлення (людина 06.11): із заголовком - збережене для таблиці (спільне) і автозіставлення за назвою/кодом, без
 * заголовка - за порядком; людина змінює будь-яке. «Все або нічого» (рішення 19.10): будь-яке зауваження розбору, нерозв'язане
 * значення, незіставлений обов'язковий стовпець, понад 5000 рядків чи відмова сервера на перевірці блокують ВЕСЬ запис.
 * Файл більший за 5 МБ не читається.
 */
export function PasteDialog({ permit, registryCode, tableName, fields, fromFile, onClose }: PasteDialogProps): JSX.Element {
  const queryClient = useQueryClient();
  const session = useSession();
  useRestoreFocus(true);
  // Пікер - лише для посилань на таблицю того самого дозволу; спільні реєстри й списки розв'язує сервер.
  const siblingFields = useMemo(() => fields.filter((field) => field.source.listScope === 'SamePermitTable'), [fields]);
  const picker = usePickerData(permit, siblingFields, true);
  const mappingQuery = useQuery({
    queryKey: ['permits', 'import-mapping', registryCode] as const,
    queryFn: () => getImportMapping(registryCode),
  });
  const saved = mappingQuery.data?.columns ?? {};

  const [text, setText] = useState('');
  // `null` - заголовок визначається сам (перший рядок схожий на назви стовпців); інакше - вибір людини.
  const [headerChoice, setHeaderChoice] = useState<boolean | null>(fromFile ? true : null);
  const [edited, setEdited] = useState<ColumnMapping | null>(null);
  const [remember, setRemember] = useState(true);
  const [file, setFile] = useState<File | null>(null);
  const [workbook, setWorkbook] = useState<{ file: File; sheet: string; sheets: readonly string[] } | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null);
  const [busy, setBusy] = useState<'check' | 'apply' | 'file' | null>(null);
  const [checked, setChecked] = useState<{ run: BatchRun; valid: readonly ImportRow[] } | null>(null);
  const [applied, setApplied] = useState<BatchRun | null>(null);
  const loadingCheck = usePendingLoading(busy === 'check');
  const loadingApply = usePendingLoading(busy === 'apply');

  const lines = useMemo(() => parseBlock(text).filter((line) => line.some((cell) => cell.trim() !== '')), [text]);
  const hasHeader = headerChoice ?? looksLikeHeader(lines[0] ?? [], fields, saved);
  const headers = useMemo(() => (hasHeader ? (lines[0] ?? []) : []), [hasHeader, lines]);
  const body = useMemo(() => (hasHeader ? lines.slice(1) : lines), [hasHeader, lines]);
  const width = useMemo(
    () => Math.min(MaxFileColumns, Math.max(headers.length, ...body.slice(0, 200).map((line) => line.length), 0)),
    [headers, body],
  );
  const automatic = useMemo(
    () => (hasHeader ? autoMapping(Array.from({ length: width }, (_, index) => headers[index] ?? ''), fields, saved) : orderMapping(width, fields)),
    [hasHeader, width, headers, fields, saved],
  );
  const mapping = edited !== null && edited.length === width ? edited : automatic;
  const derivable = mappingQuery.data?.derivable ?? [];
  const naeColumns = mappingQuery.data?.naeColumns ?? [];
  const problems = mappingProblems(mapping, fields, derivable, naeColumns);
  const mappingOk = problems.missingRequired.length === 0 && problems.duplicated.length === 0;

  const mapped = useMemo(() => mappedLines(body, mapping, fields), [body, mapping, fields]);
  const request = useMemo(() => lookupRequest(mapped, fields), [mapped, fields]);
  const requestKey = JSON.stringify(request);
  const [debouncedKey] = useDebouncedValue(requestKey, 300);
  const lookupQuery = useQuery({
    queryKey: ['permits', 'import-lookups', registryCode, debouncedKey] as const,
    queryFn: () => resolveImportLookups(registryCode, JSON.parse(debouncedKey) as typeof request),
    enabled: request.length > 0 && debouncedKey === requestKey,
    staleTime: 30_000,
  });
  const lookups = request.length === 0 ? NoLookups : debouncedKey === requestKey ? lookupQuery.data : undefined;

  const plan = useMemo(
    () => planRows(mapped, fields, serverResolver(fields, lookups, mappingQuery.data, picker.data), permit.id),
    [mapped, fields, lookups, mappingQuery.data, picker.data, permit.id],
  );
  const clean = plan.rows.filter((row) => row.clean);
  // «Все або нічого»: будь-яке зауваження розбору чи зіставлення блокує і перевірку, і запис.
  const planClean = isPlanClean(plan) && mappingOk;
  const unresolved = unresolvedGroups(plan.issues);
  // Нерозв'язані посилання - згруповано вище; тут лише зауваження клітинок і дублі ключа.
  const rowIssues = plan.issues.filter((issue) => !LookupReasons.includes(issue.reason));
  const labelOf = (code: string): string => fields.find((field) => field.column.code === code)?.column.label ?? code;
  const ready = !picker.pending && !mappingQuery.isPending && text.trim() !== '';
  const mayRemember = hasHeader && can(session.data, MappingPermission);

  function reset(): void {
    setChecked(null);
    setApplied(null);
  }

  function edit(next: string): void {
    setText(next);
    setEdited(null);
    reset();
  }

  function remap(index: number, code: string): void {
    const next = [...mapping];
    next[index] = code === '' ? null : code;
    setEdited(next);
    reset();
  }

  async function loadWorkbook(selected: File, sheet: string | null): Promise<void> {
    setBusy('file');
    try {
      const read = await readImportWorkbook(registryCode, selected, sheet);
      setWorkbook({ file: selected, sheet: read.sheet, sheets: read.sheets });
      edit(read.rows.map((row) => row.map((cell) => cell.replace(/\t/g, ' ')).join('\t')).join('\n'));
      setHeaderChoice(true);
    } catch (error) {
      showApiError(error);
    } finally {
      setBusy(null);
    }
  }

  async function loadFile(selected: File | null): Promise<void> {
    setFileError(null);
    setWorkbook(null);
    if (selected !== null && selected.size > MaxImportFileBytes) {
      setFile(null);
      setFileError(t('permits.page.paste.fileTooBig', { max: MaxImportFileMb }));
      return;
    }
    setFile(selected);
    if (selected === null) return;
    if (isWorkbook(selected)) {
      await loadWorkbook(selected, null);
      return;
    }
    try {
      edit(delimitedToTabs(await readText(selected)));
      setHeaderChoice(true);
    } catch (error) {
      showApiError(error);
    }
  }

  async function check(): Promise<void> {
    if (busy !== null || !planClean) return;
    setBusy('check');
    setProgress({ done: 0, total: clean.length });
    try {
      const run = await runImport(registryCode, clean, true, (done, total) => setProgress({ done, total }));
      // Хоч одна відмова сервера - до запису не йде жоден рядок.
      setChecked({ run, valid: run.failures.length === 0 ? clean : [] });
    } catch (error) {
      showApiError(error);
    } finally {
      setBusy(null);
      setProgress(null);
    }
  }

  /** Зберігає зіставлення таблиці (спільне), якщо воно змінилось. Збій не скасовує вже записаних рядків. */
  async function rememberMapping(): Promise<void> {
    if (!mayRemember || !remember) return;
    const next = mappingToSave(headers, mapping);
    const merged = { ...saved, ...next };
    if (Object.entries(next).every(([key, code]) => saved[key] === code)) return;
    try {
      await saveImportMapping(registryCode, merged, mappingQuery.data?.rowVersion ?? null);
      void queryClient.invalidateQueries({ queryKey: ['permits', 'import-mapping', registryCode] });
    } catch (error) {
      showApiError(error);
    }
  }

  async function apply(): Promise<void> {
    if (busy !== null || checked === null || checked.valid.length === 0) return;
    setBusy('apply');
    setProgress({ done: 0, total: checked.valid.length });
    try {
      const run = await runImport(registryCode, checked.valid, false, (done, total) => setProgress({ done, total }));
      if (run.refused) {
        // Перевірка перед записом відхилила рядки: нічого не записано, показуємо причини й закриваємо «Add».
        setChecked({ run, valid: [] });
        return;
      }
      void queryClient.invalidateQueries({ queryKey: permitKeys.rows(registryCode, permit.id) });
      void queryClient.invalidateQueries({ queryKey: permitKeys.item(permit.id) });
      if (run.stopped) {
        setApplied(run);
      } else {
        await rememberMapping();
        showDone(t('permits.page.paste.done', { count: run.ok }));
        onClose();
      }
    } catch (error) {
      showApiError(error);
    } finally {
      setBusy(null);
      setProgress(null);
    }
  }

  const failures = checked?.run.failures ?? applied?.failures ?? [];
  const blocked = plan.rows.length > 0 && (!planClean || failures.length > 0) && plan.pending !== true;
  const sample = fields.map((field) => field.column.label);
  const options = [
    { value: '', label: t('permits.page.paste.mapIgnore') },
    ...fields.map((field) => ({ value: field.column.code, label: field.column.required === true ? `${field.column.label} *` : field.column.label })),
  ];
  const derived = plan.derived ?? [];
  const underived = plan.underived ?? [];

  return (
    <Modal opened onClose={onClose} size="xl" title={t('permits.page.paste.title', { table: tableName })}>
      <Stack gap="md">
        <Text size="sm">{t('permits.page.paste.intro')}</Text>
        <Text size="sm" c="dimmed">
          {sample.join(' · ')}
        </Text>
        <FileInput
          label={t('permits.page.paste.file')}
          description={t('permits.page.paste.fileHintXlsx')}
          accept=".csv,.tsv,.txt,.xlsx,text/csv,text/plain,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
          value={file}
          onChange={(selected) => void loadFile(selected)}
          error={fileError}
          clearable
          disabled={busy === 'file'}
          data-autofocus={fromFile ? '' : undefined}
        />
        {workbook !== null && workbook.sheets.length > 1 && (
          <NativeSelect
            label={t('permits.page.paste.sheet')}
            data={[...workbook.sheets]}
            value={workbook.sheet}
            disabled={busy !== null}
            onChange={(event) => void loadWorkbook(workbook.file, event.currentTarget.value)}
          />
        )}
        <Textarea
          label={t('permits.page.paste.rows')}
          description={t('permits.page.paste.rowsHint')}
          minRows={7}
          autosize
          maxRows={14}
          value={text}
          onChange={(event) => edit(event.currentTarget.value)}
          data-autofocus={fromFile ? undefined : ''}
        />
        <Checkbox
          label={t('permits.page.paste.header')}
          checked={hasHeader}
          onChange={(event) => {
            setHeaderChoice(event.currentTarget.checked);
            setEdited(null);
            reset();
          }}
        />
        {width > 0 && (
          <div className="pp-map">
            <Text size="sm" fw={600} id="pp-map-title">
              {t('permits.page.paste.mapTitle')}
            </Text>
            <Text size="xs" c="dimmed">
              {hasHeader ? t('permits.page.paste.mapHintHeader') : t('permits.page.paste.mapHintOrder')}
            </Text>
            <table className="pp-map-table" aria-labelledby="pp-map-title">
              <thead>
                <tr>
                  <th scope="col">{t('permits.page.paste.mapFileColumn')}</th>
                  <th scope="col">{t('permits.page.paste.mapSample')}</th>
                  <th scope="col">{t('permits.page.paste.mapPermitColumn')}</th>
                </tr>
              </thead>
              <tbody>
                {Array.from({ length: width }, (_, index) => {
                  const name = hasHeader && (headers[index] ?? '').trim() !== '' ? (headers[index] ?? '').trim() : t('permits.page.paste.mapColumnN', { n: index + 1 });
                  const example = body.find((line) => (line[index] ?? '').trim() !== '')?.[index]?.trim() ?? '';
                  return (
                    <tr key={index}>
                      <th scope="row">{name}</th>
                      <td className="pp-map-sample">{example}</td>
                      <td>
                        <NativeSelect
                          size="xs"
                          aria-label={t('permits.page.paste.mapTo', { column: name })}
                          data={options}
                          value={mapping[index] ?? ''}
                          onChange={(event) => remap(index, event.currentTarget.value)}
                        />
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            {problems.missingRequired.length > 0 && (
              <Banner tone="warning" title={t('permits.page.paste.mapMissing', { columns: problems.missingRequired.map(labelOf).join(', ') })} />
            )}
            {problems.duplicated.length > 0 && (
              <Banner tone="warning" title={t('permits.page.paste.mapTwice', { columns: problems.duplicated.map(labelOf).join(', ') })} />
            )}
            {mayRemember && (
              <Checkbox
                label={t('permits.page.paste.mapRemember', { table: tableName })}
                checked={remember}
                onChange={(event) => setRemember(event.currentTarget.checked)}
              />
            )}
          </div>
        )}
        <Text size="sm" role="status">
          {plan.rows.length === 0
            ? t('permits.page.paste.nothing')
            : plan.pending === true
              ? t('permits.page.paste.resolving')
              : t('permits.page.paste.summary', { rows: clean.length, table: tableName, bad: plan.rows.length - clean.length })}
        </Text>
        {lookupQuery.isError && <Banner tone="danger" title={t('permits.page.paste.lookupFailed')} />}
        {plan.extraCells > 0 && <Text size="sm" c="dimmed">{t('permits.page.paste.extra', { count: plan.extraCells })}</Text>}
        {unresolved.length > 0 && (
          <div>
            <Text size="sm" fw={600}>{t('permits.page.paste.unresolved')}</Text>
            <ul className="pp-issues" aria-label={t('permits.page.paste.unresolved')}>
              {unresolved.slice(0, ShownIssues).map((group) => (
                <li key={`${group.column}-${group.text}-${group.reason}`}>{unresolvedText(group, labelOf)}</li>
              ))}
            </ul>
            {unresolved.length > ShownIssues && (
              <Text size="xs" c="dimmed">{t('permits.page.paste.moreIssues', { count: unresolved.length - ShownIssues })}</Text>
            )}
          </div>
        )}
        {rowIssues.length > 0 && (
          <div>
            <Text size="sm" fw={600}>{t('permits.page.paste.issues')}</Text>
            <ul className="pp-issues">
              {rowIssues.slice(0, ShownIssues).map((issue) => (
                <li key={`${String(issue.row)}-${issue.column}-${issue.reason}`}>{issueText(issue, labelOf)}</li>
              ))}
            </ul>
            {rowIssues.length > ShownIssues && (
              <Text size="xs" c="dimmed">{t('permits.page.paste.moreIssues', { count: rowIssues.length - ShownIssues })}</Text>
            )}
          </div>
        )}
        {derived.length > 0 && (
          <div>
            <Text size="sm" fw={600}>{t('permits.page.paste.derived', { count: derived.length })}</Text>
            <ul className="pp-issues" aria-label={t('permits.page.paste.derived', { count: derived.length })}>
              {derived.slice(0, ShownIssues).map((mark) => (
                <li key={`${String(mark.row)}-${mark.column}`}>
                  {t('permits.page.paste.derivedItem', { row: mark.row, column: labelOf(mark.column), value: mark.label })}
                </li>
              ))}
            </ul>
          </div>
        )}
        {underived.length > 0 && (
          <Text size="sm" c="dimmed">
            {t('permits.page.paste.underived', { count: underived.length, columns: [...new Set(underived.map((mark) => labelOf(mark.column)))].join(', ') })}
          </Text>
        )}
        {blocked && <Banner tone="warning" title={t('permits.page.paste.blocked')} />}
        {progress !== null && (
          <Progress value={progress.total === 0 ? 0 : (progress.done / progress.total) * 100} aria-label={t('permits.page.paste.progress', { done: progress.done, total: progress.total })} />
        )}
        {checked !== null && (
          <Banner
            tone={checked.run.failures.length === 0 ? 'success' : 'warning'}
            title={t('permits.page.paste.checked', { ok: checked.valid.length, bad: checked.run.failures.length })}
          />
        )}
        {applied?.stopped === true && (
          <Banner tone="danger" title={t('permits.page.paste.stopped', { written: applied.ok })} text={t('permits.page.paste.stoppedText')} />
        )}
        {failures.length > 0 && (
          <ul className="pp-issues" aria-label={t('permits.page.paste.failures')}>
            {failures.slice(0, ShownIssues).map((failure) => (
              <li key={failure.index}>{t('permits.page.paste.failure', { row: failure.index, text: failure.messages.join('; ') })}</li>
            ))}
          </ul>
        )}
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose}>
            {t('common.cancel')}
          </Button>
          <Button variant="default" disabled={!ready || !planClean || busy !== null} loading={loadingCheck} onClick={() => void check()}>
            {t('permits.page.paste.check')}
          </Button>
          <Button disabled={checked === null || !planClean || checked.valid.length === 0 || busy !== null} loading={loadingApply} onClick={() => void apply()}>
            {t('permits.page.paste.add', { count: checked?.valid.length ?? 0 })}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
