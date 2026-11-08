import type { JSX, ReactNode } from 'react';
import { Button, Skeleton, Stack, Text, Title } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { apiFetch } from '@/api/client';
import type { DocumentSummary } from '@/api/types';
import { documentState } from '@/features/documents/documentSheets';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { DetailDrawer, useDetailPanel } from '@/shared/ui/DetailDrawer';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { KeyValue } from '@/shared/ui/KeyValue';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';

interface DocumentQuickLookProps {
  /** Значення `?panel=` — ідентифікатор документа. */
  readonly panelId: string;

  /** Рядок уже завантаженої сторінки переліку, якщо документ на ній є. */
  readonly document?: DocumentSummary | undefined;

  /**
   * Чи сторінка переліку вже прочиталась. ⚠ Дочитувати документ окремо можна
   * лише ПІСЛЯ неї: інакше за прямим посиланням `?panel=` кожне відкриття
   * робило б зайвий запит ще до того, як перелік приїхав.
   */
  readonly listReady: boolean;

  readonly projectCode: (projectId: number) => string | undefined;

  readonly documentHref: (documentId: number) => string;

  /** Після закриття (Esc, хрестик, «Close») — щоб сторінка повернула фокус відкривачу. */
  readonly onClose?: (() => void) | undefined;
}

/**
 * Швидкий перегляд документа зі списку (`UI-29`, макет `screens-work.js` →
 * `ctx.panel('*')` на екрані «/», `13-docs-quicklook.png`): хто й коли змінював; дії «Close» і «Open document».
 *
 * ✎ Projects/Documents 08.11: перелік документів — лише за РІК, тож шторка відкривається без місяця. Помісячні блоки
 * (аркуші зі станом і заповненістю, «Can approve now» D-466, «Latest changes») прибрано разом із їхніми запитами
 * (`tables/status`, `workflow/history`): без місяця вони були недосяжні. Стани аркушів за місяць — у самому документі.
 *
 * ~~⚠ «Owner» з макета тут немає: сервер не віддає автора документа, а
 * `modifiedByDisplayName` — це останній, хто змінював. Тому підпис чесний —
 * «Last changed by» (картка `UI-29`).~~
 * ✎ D-450: сервер віддає власника (`ownerDisplayName`) — рядок «Owner» стоїть ПЕРЕД «Last changed by», і це різні
 * люди: власник відповідає за документ, останній редактор — лише останній, хто правив.
 *
 * ⚠ Запит — лише відкритої шторки (компонент монтується за `?panel=`) і лише для документа поза сторінкою переліку.
 */
export function DocumentQuickLook({
  panelId,
  document: fromList,
  listReady,
  projectCode,
  documentHref,
  onClose,
}: DocumentQuickLookProps): JSX.Element | null {
  const documentId = Number(panelId);
  const valid = Number.isInteger(documentId) && documentId > 0;
  const [, setPanel] = useDetailPanel();
  const navigate = useNavigate();

  // Посилання `?panel=` на документ поза завантаженою сторінкою — дочитати його.
  const fetched = useQuery({
    queryKey: ['document', documentId, null, 'quick-look'],
    queryFn: () => apiFetch<DocumentSummary>(`/api/v1/documents/${String(documentId)}`),
    // Рядок уже є на сторінці — шторка вся з нього (картка без місяця нічого понад рядок не несе).
    enabled: valid && listReady && fromList === undefined,
  });

  // ⚠ `?? undefined`: порожня відповідь (`null`) — це «документа немає», а не документ.
  const document = fromList ?? fetched.data ?? undefined;

  if (!valid) return null;

  const name = document === undefined ? '' : localized(document.nameL10n);
  const title = document === undefined ? '' : name.length > 0 ? name : document.businessKey;
  const state = document === undefined ? null : documentState(document);
  const project = document === undefined ? undefined : projectCode(document.projectId);
  const subtitle = document === undefined ? undefined : [name.length > 0 ? document.businessKey : null, project].filter(Boolean).join(' · ');

  return (
    <DetailDrawer
      panelId={panelId}
      title={title}
      subtitle={subtitle}
      badge={state === null ? undefined : <StatusBadge kind="sheet" state={state} />}
      closeLabel={t('documents.quickLookClose')}
      onClose={onClose}
      footer={
        <>
          <Button
            variant="default"
            onClick={() => {
              setPanel(null);
              onClose?.();
            }}
            data-quick-look-close=""
          >
            {t('common.close')}
          </Button>
          {document !== undefined && (
            <Button onClick={() => void navigate(documentHref(document.id))} data-quick-look-open="">
              {t('documents.openDocument')}
            </Button>
          )}
        </>
      }
    >
      {document === undefined ? (
        fetched.error !== null ? (
          <ErrorAlert error={fetched.error} onRetry={() => void fetched.refetch()} />
        ) : (
          <Skeleton height={120} radius="sm" />
        )
      ) : (
        <QuickLookBody document={document} />
      )}
    </DetailDrawer>
  );
}

function QuickLookBody({ document }: { readonly document: DocumentSummary }): JSX.Element {
  return (
    <Stack gap="lg" data-quick-look={document.id}>
      <Text size="sm" c="dimmed" data-quick-look-no-period="">
        {t('documents.quickLookNoPeriod')}
      </Text>

      <Section title={t('documents.quickLookResponsible')}>
        <KeyValue
          wide
          items={[
            { label: t('documents.owner'), value: document.ownerDisplayName ?? null },
            { label: t('documents.lastChangedBy'), value: document.modifiedByDisplayName ?? null },
            {
              label: t('documents.updated'),
              value: document.modifiedAt === null || document.modifiedAt === undefined ? null : <Timestamp value={document.modifiedAt} />,
            },
          ]}
        />
      </Section>
    </Stack>
  );
}

function Section({ title, children }: { readonly title: string; readonly children: ReactNode }): JSX.Element {
  return (
    <Stack gap="xs" component="section">
      <Title order={3} size="sm">
        {title}
      </Title>
      {children}
    </Stack>
  );
}
