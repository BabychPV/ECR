import {
  ActionIcon,
  Alert,
  Button,
  Checkbox,
  Group,
  Modal,
  Stack,
  Switch,
  Table,
  Text,
  Textarea,
  TextInput,
} from '@mantine/core';
import { useMemo, useState, type ClipboardEvent, type JSX } from 'react';
import { t } from '@/shared/i18n';
import {
  cellToText,
  gridFromTsv,
  gridIssues,
  parseCellText,
  parseConstLiteral,
  parseTsv,
  pasteBlock,
  printConstGrid,
  type ConstGrid,
  type ConstParseErrorCode,
  type GridOf,
} from './constTable';

export interface ConstTableDialogProps {
  readonly opened: boolean;
  readonly onClose: () => void;
  /** Літерал мапи (`{ … }`) під курсором. */
  readonly literal: string;
  /** Ім'я константи — лише для заголовка. */
  readonly constName?: string;
  /** «Застосувати»: новий текст літерала в канонічному форматі. */
  readonly onApply: (literal: string) => void;
}

type TextGrid = GridOf<string>;

function toTextGrid(grid: ConstGrid): TextGrid {
  return {
    rowKeys: grid.rowKeys,
    columnKeys: grid.columnKeys,
    cells: grid.cells.map((row) => row.map((cell) => cellToText(cell))),
  };
}

function toConstGrid(grid: TextGrid): ConstGrid {
  return {
    rowKeys: grid.rowKeys,
    columnKeys: grid.columnKeys,
    cells: grid.cells.map((row) => row.map((text) => parseCellText(text))),
  };
}

function parseErrorText(code: ConstParseErrorCode): string {
  switch (code) {
    case 'notMap':
      return t('methodologyScript.constTable.error.notMap');
    case 'mixedLevels':
      return t('methodologyScript.constTable.error.mixedLevels');
    case 'tooDeep':
      return t('methodologyScript.constTable.error.tooDeep');
    case 'duplicateKey':
      return t('methodologyScript.constTable.error.duplicateKey');
    case 'syntax':
      return t('methodologyScript.constTable.error.syntax');
  }
}

