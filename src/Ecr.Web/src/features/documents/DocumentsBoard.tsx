import type { JSX } from 'react';
import { ActionIcon, Text } from '@mantine/core';
import { Link, useNavigate } from 'react-router-dom';
import type { DocumentSummary } from '@/api/types';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { SegmentBar } from '@/shared/ui/SegmentBar';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { Timestamp } from '@/shared/ui/Timestamp';
import { EyeIcon, IssueCount } from './DocumentListMarks';
import { documentState, hasSheetStates, sheetLabels } from './documentSheets';
import { boardColumns, type BoardColumnId } from './documentsBoardModel';
import { LateEditsMark } from './LateEditsMark';
import { StaleResultsMark } from './StaleResultsMark';
import './documentsBoard.css';

interface DocumentsBoardProps {
  /** Документи ПОТОЧНОЇ сторінки переліку — ті самі, що в таблиці, з тими самими фільтрами. */
  readonly documents: readonly DocumentSummary[];
  readonly documentHref: (documentId: number) => string;
  /** Код проєкту; `undefined` — перелік проєктів ще не прочитано (тоді код не показується). */
  readonly projectCode: (projectId: number) => string | undefined;
  /** «Око» — швидкий перегляд у шторці (`UI-29`); кнопку-відкривач сторінка запам'ятовує сама. */
  readonly onQuickLook: (documentId: number, opener: HTMLButtonElement) => void;
}

/**
 * Подання «Board» переліку документів (`UI-40`).
 *
 * Макет — `docs/design/hybrid/screens-work.js`, екран «/» (`paintBoard`, `.work-board`,
 * `.work-col`, `.work-bcard`), знімок `ui-adoption/mockup/12-docs-board.png`: чотири стовпці
 * Draft · Waiting for approval · Returned or rejected · Approved, у заголовку — число; картка —
 * ключ, назва, смужка аркушів, «хто · коли», бейдж (лише у «Returned or rejected»), «late» і
 * число помилок; «око» — швидкий перегляд.
 *
 * ⚠ Без перетягування: макет його не малює, а стан аркуша міняють лише дії робочого процесу.
 * ⚠ Стовпець — `<section>` зі списком `<ul>`: читалка чує «список, N елементів» (a11y з задачі).
 * ⛔ P1 приховані аркуші: усе на картці — лише з відповіді сервера; `null` помилок → «—».
 */
export function DocumentsBoard({ documents, documentHref, projectCode, onQuickLook }: DocumentsBoardProps): JSX.Element {
  const navigate = useNavigate();
  const columns = boardColumns(documents);

  return (
    <div className="ecr-board" data-documents-board="">
      {columns.map((column) => (
        <section
          key={column.id}
          className="ecr-board-col"
          aria-labelledby={`ecr-board-h-${column.id}`}
          data-board-column={column.id}
        >
          <div className="ecr-board-col-h">
            <h2 id={`ecr-board-h-${column.id}`} className="ecr-board-col-title">
              {columnLabel(column.id)}
            </h2>
            <span
              className="ecr-board-count"
              data-tone={column.attention && column.items.length > 0 ? 'warn' : undefined}
              data-board-count={column.items.length}
            >
              {column.items.length}
            </span>
          </div>

          {column.items.length === 0 ? (
            <p className="ecr-board-col-empty">{t('documents.board.nothingHere')}</p>
          ) : (
            <ul className="ecr-board-list">
              {column.items.map((document) => {
                const name = localized(document.nameL10n);
                const code = projectCode(document.projectId);
                const state = documentState(document);
                const errors = document.errorCount;

                return (
                  <li
                    key={document.id}
                    className="ecr-board-card"
                    data-board-card={document.businessKey}
                    // Клік по картці (поза посиланням і кнопкою) — те саме, що посилання: макет `card.onclick`.
                    onClick={(event) => {
                      if ((event.target as HTMLElement).closest('a,button') !== null) return;
                      navigate(documentHref(document.id));
                    }}
                  >
                    <div className="ecr-board-card-top">
                      <Link to={documentHref(document.id)} className="ecr-board-key">
                        {document.businessKey}
                      </Link>
                      <ActionIcon
                        size="sm"
                        variant="subtle"
                        color="gray"
                        aria-label={t('documents.quickLook', { key: document.businessKey })}
                        data-quick-look={document.id}
                        onClick={(event) => onQuickLook(document.id, event.currentTarget)}
                      >
                        <EyeIcon />
                      </ActionIcon>
                    </div>

                    <div className="ecr-board-name" title={name.length > 0 ? name : undefined}>
                      {name.length > 0 ? name : document.businessKey}
                      {code !== undefined && <span className="ecr-board-project"> · {code}</span>}
                    </div>

                    {hasSheetStates(document) ? (
                      <SegmentBar segments={sheetLabels(document)} />
                    ) : (
                      <Text size="xs" c="dimmed">
                        —
                      </Text>
                    )}

                    <div className="ecr-board-meta">
                      <span className="ecr-board-who">
                        {typeof document.modifiedByDisplayName === 'string' && (
                          <>{document.modifiedByDisplayName} · </>
                        )}
                        <Timestamp value={document.modifiedAt} dateOnly />
                      </span>
                      <span className="ecr-board-marks">
                        {(column.id === 'Rework' || column.id === 'NoState') && state !== null && (
                          <StatusBadge kind="sheet" state={state} quiet />
                        )}
                        {document.hasLateEdits && <LateEditsMark />}
                        {document.resultsStale === true && <StaleResultsMark since={document.resultsStaleSince} />}
                        {errors === null || errors === undefined ? (
                          <span className="ecr-board-noissues" title={t('documents.issues')}>
                            —
                          </span>
                        ) : errors > 0 ? (
                          <IssueCount count={errors} />
                        ) : null}
                      </span>
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
        </section>
      ))}
    </div>
  );
}

/*
 * ⚠ Кожен підпис — окремим викликом із ЛІТЕРАЛОМ: сторож каталогу розбирає ключі лише так
 * (як у `DocumentListFilterBar`).
 */
function columnLabel(id: BoardColumnId): string {
  switch (id) {
    case 'Draft':
      return t('documents.board.draft');
    case 'Submitted':
      return t('documents.board.waiting');
    case 'Rework':
      return t('documents.board.rework');
    case 'Approved':
      return t('documents.board.approved');
    case 'NoState':
      return t('documents.board.noState');
  }
}
