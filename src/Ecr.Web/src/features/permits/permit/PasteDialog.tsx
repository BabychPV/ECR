import { useMemo, useState, type JSX } from 'react';
import { Button, Checkbox, FileInput, Group, Modal, Progress, Stack, Text, Textarea } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { showApiError, showDone } from '@/shared/ui/notify';
import type { Permit } from '../api/types';
import { MaxPastedRows } from '../lib/pasteRows';
import {
  delimitedToTabs,
  isPlanClean,
  MaxImportFileBytes,
  MaxImportFileMb,
  planImport,
  runImport,
  type BatchRun,
  type ImportIssue,
  type ImportRow,
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

/**
 * «Paste from Excel…» і «Import file…» (мокап `paste-rows`): блок із буфера (табуляції) або CSV-файл → розбір
 * `parsePastedRows` → перевірка сервером (`dryRun`) → запис шматками з прогресом і підсумком помилок.
 *
 * ⚠ XLSX-файл клієнт не читає (бібліотеки для цього в збірці немає, `package.json` не змінюється): Excel-таблицю
 * копіюють і вставляють, або зберігають як CSV. «Все або нічого» (рішення 19.10): будь-яке зауваження розбору,
 * понад 5000 рядків чи відмова сервера на перевірці блокують ВЕСЬ запис - проблеми перелічені, «Add N rows» недоступна.
 * Це відхилення від мокапу, де такі рядки додавались і червонились у таблиці. CSV більший за 5 МБ не читається.
 */
export function PasteDialog({ permit, registryCode, tableName, fields, fromFile, onClose }: PasteDialogProps): JSX.Element {
  const queryClient = useQueryClient();
  const picker = usePickerData(permit, fields, true);
  const [text, setText] = useState('');
  const [skipHeader, setSkipHeader] = useState(fromFile);
  const [file, setFile] = useState<File | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const [progress, setProgress] = useState<{ done: number; total: number } | null>(null);
  const [busy, setBusy] = useState<'check' | 'apply' | null>(null);
  const [checked, setChecked] = useState<{ run: BatchRun; valid: readonly ImportRow[] } | null>(null);
  const [applied, setApplied] = useState<BatchRun | null>(null);
  const loadingCheck = usePendingLoading(busy === 'check');
  const loadingApply = usePendingLoading(busy === 'apply');

  const plan = useMemo(
    () => planImport(text, fields, picker.data, permit.id, skipHeader),
    [text, fields, picker.data, permit.id, skipHeader],
  );
  const clean = plan.rows.filter((row) => row.clean);
  // «Все або нічого»: будь-яке зауваження розбору блокує і перевірку, і запис.
  const planClean = isPlanClean(plan);
  const labelOf = (code: string): string => fields.find((field) => field.column.code === code)?.column.label ?? code;
  const ready = !picker.pending && text.trim() !== '';

  function edit(next: string): void {
    setText(next);
    setChecked(null);
    setApplied(null);
  }

  async function loadFile(selected: File | null): Promise<void> {
    setFileError(null);
    if (selected !== null && selected.size > MaxImportFileBytes) {
      setFile(null);
      setFileError(t('permits.page.paste.fileTooBig', { max: MaxImportFileMb }));
      return;
    }
    setFile(selected);
    if (selected === null) return;
    try {
      edit(delimitedToTabs(await readText(selected)));
      setSkipHeader(true);
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
  const blocked = plan.rows.length > 0 && (!planClean || failures.length > 0);
  const sample = fields.map((field) => field.column.label);

  return (
    <Modal opened onClose={onClose} size="lg" title={t('permits.page.paste.title', { table: tableName })}>
      <Stack gap="md">
        <Text size="sm">{t('permits.page.paste.intro')}</Text>
        <Text size="sm" c="dimmed">
          {sample.join(' · ')}
        </Text>
        <FileInput
          label={t('permits.page.paste.file')}
          description={t('permits.page.paste.fileHint')}
          accept=".csv,.tsv,.txt,text/csv,text/plain"
          value={file}
          onChange={(selected) => void loadFile(selected)}
          error={fileError}
          clearable
          data-autofocus={fromFile ? '' : undefined}
        />
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
          checked={skipHeader}
          onChange={(event) => {
            setSkipHeader(event.currentTarget.checked);
            setChecked(null);
          }}
        />
        <Text size="sm" role="status">
          {plan.rows.length === 0
            ? t('permits.page.paste.nothing')
            : t('permits.page.paste.summary', { rows: clean.length, table: tableName, bad: plan.rows.length - clean.length })}
        </Text>
        {plan.extraCells > 0 && <Text size="sm" c="dimmed">{t('permits.page.paste.extra', { count: plan.extraCells })}</Text>}
        {plan.issues.length > 0 && (
          <div>
            <Text size="sm" fw={600}>{t('permits.page.paste.issues')}</Text>
            <ul className="pp-issues">
              {plan.issues.slice(0, ShownIssues).map((issue) => (
                <li key={`${String(issue.row)}-${issue.column}-${issue.reason}`}>{issueText(issue, labelOf)}</li>
              ))}
            </ul>
            {plan.issues.length > ShownIssues && (
              <Text size="xs" c="dimmed">{t('permits.page.paste.moreIssues', { count: plan.issues.length - ShownIssues })}</Text>
            )}
          </div>
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
