import type { JSX } from 'react';
import { Button, CopyButton, Group } from '@mantine/core';

/**
 * Кнопка «скопіювати кореляцію» — **окремий модуль рівно заради бюджету**
 * (`D-132`: 250 КБ gzip на маршрут).
 *
 * ⛔ `CopyButton` (і `useClipboard` під ним) не використовується в застосунку
 * більше НІДЕ — перевірено `git grep -n "CopyButton" -- src/Ecr.Web/src`.
 * Поставлений статично в `AsyncBoundary`, він лягав у всі 24 маршрутні чанки,
 * бо межа станів стоїть на кожній сторінці: виміряно +0.4 КБ gzip кожному
 * маршруту (`TemplateVersionPage` 250.4 → 250.8). Платили всі; потрібен він
 * рівно тоді, коли (а) запит упав, (б) викликач передав підпис кнопки.
 *
 * ⚠ `export default` — вимога `React.lazy()`, а не стиль файлу.
 */
export default function CorrelationCopy({
  value,
  label,
}: {
  readonly value: string;
  readonly label: string;
}): JSX.Element {
  return (
    <Group gap="xs">
      <CopyButton value={value}>
        {({ copy }) => (
          <Button size="xs" variant="default" onClick={copy}>
            {label}
          </Button>
        )}
      </CopyButton>
    </Group>
  );
}
