import type { JSX } from 'react';
import { Anchor, Text, Title } from '@mantine/core';
import { Link } from 'react-router-dom';
import { formatDate } from '@/shared/format/datetime';
import { formatNumber } from '@/shared/format/number';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import type { Permit } from '../api/types';
import { PermitsPaths } from '../shell/permitPaths';
import { HeaderForm } from './HeaderForm';
import { expiringIn, PermitStatus, type PermitCapabilities } from './permitRules';
import { localName } from './names';
import './permit.css';

function parseDay(value: string | null): number | null {
  if (value === null) return null;
  const time = Date.parse(`${value}T00:00:00Z`);
  return Number.isNaN(time) ? null : time;
}

/** Де сьогодні на лінії чинності, у відсотках (0..100); `null`, якщо дат немає чи вікно порожнє. */
export function todayPercent(validFrom: string | null, validTo: string | null, today: string): number | null {
  const from = parseDay(validFrom);
  const to = parseDay(validTo);
  const now = parseDay(today);
  if (from === null || to === null || now === null || to <= from) return null;
  return Math.max(0, Math.min(100, Math.round(((now - from) / (to - from)) * 100)));
}

function todayIso(): string {
  const now = new Date();
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const dayOfMonth = String(now.getDate()).padStart(2, '0');
  return `${String(now.getFullYear())}-${month}-${dayOfMonth}`;
}

function Validity({ permit }: { readonly permit: Permit }): JSX.Element {
  const today = todayIso();
  const percent = todayPercent(permit.validFrom, permit.validTo, today);

  return (
    <section aria-labelledby="pp-validity-title" className="pp-section">
      <Title order={2} size="h4" id="pp-validity-title">
        {t('permits.page.validity.title')}
      </Title>
      {percent === null ? (
        <Text size="sm" c="dimmed">
          {t('permits.page.validity.none')}
        </Text>
      ) : (
        <div>
          <div className="pp-tl" role="img" aria-label={t('permits.page.validity.aria')}>
            <div className="pp-tl-bar" />
            <div className="pp-tl-run" style={{ width: `${String(percent)}%` }} />
            <div className="pp-tl-now" style={{ left: `${String(percent)}%` }} />
          </div>
          <div className="pp-tl-lbl">
            <span>{formatDate(permit.validFrom ?? '')}</span>
            <span>{t('permits.page.validity.today', { date: formatDate(today) })}</span>
            <span>{formatDate(permit.validTo ?? '')}</span>
          </div>
        </div>
      )}
      <Text size="sm" c="dimmed">
        {t('permits.page.validity.note')}
      </Text>
    </section>
  );
}

function Totals({ permit }: { readonly permit: Permit }): JSX.Element {
  return (
    <section aria-labelledby="pp-totals-title" className="pp-section">
      <Title order={2} size="h4" id="pp-totals-title">
        {t('permits.page.totals.title')}
      </Title>
      <Text size="sm" c="dimmed">
        {t('permits.page.totals.note')}
      </Text>
      <div className="pp-cards">
        {permit.tables.map((table) => (
          <div key={table.code} className="pp-card">
            <Title order={3} size="h5">
              {localName(table.nameL10n) || table.code}
            </Title>
            <div className="pp-card-row">
              <Text size="sm" c="dimmed">
                {t('permits.page.totals.rows', { count: table.rowCount })}
              </Text>
              {table.total !== null && (
                <Text size="sm" fw={600} ff="monospace">
                  {formatNumber(Number(table.total), { maximumFractionDigits: 5 })}
                </Text>
              )}
            </div>
          </div>
        ))}
      </div>
    </section>
  );
}

function ReadBy({ permit }: { readonly permit: Permit }): JSX.Element {
  return (
    <section aria-labelledby="pp-readby-title" className="pp-section">
      <Title order={2} size="h4" id="pp-readby-title">
        {t('permits.page.readBy.title')}
      </Title>
      {permit.boundSheets.length === 0 ? (
        <Text size="sm" c="dimmed">
          {t('permits.page.readBy.none')}
        </Text>
      ) : (
        <div className="pp-tw">
          <table className="pp-t">
            <caption className="mantine-visually-hidden">{t('permits.page.readBy.title')}</caption>
            <thead>
              <tr>
                <th scope="col">{t('permits.page.readBy.sheet')}</th>
                <th scope="col">{t('permits.page.readBy.binding')}</th>
                <th scope="col">{t('permits.page.readBy.how')}</th>
              </tr>
            </thead>
            <tbody>
              {permit.boundSheets.map((sheet) => (
                <tr key={`${sheet.bindingCode}-${sheet.sheetName}`}>
                  <th scope="row">{sheet.sheetName}</th>
                  <td>
                    <Anchor component={Link} to={`${PermitsPaths.bindings}/${encodeURIComponent(sheet.bindingCode)}`} size="sm">
                      {sheet.bindingCode}
                    </Anchor>
                  </td>
                  <td>{sheet.mode === 'Formulas' ? t('permits.page.readBy.formulas') : t('permits.page.readBy.rows')}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

export interface OverviewTabProps {
  readonly permit: Permit;
  readonly caps: PermitCapabilities;
  readonly onActivate: () => void;
  readonly onRenew: () => void;
}

/**
 * Вкладка «Overview» (мокап `overview()`): смуги про чернетку / скорий кінець, шапка, лінія чинності, підсумки
 * таблиць, «Read by». Перевірок типу тут немає: сервер їх у `PermitDto` не віддає, і малювати «все гаразд» без
 * даних означало б збрехати.
 */
export function OverviewTab({ permit, caps, onActivate, onRenew }: OverviewTabProps): JSX.Element {
  const ending = expiringIn(permit);

  return (
    <div className="pp-stack">
      {ending !== null && (
        <Banner
          tone="warning"
          title={t('permits.page.banner.endsTitle', { days: ending, date: formatDate(permit.validTo ?? '') })}
          text={t('permits.page.banner.endsText')}
          actions={caps.renew ? [{ label: t('permits.page.actions.renew'), onClick: onRenew }] : undefined}
        />
      )}
      {permit.status === PermitStatus.Draft && (
        <Banner
          tone="info"
          title={t('permits.page.banner.draftTitle')}
          text={t('permits.page.banner.draftText')}
          actions={caps.activate ? [{ label: t('permits.page.actions.activate'), onClick: onActivate }] : undefined}
        />
      )}
      <div className="pp-two">
        <HeaderForm permit={permit} editable={caps.editHeader} />
        <Validity permit={permit} />
      </div>
      <Totals permit={permit} />
      <ReadBy permit={permit} />
    </div>
  );
}
