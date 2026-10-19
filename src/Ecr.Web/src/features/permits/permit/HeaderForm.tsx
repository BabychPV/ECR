import { useState, type JSX } from 'react';
import { Button, Checkbox, Group, Stack, TextInput, Title } from '@mantine/core';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { EcrApiError } from '@/api/client';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import { formatDate } from '@/shared/format/datetime';
import { t } from '@/shared/i18n';
import { KeyValue } from '@/shared/ui/KeyValue';
import { showApiError, showDone } from '@/shared/ui/notify';
import { updatePermit } from '../api/permitsApi';
import type { Permit, PermitReportingFlags, UpdatePermitRequest } from '../api/types';
import { DateField } from './DateField';
import { permitKeys } from './usePermitData';
import './permit.css';

/** Код відмови «токен застарів / дозвіл закінчився» (`PUT /permits/{id}`): екран перечитує дозвіл. */
export const PermitStateConflictCode = 'ECR-PRM-4096';

interface HeaderDraft {
  readonly number: string;
  readonly location: string;
  readonly issueDate: string;
  readonly validFrom: string;
  readonly validTo: string;
  readonly actualEnd: string;
  readonly flags: PermitReportingFlags;
}

function draftOf(permit: Permit): HeaderDraft {
  return {
    number: permit.number,
    location: permit.location ?? '',
    issueDate: permit.issueDate ?? '',
    validFrom: permit.validFrom ?? '',
    validTo: permit.validTo ?? '',
    actualEnd: permit.actualEnd ?? '',
    flags: permit.reportingFlags,
  };
}

/** Помилки шапки до відправки: дзеркало перевірок сервера (номер, півінтервал чинності). */
export function headerErrors(draft: HeaderDraft): { number?: string; validFrom?: string; validTo?: string } {
  const errors: { number?: string; validFrom?: string; validTo?: string } = {};
  if (draft.number.trim() === '') errors.number = t('permits.page.header.numberRequired');
  if (draft.validFrom === '') errors.validFrom = t('permits.page.header.dateRequired');
  if (draft.validTo === '') errors.validTo = t('permits.page.header.dateRequired');
  else if (draft.validFrom !== '' && draft.validTo <= draft.validFrom) {
    errors.validTo = t('permits.page.header.validToAfter');
  }
  return errors;
}

function sameDraft(a: HeaderDraft, b: HeaderDraft): boolean {
  return (
    a.number === b.number &&
    a.location === b.location &&
    a.issueDate === b.issueDate &&
    a.validFrom === b.validFrom &&
    a.validTo === b.validTo &&
    a.actualEnd === b.actualEnd &&
    a.flags.form300 === b.flags.form300 &&
    a.flags.form301 === b.flags.form301 &&
    a.flags.form302 === b.flags.form302 &&
    a.flags.tox === b.flags.tox
  );
}

function formsLabel(flags: PermitReportingFlags): string {
  const names = [
    flags.form300 ? t('permits.page.header.form300') : null,
    flags.form301 ? t('permits.page.header.form301') : null,
    flags.form302 ? t('permits.page.header.form302') : null,
    flags.tox ? t('permits.page.header.tox') : null,
  ].filter((name): name is string => name !== null);
  return names.length === 0 ? t('permits.page.header.noForms') : names.join(', ');
}

function day(value: string | null): string {
  return value === null || value === '' ? '' : formatDate(value);
}

/** Шапка лише для читання (закінчений дозвіл або немає права): перелік пар, як у мокапі. */
function ReadOnlyHeader({ permit }: { readonly permit: Permit }): JSX.Element {
  return (
    <KeyValue
      items={[
        { label: t('permits.page.header.number'), value: permit.number, mono: true },
        { label: t('permits.page.header.type'), value: permit.typeCode },
        { label: t('permits.page.header.location'), value: permit.location ?? '' },
        { label: t('permits.page.header.issueDate'), value: day(permit.issueDate) },
        { label: t('permits.page.header.valid'), value: `${day(permit.validFrom)} – ${day(permit.validTo)}` },
        { label: t('permits.page.header.actualEnd'), value: day(permit.actualEnd) },
        { label: t('permits.page.header.reportedIn'), value: formsLabel(permit.reportingFlags) },
      ]}
    />
  );
}

