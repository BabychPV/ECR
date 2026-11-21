import type { JSX } from 'react';
import { Text } from '@mantine/core';
import { formatNumber } from '@/shared/format';
import { t } from '@/shared/i18n';
import { isTruncated, type ReadList } from './truncatedOption';

/**
 * «Показано перші N; є ще» під переліком, який `fetchAllPages` дочитав лише до запобіжника (PSS-19c, рецензія L4).
 *
 * ⚠ Запобіжник — 10 000 записів, тож на чинних обсягах примітки не видно ніколи; але перелік, що мовчки обрізався,
 * — саме той дефект, який PSS-19c закриває, і повертати його на межі запобіжника не можна. Для випадних списків —
 * `truncatedOption` (`./truncatedOption`).
 */
export function TruncatedListNote({ list }: { readonly list: ReadList | undefined }): JSX.Element | null {
  if (!isTruncated(list)) return null;

  return (
    <Text size="xs" c="dimmed" role="note" data-truncated-list="">
      {t('common.shownSoFar', { shown: formatNumber(list.items.length) })}
    </Text>
  );
}