function Editor(props: {
  readonly initial: TextGrid;
  readonly onApply: (literal: string) => void;
  readonly onClose: () => void;
}): JSX.Element {
  const [grid, setGrid] = useState<TextGrid>(props.initial);
  const [pasteText, setPasteText] = useState('');
  const [headers, setHeaders] = useState(true);
  const issues = useMemo(() => gridIssues(toConstGrid(grid)), [grid]);

  const keyError = (axis: 'row' | 'column', index: number): string | undefined => {
    const issue = issues.find((i) => i.axis === axis && i.index === index);
    if (issue === undefined) return undefined;
    return issue.kind === 'emptyKey'
      ? t('methodologyScript.constTable.error.emptyKey')
      : t('methodologyScript.constTable.error.duplicateKey');
  };

  const columnCount = grid.columnKeys === null ? 1 : grid.columnKeys.length;

  const setCell = (r: number, c: number, text: string): void => {
    setGrid((g) => ({
      ...g,
      cells: g.cells.map((row, ri) => (ri === r ? row.map((v, ci) => (ci === c ? text : v)) : row)),
    }));
  };
  const setRowKey = (r: number, key: string): void => {
    setGrid((g) => ({ ...g, rowKeys: g.rowKeys.map((k, i) => (i === r ? key : k)) }));
  };
  const setColumnKey = (c: number, key: string): void => {
    setGrid((g) => ({ ...g, columnKeys: g.columnKeys?.map((k, i) => (i === c ? key : k)) ?? null }));
  };
  const addRow = (): void => {
    setGrid((g) => pasteBlock<string>(g, [[]], g.rowKeys.length, 0, ''));
  };
  const addColumn = (): void => {
    setGrid((g) => {
      if (g.columnKeys === null) return g;
      let n = g.columnKeys.length + 1;
      while (g.columnKeys.includes(`Col${String(n)}`)) n++;
      return {
        ...g,
        columnKeys: [...g.columnKeys, `Col${String(n)}`],
        cells: g.cells.map((row) => [...row, '']),
      };
    });
  };
  const removeRow = (r: number): void => {
    setGrid((g) => ({
      ...g,
      rowKeys: g.rowKeys.filter((_, i) => i !== r),
      cells: g.cells.filter((_, i) => i !== r),
    }));
  };
  const removeColumn = (c: number): void => {
    setGrid((g) => ({
      ...g,
      columnKeys: g.columnKeys?.filter((_, i) => i !== c) ?? null,
      cells: g.cells.map((row) => row.filter((_, i) => i !== c)),
    }));
  };
  const setSingleColumn = (single: boolean): void => {
    setGrid((g) =>
      single
        ? { ...g, columnKeys: null, cells: g.cells.map((row) => [row[0] ?? '']) }
        : {
            ...g,
            columnKeys: [t('methodologyScript.constTable.defaultColumn')],
            cells: g.cells.map((row) => [row[0] ?? '']),
          },
    );
  };

  const onCellPaste = (event: ClipboardEvent<HTMLInputElement>, r: number, c: number): void => {
    const text = event.clipboardData.getData('text');
    if (!text.includes('\t') && !/\r?\n./.test(text)) return;
    event.preventDefault();
    const block = parseTsv(text).map((row) => row.map((cell) => cellToText(cell)));
    setGrid((g) => pasteBlock<string>(g, block, r, c, ''));
  };

  const insertTable = (): void => {
    const parsed = gridFromTsv(pasteText, headers);
    if (parsed === null) return;
    setGrid(toTextGrid(parsed));
    setPasteText('');
  };

  const blocked = issues.length > 0;

  return (
    <Stack gap="md">
      <Switch
        checked={grid.columnKeys === null}
        onChange={(event) => {
          setSingleColumn(event.currentTarget.checked);
        }}
        label={t('methodologyScript.constTable.singleColumn')}
      />

      <Table.ScrollContainer minWidth={360}>
        <Table withColumnBorders>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>{t('methodologyScript.constTable.key')}</Table.Th>
              {grid.columnKeys === null ? (
                <Table.Th>{t('methodologyScript.constTable.value')}</Table.Th>
              ) : (
                grid.columnKeys.map((key, c) => (
                  <Table.Th key={c}>
                    <Group gap="xs" wrap="nowrap">
                      <TextInput
                        size="xs"
                        value={key}
                        error={keyError('column', c)}
                        aria-label={t('methodologyScript.constTable.columnKey', { n: c + 1 })}
                        onChange={(event) => {
                          setColumnKey(c, event.currentTarget.value);
                        }}
                      />
                      <ActionIcon
                        variant="subtle"
                        color="statusError"
                        size="sm"
                        aria-label={t('methodologyScript.constTable.removeColumn', { key })}
                        onClick={() => {
                          removeColumn(c);
                        }}
                      >
                        ×
                      </ActionIcon>
                    </Group>
                  </Table.Th>
                ))
              )}
              <Table.Th w={36} />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {grid.rowKeys.map((rowKey, r) => (
              <Table.Tr key={r}>
                <Table.Td>
                  <TextInput
                    size="xs"
                    value={rowKey}
                    error={keyError('row', r)}
                    aria-label={t('methodologyScript.constTable.rowKey', { n: r + 1 })}
                    onChange={(event) => {
                      setRowKey(r, event.currentTarget.value);
                    }}
                  />
                </Table.Td>
                {Array.from({ length: columnCount }, (_, c) => (
                  <Table.Td key={c}>
                    <TextInput
                      size="xs"
                      value={grid.cells[r]?.[c] ?? ''}
                      aria-label={t('methodologyScript.constTable.cell', {
                        row: rowKey,
                        column: grid.columnKeys?.[c] ?? t('methodologyScript.constTable.value'),
                      })}
                      onChange={(event) => {
                        setCell(r, c, event.currentTarget.value);
                      }}
                      onPaste={(event) => {
                        onCellPaste(event, r, c);
                      }}
                    />
                  </Table.Td>
                ))}
                <Table.Td>
                  <ActionIcon
                    variant="subtle"
                    color="statusError"
                    size="sm"
                    aria-label={t('methodologyScript.constTable.removeRow', { key: rowKey })}
                    onClick={() => {
                      removeRow(r);
                    }}
                  >
                    ×
                  </ActionIcon>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>

      <Group gap="xs">
        <Button size="xs" variant="light" onClick={addRow}>
          {t('methodologyScript.constTable.addRow')}
        </Button>
        {grid.columnKeys !== null && (
          <Button size="xs" variant="light" onClick={addColumn}>
            {t('methodologyScript.constTable.addColumn')}
          </Button>
        )}
      </Group>

      <Stack gap="xs">
        <Textarea
          label={t('methodologyScript.constTable.pasteLabel')}
          description={t('methodologyScript.constTable.pasteHint')}
          value={pasteText}
          minRows={3}
          autosize
          onChange={(event) => {
            setPasteText(event.currentTarget.value);
          }}
        />
        <Group gap="md">
          <Checkbox
            size="xs"
            checked={headers}
            onChange={(event) => {
              setHeaders(event.currentTarget.checked);
            }}
            label={t('methodologyScript.constTable.firstRowHeaders')}
          />
          <Button
            size="xs"
            variant="default"
            disabled={pasteText.trim() === ''}
            onClick={insertTable}
          >
            {t('methodologyScript.constTable.replaceFromPaste')}
          </Button>
        </Group>
      </Stack>

      {blocked && (
        <Text size="sm" c="statusError">
          {t('methodologyScript.constTable.fixKeys')}
        </Text>
      )}

      <Group justify="flex-end">
        <Button variant="default" onClick={props.onClose}>
          {t('methodologyScript.constTable.cancel')}
        </Button>
        <Button
          disabled={blocked}
          onClick={() => {
            props.onApply(printConstGrid(toConstGrid(grid)));
          }}
        >
          {t('methodologyScript.constTable.apply')}
        </Button>
      </Group>
    </Stack>
  );
}

/** Редагування `const`-мапи сіткою (план 13.5). */
export function ConstTableDialog(props: ConstTableDialogProps): JSX.Element {
  const parsed = useMemo(
    () => (props.opened ? parseConstLiteral(props.literal) : null),
    [props.opened, props.literal],
  );

  return (
    <Modal
      opened={props.opened}
      onClose={props.onClose}
      size="xl"
      title={
        props.constName === undefined
          ? t('methodologyScript.constTable.title')
          : t('methodologyScript.constTable.titleNamed', { name: props.constName })
      }
    >
      {parsed === null ? null : parsed.ok ? (
        <Editor initial={toTextGrid(parsed.grid)} onApply={props.onApply} onClose={props.onClose} />
      ) : (
        <Stack gap="md">
          <Alert color="statusError" role="alert">
            {parseErrorText(parsed.code)}
          </Alert>
          <Group justify="flex-end">
            <Button variant="default" onClick={props.onClose}>
              {t('methodologyScript.constTable.cancel')}
            </Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
