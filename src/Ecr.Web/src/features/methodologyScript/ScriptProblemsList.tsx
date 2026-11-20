import { Alert, Anchor, Group, List, Text } from '@mantine/core';
import type { JSX } from 'react';
import { t } from '@/shared/i18n';
import { localizedScriptMessage } from './scriptMarkers';
import type { ScriptDiagnosticDto } from './types';

export interface ScriptProblemsListProps {
  readonly diagnostics: readonly ScriptDiagnosticDto[];
  /** Клік по проблемі: перейти до позиції в редакторі (1-базові рядок і колонка). */
  readonly onJump: (line: number, column: number) => void;
}

/** Перелік діагностик скрипта; клік веде до рядка. */
export function ScriptProblemsList({ diagnostics, onJump }: ScriptProblemsListProps): JSX.Element {
  if (diagnostics.length === 0) {
    return <Text c="dimmed">{t('methodologyScript.problems.none')}</Text>;
  }

  return (
    <List
      spacing="xs"
      listStyleType="none"
      aria-label={t('methodologyScript.problems.title')}
      data-testid="script-problems"
    >
      {diagnostics.map((d, index) => (
        <List.Item key={`${String(index)}-${d.code}-${String(d.line)}-${String(d.column)}`}>
          <Alert color="statusError" variant="light" p="xs">
            <Group gap="xs" wrap="nowrap" align="flex-start">
              <Anchor
                component="button"
                type="button"
                size="sm"
                onClick={() => {
                  onJump(d.line, d.column);
                }}
                aria-label={t('methodologyScript.problems.jump', {
                  line: d.line,
                  column: d.column,
                })}
              >
                {d.line}:{d.column}
              </Anchor>
              <Text size="sm">{localizedScriptMessage(d)}</Text>
              <Text size="xs" c="dimmed" ml="auto">
                {d.code}
              </Text>
            </Group>
          </Alert>
        </List.Item>
      ))}
    </List>
  );
}
