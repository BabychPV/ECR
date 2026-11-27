import type { JSX } from 'react';
import { Button } from '@mantine/core';
import { Link } from 'react-router-dom';
import { t } from '@/shared/i18n';

/**
 * ✎ DOCUX (огляд UI 27.11): порожній стан «No collection sources configured» на Mapping preview / Data pipeline / PI data —
 * з переходом туди, де джерела заводять (а не з порожнім вибором сутності й другою підказкою «Pick a source entity…»).
 */
/** `href` — адреса екрана Sources з маршрутів сторінки (`features` не імпортує `app`). */
export function SourcesSetupLink({ href }: { readonly href: string }): JSX.Element {
  return (
    <Button component={Link} to={href} variant="default" data-sources-setup-link="">
      {t('sources.goToSources')}
    </Button>
  );
}

/** Чи перелік сутностей уже прийшов і порожній: тоді вибору й підказки «оберіть сутність» немає. */
export function noSourcesYet(sources: readonly unknown[] | undefined): boolean {
  return sources !== undefined && sources.length === 0;
}
