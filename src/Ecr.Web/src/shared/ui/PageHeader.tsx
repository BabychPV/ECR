import { useEffect, useRef, type JSX, type ReactNode } from 'react';
import { Group, Title } from '@mantine/core';
import { announceRoute } from './RouteAnnouncer';

/**
 * Заголовок сторінки з місцем для дій праворуч.
 *
 * ⛔ Заголовок ще й **приймає фокус** при відкритті екрана (`ФВ-14.19`).
 * Користувач клавіатури після переходу опиняється «ніде»: фокус лишається на
 * пункті меню попереднього екрана, і `Tab` веде його по навігації заново —
 * тобто кожен перехід коштує йому десятка натискань.
 */
export function PageHeader({
  title,
  actions,
}: {
  title: string;
  actions?: ReactNode;
}): JSX.Element {
  const heading = useRef<HTMLHeadingElement>(null);
  const focused = useRef(false);

  useEffect(() => {
    // ⛔ Рівно ОДИН раз за монтування. Заголовок сторінки документа
    // уточнюється після завантаження даних, і фокус за кожною зміною назви
    // висмикував би курсор із поля, у якому користувач уже пише.
    if (focused.current) return;

    focused.current = true;
    heading.current?.focus();
  }, []);

  useEffect(() => {
    announceRoute(title);
  }, [title]);

  return (
    <Group justify="space-between" mb="md">
      {/*
       * ⚠ `tabIndex={-1}`: заголовок приймає фокус програмно, але НЕ стає
       * зупинкою при обході табом. Інакше кожен екран додавав би користувачеві
       * зайве натискання на шляху до першого поля.
       */}
      <Title order={3} ref={heading} tabIndex={-1}>
        {title}
      </Title>
      {actions}
    </Group>
  );
}
