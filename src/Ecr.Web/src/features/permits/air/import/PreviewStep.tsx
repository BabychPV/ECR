import { useMemo, type JSX } from 'react';
import { Button, Checkbox, Select, Text, Title } from '@mantine/core';
import { formatNumber } from '@/shared/format/number';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import type { AirPreview, MissingReference, OwnerReplace } from '../airTypes';
import { useNaeTree } from '../useAirLimits';
import {
  applyGate,
  errorIssues,
  resolutionKey,
  UnsetResolution,
  unresolved,
  warningIssues,
  type ImportDraft,
  type Resolution,
  type ResolutionChoice,
} from './importModel';
import { IssuesTable } from './IssuesTable';
import { nodeOptions } from './naeNodes';

export interface PreviewStepProps {
  readonly preview: AirPreview;
  readonly draft: ImportDraft;
  readonly onDraft: (patch: Partial<ImportDraft>) => void;
  /** Параметри змінились після перегляду: застосувати не можна, поки його не повторено. */
  readonly stale: boolean;
  readonly onRerun: () => void;
  readonly rerunning: boolean;
  readonly onApply: () => void;
  readonly applying: boolean;
  readonly mayApply: boolean;
}

function ownerLine(owner: OwnerReplace): string {
  return t('permits.air.import.replace.owner', {
    label: owner.label ?? owner.ownerKey,
    parents: formatNumber(owner.parents),
    rows: formatNumber(owner.children),
  });
}