function EditableHeader({ permit }: { readonly permit: Permit }): JSX.Element {
  const queryClient = useQueryClient();
  const original = draftOf(permit);
  const [draft, setDraft] = useState<HeaderDraft>(original);
  const [shownErrors, setShownErrors] = useState(false);

  const save = useMutation<Permit, Error, UpdatePermitRequest>({
    mutationKey: ['permits', 'item', permit.id, 'update'],
    mutationFn: (body) => updatePermit(permit.id, body),
    onSuccess: (next) => {
      queryClient.setQueryData(permitKeys.item(permit.id), next);
      void queryClient.invalidateQueries({ queryKey: ['permits', 'list'] });
      showDone(t('permits.page.header.saved'));
    },
    onError: (error) => {
      showApiError(error);
      // Токен застарів: чужа правка або дозвіл закінчився. Екран бере свіжий дозвіл, форма перезбирається з нього.
      if (error instanceof EcrApiError && error.problem.errorCode === PermitStateConflictCode) {
        void queryClient.invalidateQueries({ queryKey: permitKeys.item(permit.id) });
      }
    },
  });
  const saving = usePendingLoading(save.isPending);

  const errors = shownErrors ? headerErrors(draft) : {};
  const dirty = !sameDraft(draft, original);

  function set<K extends keyof HeaderDraft>(key: K, value: HeaderDraft[K]): void {
    setDraft((current) => ({ ...current, [key]: value }));
  }

  function submit(): void {
    if (save.isPending) return;
    setShownErrors(true);
    if (Object.keys(headerErrors(draft)).length > 0) return;
    save.mutate({
      number: draft.number.trim(),
      location: draft.location.trim() === '' ? null : draft.location.trim(),
      issueDate: draft.issueDate === '' ? null : draft.issueDate,
      validFrom: draft.validFrom,
      validTo: draft.validTo,
      actualEnd: draft.actualEnd === '' ? null : draft.actualEnd,
      reportingFlags: draft.flags,
      rowVersion: permit.rowVersion,
    });
  }

  const flag = (key: keyof PermitReportingFlags, label: string): JSX.Element => (
    <Checkbox
      label={label}
      checked={draft.flags[key]}
      onChange={(event) => set('flags', { ...draft.flags, [key]: event.currentTarget.checked })}
    />
  );

  return (
    <form
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
      noValidate
      aria-label={t('permits.page.header.title')}
    >
      <Stack gap="md">
        <fieldset className="pp-group">
          <legend>{t('permits.page.header.groupIdentity')}</legend>
          <div className="pp-fields">
            <TextInput
              label={t('permits.page.header.number')}
              required
              value={draft.number}
              error={errors.number}
              onChange={(event) => set('number', event.currentTarget.value)}
            />
            <TextInput
              label={t('permits.page.header.location')}
              value={draft.location}
              onChange={(event) => set('location', event.currentTarget.value)}
            />
          </div>
        </fieldset>
        <fieldset className="pp-group">
          <legend>{t('permits.page.header.groupValidity')}</legend>
          <div className="pp-fields">
            <DateField
              label={t('permits.page.header.issueDate')}
              value={draft.issueDate}
              onChange={(value) => set('issueDate', value)}
            />
            <DateField
              label={t('permits.page.header.validFrom')}
              required
              value={draft.validFrom}
              error={errors.validFrom}
              onChange={(value) => set('validFrom', value)}
            />
            <DateField
              label={t('permits.page.header.validTo')}
              description={t('permits.page.header.validToHint')}
              required
              value={draft.validTo}
              error={errors.validTo}
              onChange={(value) => set('validTo', value)}
            />
            <DateField
              label={t('permits.page.header.actualEnd')}
              value={draft.actualEnd}
              onChange={(value) => set('actualEnd', value)}
            />
          </div>
        </fieldset>
        <fieldset className="pp-group">
          <legend>{t('permits.page.header.groupForms')}</legend>
          <Group gap="lg">
            {flag('form300', t('permits.page.header.form300'))}
            {flag('form301', t('permits.page.header.form301'))}
            {flag('form302', t('permits.page.header.form302'))}
            {flag('tox', t('permits.page.header.tox'))}
          </Group>
        </fieldset>
        <Group justify="flex-end" gap="xs">
          <Button
            variant="default"
            disabled={!dirty || save.isPending}
            onClick={() => {
              setDraft(original);
              setShownErrors(false);
            }}
          >
            {t('permits.page.header.discard')}
          </Button>
          <Button type="submit" loading={saving} disabled={!dirty}>
            {t('permits.page.header.save')}
          </Button>
        </Group>
      </Stack>
    </form>
  );
}

/**
 * Шапка дозволу: форма за групами (Identity · Validity · Reporting forms) для чернетки й чинного дозволу з правом
 * запису, інакше перелік пар. `key` на токені: збережений чи перечитаний дозвіл перезбирає форму зі свіжих значень.
 */
export function HeaderForm({ permit, editable }: { readonly permit: Permit; readonly editable: boolean }): JSX.Element {
  return (
    <section aria-labelledby="pp-header-title" className="pp-section">
      <Title order={2} size="h4" id="pp-header-title">
        {t('permits.page.header.title')}
      </Title>
      {editable ? <EditableHeader key={permit.rowVersion} permit={permit} /> : <ReadOnlyHeader permit={permit} />}
    </section>
  );
}
