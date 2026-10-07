import { Suspense, lazy, useState, type JSX } from 'react';
import { Menu } from '@mantine/core';
import { t } from '@/shared/i18n';
import type { DocumentLogKind } from './DocumentLogDialog';

/**
 * Діалог — окремим чанком (`D-132`): разом із журналом і порівнянням він не
 * потрібен у мить першого малюнка сторінки й не повинен збільшувати її бюджет.
 */
const DocumentLogDialog = lazy(async () => ({
  default: (await import('./DocumentLogDialog')).DocumentLogDialog,
}));

interface DocumentLogActions {
  /** Пункти меню «More»: «History» і «Compare versions». */
  readonly menuItems: readonly JSX.Element[];

  /** Діалог — поза меню: меню розмонтовує вміст, щойно закривається. */
  readonly dialog: JSX.Element | null;
}

/**
 * «History» (журнал переходів аркушів) і «Compare versions» (ФВ-5.22) — пункти
 * меню «More» замість двох великих посилань між шапкою й сіткою (макет
 * `screen-document.js`: у сторінці їх немає).
 */
export function useDocumentLogActions({
  documentId,
  periodKey,
}: {
  readonly documentId: number;
  readonly periodKey: number;
}): DocumentLogActions {
  const [kind, setKind] = useState<DocumentLogKind | null>(null);

  return {
    menuItems: [
      <Menu.Item key="log-history" onClick={() => setKind('history')} data-document-log="history">
        {t('workflow.history')}
      </Menu.Item>,
      <Menu.Item key="log-compare" onClick={() => setKind('compare')} data-document-log="compare">
        {t('document.compare')}
      </Menu.Item>,
    ],
    dialog:
      kind === null ? null : (
        <Suspense fallback={null}>
          <DocumentLogDialog
            kind={kind}
            documentId={documentId}
            periodKey={periodKey}
            onClose={() => setKind(null)}
          />
        </Suspense>
      ),
  };
}
