import type { JSX } from 'react';
import { Button, Text } from '@mantine/core';
import { Link } from 'react-router-dom';
import { formatNumber } from '@/shared/format/number';
import { t } from '@/shared/i18n';
import { Banner } from '@/shared/ui/Banner';
import type { AirImportResult } from '../airTypes';
import { PermitsPaths } from '../../shell/permitPaths';

export interface ApplyStepProps {
  readonly result: AirImportResult;
  readonly permitId: number;
}

/** Крок 4: підсумок застосованого імпорту — скільки джерел і речовин, що створено й що замінено. */
export function ApplyStep({ result, permitId }: ApplyStepProps): JSX.Element {
  return (
    <div className="pi-page">
      <Banner
        tone="success"
        title={t('permits.air.import.done.title', { permit: result.permitNumber })}
        text={t('permits.air.import.done.text')}
      />
      <div className="pi-stats" data-testid="air-import-result">
        <div className="pi-stat">
          {t('permits.air.import.done.sources')}
          <b>{formatNumber(result.sources)}</b>
        </div>
        <div className="pi-stat">
          {t('permits.air.import.done.substances')}
          <b>{formatNumber(result.substances)}</b>
        </div>
        <div className="pi-stat">
          {t('permits.air.import.done.createdNae')}
          <b>{formatNumber(result.createdNae)}</b>
        </div>
        <div className="pi-stat">
          {t('permits.air.import.done.replacedOwn')}
          <b>{formatNumber(result.replacedOwn)}</b>
        </div>
      </div>
      {result.replacedOthers.length > 0 && (
        <div className="pi-panel">
          <Text fw={600}>{t('permits.air.import.done.others')}</Text>
          <ul>
            {result.replacedOthers.map((owner) => (
              <li key={owner.ownerKey}>
                {t('permits.air.import.replace.owner', {
                  label: owner.label ?? owner.ownerKey,
                  parents: formatNumber(owner.parents),
                  rows: formatNumber(owner.children),
                })}
              </li>
            ))}
          </ul>
        </div>
      )}
      <Text size="sm" c="dimmed">
        {t('permits.air.import.done.fingerprint', { value: result.fingerprint.slice(0, 12) })}
      </Text>
      <div className="pi-actions">
        <Button component={Link} to={`${PermitsPaths.permits}/${String(permitId)}?tab=air`}>
          {t('permits.air.import.done.open')}
        </Button>
      </div>
    </div>
  );
}
