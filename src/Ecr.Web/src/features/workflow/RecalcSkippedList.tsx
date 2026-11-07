import type { JSX } from 'react';
import { Alert, Anchor, List, Stack, Text } from '@mantine/core';
import { Link } from 'react-router-dom';
import type { components } from '@/api/schema';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';

/** Аркуш, який перерахунок пропустив (затверджений), — `02-contracts.md`, UAT-recalc-ux. */
export type SkippedSheet = components['schemas']['SkippedSheet'];

/** Відповідь `POST …/recalculate` (проєкт або документ): задача й пропущені аркуші. */
export interface RecalcRequestOutcome {
  readonly jobId?: string | null;
  readonly skipped?: readonly SkippedSheet[] | null;
  readonly skippedTruncated?: boolean;
  readonly nothingToRecalculate?: boolean;
}

/** Адреса аркуша документа в періоді — та сама форма, що на сторінці документа (`?periodKey=&sheet=`). */
export function skippedSheetHref(sheet: Pick<SkippedSheet, 'documentId' | 'periodKey' | 'sheetCode'>): string {
  const query = new URLSearchParams({ periodKey: String(sheet.periodKey), sheet: sheet.sheetCode });
  return `/documents/${String(sheet.documentId)}?${query.toString()}`;
}

interface RecalcSkippedListProps {
  readonly outcome: RecalcRequestOutcome;
}

/**
 * ✎ UAT-recalc-ux (UAT 07.11: «Recalculate» відмовляв «Period … has submitted sheets», і людина не могла знайти, які
 * саме): після перерахунку — «N затверджених аркушів пропущено» з розгорнутим переліком, кожен рядок — посилання на
 * документ, аркуш і період. «Рахувати нічого» — не помилка, а той самий перелік із поясненням.
 *
 * ⚠ Перелік — нативний `<details>`: розгортання з клавіатури (Enter/Space на `<summary>`) і стан для читача екрана
 * без власного коду.
 */
export function RecalcSkippedList({ outcome }: RecalcSkippedListProps): JSX.Element | null {
  const skipped = outcome.skipped ?? [];
  const nothing = outcome.nothingToRecalculate === true;

  if (skipped.length === 0 && !nothing) {
    return null;
  }

  const list =
    skipped.length === 0 ? null : (
      <details data-recalc-skipped="">
        <summary>
          <Text span size="sm">
            {t('recalc.skippedSummary', { count: String(skipped.length) })}
          </Text>
        </summary>
        <List size="sm" spacing="xs" mt="xs" listStyleType="none">
          {skipped.map((sheet) => {
            const documentName = localized(sheet.documentName ?? null);
            const sheetName = localized(sheet.sheetName);
            const label = `${documentName.length > 0 ? documentName : sheet.documentCode} · ${
              sheetName.length > 0 ? sheetName : sheet.sheetCode
            } · ${String(sheet.periodKey)}`;

            return (
              <List.Item key={`${String(sheet.documentId)}-${String(sheet.sheetDefId)}-${String(sheet.periodKey)}`}>
                <Anchor component={Link} to={skippedSheetHref(sheet)} data-recalc-skipped-link="">
                  {label}
                </Anchor>
              </List.Item>
            );
          })}
        </List>
        {outcome.skippedTruncated === true && (
          <Text size="xs" c="dimmed" mt="xs">
            {t('recalc.skippedTruncated', { shown: String(skipped.length) })}
          </Text>
        )}
      </details>
    );

  return nothing ? (
    <Alert color="statusInfo" title={t('recalc.nothingToRecalculate')} data-recalc-nothing="">
      {list}
    </Alert>
  ) : (
    <Stack gap="xs">{list}</Stack>
  );
}
