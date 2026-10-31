import type { JSX } from 'react';
import { Alert, List, Stack, Text } from '@mantine/core';
import { KeyValue } from '@/shared/ui/KeyValue';
import { Timestamp } from '@/shared/ui/Timestamp';
import { t } from '@/shared/i18n';
import type { RawCoverageView, RawInterval } from './api';
import { lastCollectionItems } from './lastCollectionItems';

/** Скільки прогалин назвати поіменно; решта — числом. */
const ShownGaps = 10;

/**
 * Покриття вікна збором (`D-463`): частка вікна, за яку збір відзвітував успіхом, прогалини і стан останнього
 * прогону.
 *
 * ⛔ «Немає точок» і «не збирали» — різні стани (ІНТ-3.3: ознака здоров'я — журнал покриття, а не тиша).
 * Порожній графік без цієї секції читався б як «датчик мовчав», хоча збір міг просто не запускатися.
 */
export function CoverageSummary({ coverage }: { readonly coverage: RawCoverageView }): JSX.Element {
  const total = Date.parse(coverage.toUtc) - Date.parse(coverage.fromUtc);
  const covered = coverage.covered.reduce((sum, i) => sum + (Date.parse(i.toUtc) - Date.parse(i.fromUtc)), 0);
  const percent = total <= 0 ? 0 : Math.round((covered / total) * 1000) / 10;

  return (
    <Stack gap="xs" data-pi-coverage="">
      <KeyValue items={lastCollectionItems(coverage.lastCollection, percent)} />
      {coverage.isTruncated && (
        <Alert color="statusWarning" title={t('piData.coverageTruncated')}>
          {t('piData.coverageTruncatedHint')}
        </Alert>
      )}
      {coverage.gaps.length === 0 ? (
        <Text size="sm" c="dimmed">
          {t('piData.noGaps')}
        </Text>
      ) : (
        <>
          <Text size="sm" fw={500}>
            {t('piData.gaps', { count: coverage.gaps.length })}
          </Text>
          <List size="sm" spacing={2} data-pi-gaps="">
            {coverage.gaps.slice(0, ShownGaps).map((gap) => (
              <List.Item key={gap.fromUtc}>
                <Interval interval={gap} />
              </List.Item>
            ))}
          </List>
          {coverage.gaps.length > ShownGaps && (
            <Text size="xs" c="dimmed">
              {t('piData.moreGaps', { count: coverage.gaps.length - ShownGaps })}
            </Text>
          )}
        </>
      )}
    </Stack>
  );
}

function Interval({ interval }: { readonly interval: RawInterval }): JSX.Element {
  return (
    <>
      <Timestamp value={interval.fromUtc} /> — <Timestamp value={interval.toUtc} />
    </>
  );
}