function MissingTable({
  missing,
  draft,
  onDraft,
}: {
  readonly missing: readonly MissingReference[];
  readonly draft: ImportDraft;
  readonly onDraft: (patch: Partial<ImportDraft>) => void;
}): JSX.Element {
  const tree = useNaeTree(missing.length > 0);
  const options = useMemo(() => nodeOptions(tree.data?.nodes ?? []), [tree.data]);
  const open = unresolved(missing, draft.resolutions);

  const set = (item: MissingReference, resolution: Resolution): void =>
    onDraft({ resolutions: { ...draft.resolutions, [resolutionKey(item)]: resolution } });

  const setAll = (resolution: Resolution): void => {
    const next = { ...draft.resolutions };
    for (const item of open) next[resolutionKey(item)] = resolution;
    onDraft({ resolutions: next });
  };

  const choices: { value: ResolutionChoice; label: string }[] = [
    { value: 'unset', label: t('permits.air.import.missing.choose') },
    { value: 'create', label: t('permits.air.import.missing.create') },
    { value: 'skip', label: t('permits.air.import.missing.skip') },
  ];

  return (
    <section className="pi-panel" aria-labelledby="pi-missing-title">
      <Title order={2} size="h4" id="pi-missing-title">
        {t('permits.air.import.missing.title', { count: formatNumber(missing.length) })}
      </Title>
      <Text size="sm" c="dimmed">
        {t('permits.air.import.missing.hint')}
      </Text>
      {tree.isError && <ErrorAlert error={tree.error} onRetry={() => void tree.refetch()} />}
      {open.length > 0 && (
        <div className="pi-actions" style={{ justifyContent: 'flex-start' }}>
          <Button variant="default" onClick={() => setAll({ choice: 'skip', createUnder: '' })}>
            {t('permits.air.import.missing.skipAll', { count: formatNumber(open.length) })}
          </Button>
        </div>
      )}
      <div className="pa-scroll" role="region" aria-label={t('permits.air.import.missing.aria')} tabIndex={0}>
        <table className="pi-table">
          <thead>
            <tr>
              <th scope="col">{t('permits.air.import.missing.code')}</th>
              <th scope="col" className="pa-num">
                {t('permits.air.import.issues.row')}
              </th>
              <th scope="col">{t('permits.air.import.missing.group')}</th>
              <th scope="col">{t('permits.air.import.missing.action')}</th>
            </tr>
          </thead>
          <tbody>
            {missing.map((item) => {
              const resolution = draft.resolutions[resolutionKey(item)] ?? UnsetResolution;

              return (
                <tr key={resolutionKey(item)}>
                  <th scope="row" className="pa-code">
                    {item.code}
                  </th>
                  <td className="pa-num">{item.row}</td>
                  <td>{item.groupLabel ?? '—'}</td>
                  <td>
                    <div className="pi-grid">
                      <Select
                        aria-label={t('permits.air.import.missing.actionFor', { code: item.code })}
                        data={choices}
                        allowDeselect={false}
                        value={resolution.choice}
                        onChange={(value) =>
                          set(item, {
                            choice: value === 'create' || value === 'skip' ? value : 'unset',
                            createUnder: resolution.createUnder,
                          })
                        }
                      />
                      {resolution.choice === 'create' && (
                        <Select
                          aria-label={t('permits.air.import.missing.underFor', { code: item.code })}
                          placeholder={t('permits.air.import.missing.under')}
                          data={options as { value: string; label: string }[]}
                          searchable
                          nothingFoundMessage={t('permits.air.import.missing.noNodes')}
                          value={resolution.createUnder === '' ? null : resolution.createUnder}
                          onChange={(value) => set(item, { choice: 'create', createUnder: value ?? '' })}
                        />
                      )}
                    </div>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function ReplacePanel({
  preview,
  draft,
  onDraft,
}: {
  readonly preview: AirPreview;
  readonly draft: ImportDraft;
  readonly onDraft: (patch: Partial<ImportDraft>) => void;
}): JSX.Element {
  const { replace, diff } = preview.preview;

  return (
    <section className="pi-panel" data-emphasis="true" aria-labelledby="pi-replace-title">
      <Title order={2} size="h4" id="pi-replace-title">
        {t('permits.air.import.replace.title')}
      </Title>
      <Text size="sm" c="dimmed">
        {t('permits.air.import.replace.scope', {
          permit: preview.permitNumber,
          region: preview.region,
          area: preview.area,
        })}
      </Text>
      <div className="pi-row">
        <span>{t('permits.air.import.replace.new')}</span>
        <b>
          {t('permits.air.import.replace.counts', {
            parents: formatNumber(diff.newParents + diff.changedParents + diff.unchangedParents),
            rows: formatNumber(diff.children),
          })}
        </b>
      </div>
      <div className="pi-row">
        <span>{t('permits.air.import.replace.thisOwner')}</span>
        <b>
          {t('permits.air.import.replace.counts', {
            parents: formatNumber(replace.thisOwner.parents),
            rows: formatNumber(replace.thisOwner.children),
          })}
        </b>
      </div>
      <dl className="pi-diff" aria-label={t('permits.air.import.diff.aria')}>
        <div className="pi-row">
          <dt>{t('permits.air.import.diff.new')}</dt>
          <dd>{formatNumber(diff.newParents)}</dd>
        </div>
        <div className="pi-row">
          <dt>{t('permits.air.import.diff.changed')}</dt>
          <dd>{formatNumber(diff.changedParents)}</dd>
        </div>
        <div className="pi-row">
          <dt>{t('permits.air.import.diff.unchanged')}</dt>
          <dd>{formatNumber(diff.unchangedParents)}</dd>
        </div>
        <div className="pi-row">
          <dt>{t('permits.air.import.diff.removed')}</dt>
          <dd>{formatNumber(diff.removedParents)}</dd>
        </div>
        <div className="pi-row">
          <dt>{t('permits.air.import.diff.skipped')}</dt>
          <dd>{formatNumber(diff.skippedParents)}</dd>
        </div>
      </dl>
      {replace.confirmationRequired && (
        <div role="group" aria-labelledby="pi-others-title" className="pi-others">
          <Text id="pi-others-title" fw={600} c="orange.8">
            {t('permits.air.import.replace.others')}
          </Text>
          <ul>
            {replace.otherOwners.map((owner) => (
              <li key={owner.ownerKey}>{ownerLine(owner)}</li>
            ))}
          </ul>
          <Checkbox
            label={t('permits.air.import.replace.confirm')}
            checked={draft.confirmedReplace}
            onChange={(event) => onDraft({ confirmedReplace: event.currentTarget.checked })}
          />
        </div>
      )}
    </section>
  );
}

const ReasonText = {
  errors: () => t('permits.air.import.gate.errors'),
  unresolved: () => t('permits.air.import.gate.unresolved'),
  confirm: () => t('permits.air.import.gate.confirm'),
  stale: () => t('permits.air.import.gate.stale'),
} as const;

/**
 * Крок 3: перегляд імпорту в дозвіл. Лічильники, знахідки (помилки блокують, попередження ні), відсутні НАЕ з
 * рішенням «створити під вузлом / пропустити», обсяг заміни (рядки ІНШИХ дозволів — лише з явним підтвердженням) і
 * «Apply import (all or nothing)». Застосувати можна лише свіжий перегляд: відбиток плану їде в `apply`.
 */
export function PreviewStep(props: PreviewStepProps): JSX.Element {
  const { preview, draft, onDraft, stale, onRerun, rerunning, onApply, applying, mayApply } = props;
  const body = preview.preview;
  const gate = applyGate(preview, draft, stale);
  const errors = errorIssues(body.issues);
  const warnings = warningIssues(body.issues);

  return (
    <div className="pi-page">
      {stale && (
        <Banner
          tone="warning"
          title={t('permits.air.import.stale.title')}
          text={t('permits.air.import.stale.text')}
        />
      )}
      <div className="pi-stats">
        <div className="pi-stat">
          {t('permits.air.import.stats.parents')}
          <b>{formatNumber(body.stats.parents)}</b>
        </div>
        <div className="pi-stat">
          {t('permits.air.import.stats.missing')}
          <b>{formatNumber(body.missing.length)}</b>
        </div>
        <div className="pi-stat">
          {t('permits.air.import.stats.children')}
          <b>{formatNumber(body.stats.children)}</b>
        </div>
        <div className="pi-stat">
          {t('permits.air.import.stats.errors')}
          <b>{formatNumber(errors.length)}</b>
        </div>
        <div className="pi-stat">
          {t('permits.air.import.stats.warnings')}
          <b>{formatNumber(warnings.length)}</b>
        </div>
      </div>
      <div className="pi-split">
        <div className="pi-page">
          {body.missing.length > 0 && <MissingTable missing={body.missing} draft={draft} onDraft={onDraft} />}
          <section aria-labelledby="pi-issues-title" className="pi-page">
            <Title order={2} size="h4" id="pi-issues-title">
              {t('permits.air.import.issues.title')}
            </Title>
            <IssuesTable issues={body.issues} dropped={body.droppedIssues} />
          </section>
        </div>
        <div className="pi-page">
          <ReplacePanel preview={preview} draft={draft} onDraft={onDraft} />
          {!gate.allowed && (
            <ul className="pi-reasons" aria-label={t('permits.air.import.gate.aria')}>
              {gate.reasons.map((reason) => (
                <li key={reason}>{ReasonText[reason]()}</li>
              ))}
            </ul>
          )}
          <div className="pi-actions">
            {stale && (
              <Button variant="default" loading={rerunning} onClick={onRerun}>
                {t('permits.air.import.rerun')}
              </Button>
            )}
            <Button disabled={!gate.allowed || !mayApply} loading={applying} onClick={onApply}>
              {t('permits.air.import.apply')}
            </Button>
          </div>
        </div>
      </div>
    </div>
  );
}
