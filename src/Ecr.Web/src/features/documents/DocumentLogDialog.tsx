import type { JSX } from 'react';
import { Modal } from '@mantine/core';
import { useReturnFocusOnUnmount } from '@/shared/a11y/focus';
import { DocumentVersionCompare } from '@/features/documents/DocumentVersionCompare';
import { WorkflowHistory } from '@/features/workflow/WorkflowHistory';
import { t } from '@/shared/i18n';

/** Що показує діалог: журнал переходів чи порівняння версій. */
export type DocumentLogKind = 'history' | 'compare';

/**
 * Журнал переходів і порівняння версій документа (`ФВ-5.22`, `BE-11b`) у
 * діалозі з меню «More» (макет `screen-document.js`: у сторінці між шапкою й
 * сіткою таких блоків немає; «History» інспектора — історія клітинки).
 *
 * ⚠ Монтується лише відкритим: до відкриття жодного запиту (`L2`) і жодного
 * байта в чанку сторінки — обидва компоненти приїжджають із цим чанком.
 */
export function DocumentLogDialog({
  kind,
  documentId,
  periodKey,
  onClose,
}: {
  readonly kind: DocumentLogKind;
  readonly documentId: number;
  readonly periodKey: number;
  readonly onClose: () => void;
}): JSX.Element {
  useReturnFocusOnUnmount();

  return (
    <Modal
      opened
      onClose={onClose}
      size="lg"
      title={kind === 'history' ? t('workflow.history') : t('document.compare')}
      data-testid={`document-log-dialog-${kind}`}
    >
      {kind === 'history' ? (
        <WorkflowHistory embedded documentId={documentId} periodKey={periodKey} />
      ) : (
        <DocumentVersionCompare embedded documentId={documentId} periodKey={periodKey} />
      )}
    </Modal>
  );
}
