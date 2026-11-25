import { lazy, Suspense, useState, type JSX } from 'react';
import { Button, Menu, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import type { components } from '@/api/schema';
import { formatPeriodKey } from '@/shared/format';
import { t } from '@/shared/i18n';
import { getDocumentPeriodReopen, periodReopenKey } from './api';

type PeriodState = components['schemas']['PeriodState'];

/** Той самий діалог, що й у місячного документа (окремий чанк, `D-132`). */
const loadDialog = () => import('./DocumentPeriodReopenDialog');
const DocumentPeriodReopenDialog = lazy(async () => ({ default: (await loadDialog()).DocumentPeriodReopenDialog }));

export interface YearMonthReopenProps {
  readonly documentId: number;
  /** Місяці року документа з календаря проєкту (лише цього року, за зростанням). */
  readonly months: readonly { readonly periodKey: number; readonly state?: PeriodState | null | undefined }[];
  readonly timeZoneId?: string | null | undefined;
  /**
   * ✎ b14 doc-notices-compact: місяці, уже відкриті для документа (eopenedPeriods картки) — пункт недоступний: про них
   * каже чип у шапці, а рядок «Reopened for this document until …» поруч із кнопкою дублював його.
   */
  readonly reopened?: readonly number[] | undefined;
}

/**
 * ✎ LAND-YEAR L7a (рішення координатора 17.11): документ «рік» відкриває закритий МІСЯЦЬ для себе з самого документа.
 *
 * L7a веде будь-який `?periodKey` місяця на якір року, тож «Reopen <місяць> for this document…» з банера закритого
 * періоду (`useDocumentPeriodReopen`) лишився лише для якоря. Тут — вибір місяця року: той самий API
 * (`GET/POST …/period-reopen`), те саме право (`Period.Reopen`, вирішує сервер: `canOpen`/`denial`) і той самий діалог
 * причини й строку; від місячного шляху відрізняється лише ціль — місяць із вибору, а не з адреси.
 *
 * ⚠ Відкрити можна лише закритий місяць: інші пункти недоступні. Сторінка показує кнопку лише з правом `Period.Reopen`
 * і лише коли в році є закритий місяць.
 */
export function YearMonthReopen({ documentId, months, timeZoneId, reopened = [] }: YearMonthReopenProps): JSX.Element {
  const [target, setTarget] = useState<number | null>(null);
  const [dialogOpen, setDialogOpen] = useState(false);

  const query = useQuery({
    queryKey: periodReopenKey(documentId, target ?? 0),
    queryFn: () => getDocumentPeriodReopen(documentId, target ?? 0),
    enabled: target !== null,
  });
  const state = target === null ? undefined : query.data;

  // Відповідь прийшла: можна — діалог; уже відкрито або відмова — словами поруч (без діалогу).
  const showDialog = dialogOpen && target !== null && state !== undefined && state.canOpen && state.active === null;

  return (
    <>
      <Menu position="bottom-end" withinPortal>
        <Menu.Target>
          <Button size="xs" variant="default" data-testid="year-month-reopen">
            {t('document.reopen.chooseMonth')}
          </Button>
        </Menu.Target>
        <Menu.Dropdown>
          {months.map((month) => (
            <Menu.Item
              key={month.periodKey}
              disabled={month.state !== 'Closed' || reopened.includes(month.periodKey)}
              data-year-reopen-month={month.periodKey}
              onClick={() => {
                setTarget(month.periodKey);
                setDialogOpen(true);
              }}
            >
              {formatPeriodKey(month.periodKey)}
            </Menu.Item>
          ))}
        </Menu.Dropdown>
      </Menu>

      {/* ✎ b14 doc-notices-compact: «відкрито до …» тут більше не пишемо — це каже чип у шапці (ReopenedMonthsChip). */}

      {target !== null && state !== undefined && state.active === null && !state.canOpen && state.denial === 'NoPermission' && (
        <Text size="sm" data-testid="year-month-reopen-denied">
          {t('document.reopen.noPermission')}
        </Text>
      )}

      {showDialog && target !== null && state !== undefined && (
        <Suspense fallback={null}>
          <DocumentPeriodReopenDialog
            documentId={documentId}
            periodKey={target}
            defaultOpenUntil={state.defaultOpenUntil}
            timeZoneId={timeZoneId}
            onClose={() => setDialogOpen(false)}
          />
        </Suspense>
      )}
    </>
  );
}
