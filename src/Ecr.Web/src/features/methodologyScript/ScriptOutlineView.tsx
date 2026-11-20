import { Badge, Group, List, Stack, Text, Title } from '@mantine/core';
import type { JSX } from 'react';
import { t } from '@/shared/i18n';
import type { ScriptOutlineDto } from './types';

export interface ScriptOutlineViewProps {
  readonly outline: ScriptOutlineDto;
  /** Коди виходів, для яких немає колонки-прив'язки. */
  readonly outputsWithoutColumn?: ReadonlySet<string>;
}

function Section(props: {
  readonly title: string;
  readonly empty: string;
  readonly children: readonly JSX.Element[];
}): JSX.Element {
  return (
    <Stack gap="xs">
      <Title order={5}>{props.title}</Title>
      {props.children.length === 0 ? (
        <Text size="sm" c="dimmed">
          {props.empty}
        </Text>
      ) : (
        <List listStyleType="none" spacing={2}>
          {props.children}
        </List>
      )}
    </Stack>
  );
}

/** «Що читає / що пише»: входи, виходи, таблиці, імпорти. */
export function ScriptOutlineView({
  outline,
  outputsWithoutColumn,
}: ScriptOutlineViewProps): JSX.Element {
  return (
    <Stack gap="md" data-testid="script-outline">
      <Section
        title={t('methodologyScript.outline.inputs')}
        empty={t('methodologyScript.outline.noneItems')}
      >
        {outline.inputs.map((input) => (
          <List.Item key={input.name}>
            <Group gap="xs">
              <Text size="sm" ff="monospace">
                {input.name}
              </Text>
              {input.optional && (
                <Badge size="xs" variant="light" color="gray">
                  {t('methodologyScript.outline.optional')}
                </Badge>
              )}
            </Group>
          </List.Item>
        ))}
      </Section>

      <Section
        title={t('methodologyScript.outline.outputs')}
        empty={t('methodologyScript.outline.noneItems')}
      >
        {outline.outputs.map((output) => (
          <List.Item key={output.code}>
            <Group gap="xs">
              <Text size="sm" ff="monospace">
                {output.code}
              </Text>
              <Text size="xs" c="dimmed">
                {output.unit}
              </Text>
              {output.perSubstance && (
                <Badge size="xs" variant="light">
                  {t('methodologyScript.outline.perSubstance')}
                </Badge>
              )}
              {outputsWithoutColumn?.has(output.code) === true && (
                <Badge size="xs" color="statusWarning" variant="light">
                  {t('methodologyScript.outline.noColumn')}
                </Badge>
              )}
            </Group>
          </List.Item>
        ))}
      </Section>

      <Section
        title={t('methodologyScript.outline.tables')}
        empty={t('methodologyScript.outline.noneItems')}
      >
        {outline.tables.map((table) => (
          <List.Item key={`${table.alias ?? ''}:${table.code}`}>
            <Group gap="xs">
              <Text size="sm" ff="monospace">
                {table.code}
              </Text>
              {table.alias !== undefined && table.alias !== null && table.alias !== '' && (
                <Badge size="xs" variant="outline">
                  {table.alias}
                </Badge>
              )}
            </Group>
          </List.Item>
        ))}
      </Section>

      <Section
        title={t('methodologyScript.outline.imports')}
        empty={t('methodologyScript.outline.noneItems')}
      >
        {outline.imports.map((name) => (
          <List.Item key={name}>
            <Text size="sm" ff="monospace">
              {name}
            </Text>
          </List.Item>
        ))}
      </Section>
    </Stack>
  );
}
