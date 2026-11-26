import type { JSX } from 'react';
import { Anchor, Group } from '@mantine/core';
import { Link } from 'react-router-dom';
import { t } from '@/shared/i18n';
import { consistencyFindingParams } from './consistencyFindingText';

/** Посилання з знахідки на її місце. */
export interface FindingLink {
  /** Куди. */
  readonly to: string;

  /** Підпис мовою інтерфейсу. */
  readonly label: string;
}

/** Ціле додатне число з підстановки, інакше `null` (`—`, порожньо, сміття). */
function positive(value: string | undefined): number | null {
  if (value === undefined || !/^\d+$/.test(value)) return null;
  const number = Number(value);
  return number > 0 ? number : null;
}

/**
 * Посилання знахідки: документ за період і методологія — з підстановок конверта (`documentId` + `period`/`periodKey`,
 * `methodologyId`). Знахідка без них (структура шаблону, архів) — без посилань.
 *
 * ⚠ Права тут не перевіряються: сторінка документа й методології самі відмовлять тому, хто їх не бачить; журнал
 * лише називає місце, яке вже стоїть у тексті знахідки.
 */
export function findingLinks(message: string): readonly FindingLink[] {
  const params = consistencyFindingParams(message);
  if (params === null) return [];

  const links: FindingLink[] = [];
  const documentId = positive(params.documentId);
  const period = positive(params.period ?? params.periodKey);
  if (documentId !== null && period !== null) {
    links.push({
      to: `/documents/${String(documentId)}?periodKey=${String(period)}`,
      label: t('consistency.openDocument', { documentId: String(documentId), period: String(period) }),
    });
  }

  const methodologyId = positive(params.methodologyId);
  if (methodologyId !== null) {
    links.push({
      to: `/admin/methodologies/${String(methodologyId)}/versions`,
      label: t('consistency.openMethodology', { methodology: params.methodology ?? String(methodologyId) }),
    });
  }

  return links;
}

/** Посилання знахідки рядком; клік не відкриває шторку рядка. */
export function FindingLinks({ message }: { readonly message: string }): JSX.Element | null {
  const links = findingLinks(message);
  if (links.length === 0) return null;

  return (
    <Group gap="sm" data-finding-links>
      {links.map((link) => (
        <Anchor key={link.to} component={Link} to={link.to} size="xs" onClick={(event) => event.stopPropagation()}>
          {link.label}
        </Anchor>
      ))}
    </Group>
  );
}
