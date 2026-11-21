import { lazy, Suspense, useCallback, useEffect, useMemo, useReducer, useRef, useState, type JSX } from 'react';
import { Alert, Badge, Box, Button, Group, Stack, Tabs, Text, Tooltip } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import { queryKeys } from '@/api/queryKeys';
import { scriptKeys } from '@/features/methodologyScript/scriptQueryKeys';
import { usePendingLoading } from '@/features/common/usePendingLoading';
import {
  methodologyVersions,
  methodologyTestCases,
} from '@/features/methodologies/api';
import { mayEditContent } from '@/features/methodologies/draft';
import { usePublishVersion } from '@/features/methodologies/PublishVersionFlow';
import {
  methodologyScript,
  methodologyScriptSubstances,
  saveMethodologyScript,
} from '@/features/methodologyScript/api';
import { findConstAt } from '@/features/methodologyScript/constTable';
import {
  ScriptEditor,
  type ScriptCompletionBase,
  type ScriptEditorHandle,
} from '@/features/methodologyScript/ScriptEditor';
import { ScriptProblemsList } from '@/features/methodologyScript/ScriptProblemsList';
import {
  canSaveScript,
  EmptyScriptEditorState,
  isScriptChangedError,
  isScriptDirty,
  liveDiagnostics,
  replaceTextRange,
  saveScriptBody,
  scriptEditorReducer,
} from '@/features/methodologyScript/scriptEditorState';
import type { ScriptTestEditing } from '@/features/methodologyScript/ScriptTestsPanel';
import { suggestTestCode } from '@/features/methodologyScript/testForm';
import type { TestCaseForm } from '@/features/methodologyScript/testCases';
import type {
  MethodologyScriptDto,
  ScriptDiagnosticDto,
  ScriptOutlineDto,
} from '@/features/methodologyScript/types';
import { useScriptCompletionData } from '@/features/methodologyScript/useScriptCompletionData';
import { useScriptValidation } from '@/features/methodologyScript/useScriptValidation';
import { t } from '@/shared/i18n';
import { localized } from '@/shared/i18n/localized';
import { can, useSession } from '@/shared/session/useSession';
import { ErrorAlert } from '@/shared/ui/ErrorAlert';
import { PageHeader } from '@/shared/ui/PageHeader';
import { StatusBadge } from '@/shared/ui/StatusBadge';
import { showApiError, showDone, showWarning } from '@/shared/ui/notify';
import { registerUnsavedSource } from '@/shared/ui/unsavedSources';
import { apiFetch } from '@/api/client';
import type { MethodologyDto } from '@/api/types';

/** Панель «Обов'язкові колонки» — наявний компонент у власному чанку (`D-132`). */
const MethodologyRequiredInputsPanel = lazy(async () => {
  const loaded = await import('@/features/methodologies/MethodologyContentPanels');

  return { default: loaded.MethodologyRequiredInputsPanel };
});

/*
 * ⛔ Усе, що не потрібне для першого кадру, — за `import()`: бюджет маршруту 250 КБ gzip (`D-132`). Редактор, шапка,
 * збереження й перевірка — у чанку сторінки; перегляд (вибір документа, період, таблиця), форма тестів, діалоги
 * «таблиця коефіцієнтів» і «порівняння» вантажаться паралельно з даними (вкладки) або за дією (діалоги).
 */
const ScriptPreviewPanel = lazy(async () => ({
  default: (await import('@/features/methodologyScript/ScriptPreviewPanel')).ScriptPreviewPanel,
}));
const ScriptTestsPanel = lazy(async () => ({
  default: (await import('@/features/methodologyScript/ScriptTestsPanel')).ScriptTestsPanel,
}));
const ScriptOutlinePanel = lazy(async () => ({
  default: (await import('@/features/methodologyScript/ScriptOutlinePanel')).ScriptOutlinePanel,
}));
const ConstTableDialog = lazy(async () => ({
  default: (await import('@/features/methodologyScript/ConstTableDialog')).ConstTableDialog,
}));
const ScriptCompareModal = lazy(async () => ({
  default: (await import('@/features/methodologyScript/ScriptCompareModal')).ScriptCompareModal,
}));

type TabId = 'preview' | 'io' | 'tests' | 'required' | 'problems';

const ValidationDelayMs = 500;

/**
 * Редактор скрипта методології (`METHODOLOGY-SCRIPT-PLAN.md`, розд. 6).
 *
 * ⛔ Текст лежить у редакторі, а не в запиті: збереження, перевірка й перегляд
 * працюють із ПОТОЧНИМ текстом (збереження — з `expectedHash`, щоб не затерти
 * правку з іншої вкладки). Опублікована чи застаріла версія відкривається лише
 * на читання; вкладки справа працюють і для неї (перегляд — зі збереженим
 * текстом).
 *
 * ⚠ Заборону правки опублікованої версії тримає сервер (`409 scriptNotEditable`);
 * тут лише немає кнопки, яка не спрацює.
 */
export function MethodologyScriptPage(): JSX.Element {
  const params = useParams();
  const methodologyId = Number(params['id']);
  const versionId = Number(params['vid']);
  const known = Number.isFinite(methodologyId) && Number.isFinite(versionId);

  const queryClient = useQueryClient();
  const session = useSession();
  const mayEdit = can(session.data, 'Calculation.EditFormula');
  const mayPublish = can(session.data, 'Calculation.Publish');
  const mayManageRequiredInputs = can(session.data, 'Calculation.ManageRequiredInputs');

  const [state, dispatch] = useReducer(scriptEditorReducer, EmptyScriptEditorState);
  const [loaded, setLoaded] = useState(false);
  const [tab, setTab] = useState<TabId>('preview');
  const [cursor, setCursor] = useState(0);
  const [comparing, setComparing] = useState(false);
  const [constRange, setConstRange] = useState<{ start: number; end: number; name: string } | null>(
    null,
  );
  const [testEditing, setTestEditing] = useState<ScriptTestEditing | null>(null);
  const [previewPeriod, setPreviewPeriod] = useState<number | null>(null);
  const editor = useRef<ScriptEditorHandle | null>(null);

  // ── Дані ──────────────────────────────────────────────────────────────────
  const versions = useQuery({
    queryKey: queryKeys.methodologies.versionsOf(methodologyId),
    queryFn: () => methodologyVersions(methodologyId),
    enabled: known,
  });
  const version = versions.data?.find((item) => item.id === versionId);

  const methodologies = useQuery({
    queryKey: queryKeys.methodologies.list(),
    queryFn: () => apiFetch<MethodologyDto[]>('/api/v1/methodologies'),
    enabled: known,
  });
  const methodology = methodologies.data?.find((item) => item.id === methodologyId);

  const script = useQuery({
    queryKey: scriptKeys.script(versionId),
    queryFn: () => methodologyScript(methodologyId, versionId),
    enabled: known,
    refetchOnWindowFocus: false,
  });

  // ⚠ Текст з сервера переходить у редактор ОДИН раз (і після явного «Перезавантажити»):
  // подальші оновлення кешу (збереження) не повинні затирати те, що людина набирає.
  const applied = useRef<MethodologyScriptDto | null>(null);
  const applyServerScript = useCallback((data: MethodologyScriptDto): void => {
    applied.current = data;
    dispatch({ type: 'loaded', text: data.text, hash: data.hash });
    setLoaded(true);
  }, []);
  useEffect(() => {
    if (script.data !== undefined && applied.current === null) {
      applyServerScript(script.data);
    }
  }, [script.data, applyServerScript]);

  const tests = useQuery({
    queryKey: queryKeys.methodologies.tests(versionId),
    queryFn: () => methodologyTestCases(methodologyId, versionId),
    enabled: known,
  });
  const substances = useQuery({
    queryKey: scriptKeys.substances(versionId),
    queryFn: () => methodologyScriptSubstances(methodologyId, versionId),
    enabled: known,
  });
  const substanceEntryIds = useMemo(() => {
    const map: Record<string, number> = {};
    for (const item of substances.data?.items ?? []) {
      if (!item.isDeleted) map[item.code] = item.substanceEntryId;
    }
    return map;
  }, [substances.data]);

  // ── Права й режим ─────────────────────────────────────────────────────────
  const isScript = (script.data?.engine ?? version?.engine) === 'Script';
  const readOnly =
    !mayEditContent(version, mayEdit) || script.data?.isDraft === false || !isScript;
  const requiredInputsEditable = mayEditContent(version, mayManageRequiredInputs);

  // Версія рівня Module (HSE400): сервер на перегляд відповідає 422 `scriptPreviewModuleLevel` (набір аргументів складає
  // модуль, а не рядки скрипта) — вкладку вимкнено з поясненням, відкрита за замовчуванням — «Входи й виходи».
  const previewUnavailable = version?.level === 'Module';
  useEffect(() => {
    if (previewUnavailable) setTab((current) => (current === 'preview' ? 'io' : current));
  }, [previewUnavailable]);

  // ── Перевірка живого тексту ───────────────────────────────────────────────
  const [debouncedText] = useDebouncedValue(state.text, ValidationDelayMs);
  // PSS-21: `429` перевірки — не тиша, а пауза з повтором (рядок стану нижче).
  const { validation, pausedSeconds } = useScriptValidation(
    methodologyId,
    versionId,
    debouncedText,
    known && loaded && isScript && debouncedText.trim() !== '',
  );

  const diagnostics: readonly ScriptDiagnosticDto[] = liveDiagnostics({
    text: state.text,
    savedText: state.savedText,
    validated: validation.data?.diagnostics,
    saved: script.data?.diagnostics,
  });
  const outline: ScriptOutlineDto | null = validation.data?.outline ?? script.data?.outline ?? null;
  const checking =
    loaded && pausedSeconds === null && (state.text !== debouncedText || validation.isFetching);

  // ── Підказки ──────────────────────────────────────────────────────────────
  const completionBase = useScriptCompletionData(outline, versionId);
  const completionRef = useRef<ScriptCompletionBase>(completionBase);
  completionRef.current = completionBase;

  // ── Збереження ────────────────────────────────────────────────────────────
  const stateRef = useRef(state);
  stateRef.current = state;

  const save = useMutation({
    mutationFn: (submitted: string) =>
      saveMethodologyScript(methodologyId, versionId, {
        ...saveScriptBody({ ...stateRef.current, text: submitted }),
      }),
    onSuccess: (saved, submitted) => {
      applied.current = saved;
      dispatch({ type: 'saved', submitted, hash: saved.hash });
      queryClient.setQueryData(scriptKeys.script(versionId), saved);
      // Збереження матеріалізує виходи: покриття й перелік тестів можуть змінитися.
      void queryClient.invalidateQueries({ queryKey: scriptKeys.coverage(methodologyId, versionId) });
      if (saved.diagnostics.length > 0) {
        showWarning(t('methodologyScript.savedWithProblems', { count: saved.diagnostics.length }));
      } else {
        showDone(t('methodologyScript.saved'));
      }
    },
    onError: (error) => {
      if (isScriptChangedError(error)) {
        dispatch({ type: 'conflict' });
        return;
      }
      showApiError(error);
    },
  });
  const saveLoading = usePendingLoading(save.isPending);

  const saveNow = (): void => {
    if (save.isPending || !canSaveScript(stateRef.current, readOnly)) return;
    save.mutate(stateRef.current.text);
  };
  const saveNowRef = useRef(saveNow);
  saveNowRef.current = saveNow;

  const dirty = isScriptDirty(state);

  // ⛔ Захист від втрати правок: `UnsavedGuard` (навігація в застосунку) + `beforeunload`
  // (закриття вкладки). `flush` — зберегти й дозволити перехід; не вдалося (конфлікт, відмова) — лишитись.
  useEffect(() => {
    if (!dirty || readOnly) return undefined;

    const unregister = registerUnsavedSource('methodology-script', {
      hasUnsaved: () => true,
      unsavedCount: () => 1,
      flush: async () => {
        const current = stateRef.current;
        if (!canSaveScript(current, readOnly)) return false;
        try {
          await save.mutateAsync(current.text);
          return true;
        } catch {
          return false;
        }
      },
    });
    const warn = (event: BeforeUnloadEvent): void => {
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);

    return () => {
      unregister();
      window.removeEventListener('beforeunload', warn);
    };
  }, [dirty, readOnly, save]);

  // «Перезавантажити» застосовує відповідь напряму: за ідентичних даних кеш не змінюється, ефект не спрацював би, і
  // набраний текст лишився б замість серверного. Відмова мережі правок не чіпає.
  const reload = (): void => {
    void script.refetch().then((result) => {
      if (!result.isError && result.data !== undefined) applyServerScript(result.data);
    });
  };

  // ── Публікація ────────────────────────────────────────────────────────────
  const publishFlow = usePublishVersion(methodologyId, () => {
    void queryClient.invalidateQueries({ queryKey: scriptKeys.script(versionId) });
    reload();
  });
  const publishVisible = version !== undefined && version.status !== 'Published' && mayPublish && isScript;

  // ── «Редагувати як таблицю» ───────────────────────────────────────────────
  const constAtCursor = useMemo(() => findConstAt(state.text, cursor), [state.text, cursor]);
  const openConstTable = (): void => {
    const range = findConstAt(stateRef.current.text, editor.current?.cursorOffset() ?? cursor);
    if (range === null) return;
    setConstRange({ start: range.start, end: range.end, name: range.name });
  };
  const constLiteral =
    constRange === null ? '' : state.text.slice(constRange.start, constRange.end);

  const applyConst = (literal: string): void => {
    const range = constRange;
    if (range === null) return;

    // ⛔ Літерал уже в каноні клієнта (`printConstGrid`) і несе коментарі та вихідний текст чисел; серверний
    // `format-const` друкує зі значень — прибрав би коментарі й змінив би `1.0`/довгі `decimal`.
    const canonical = literal;

    if (editor.current === null) {
      dispatch({
        type: 'edited',
        text: replaceTextRange(stateRef.current.text, range.start, range.end, canonical),
      });
    } else {
      editor.current.replaceRange(range.start, range.end, canonical);
    }
    setConstRange(null);
  };

  // ── Тест із рядка перегляду ───────────────────────────────────────────────
  const createTestFromRow = (form: TestCaseForm): void => {
    setTestEditing({
      code: suggestTestCode((tests.data ?? []).map((test) => test.code)),
      isNew: true,
      form,
    });
    setTab('tests');
  };

  // ── Шапка ─────────────────────────────────────────────────────────────────
  const methodologyName = methodology === undefined ? '' : localized(methodology.nameL10n);
  const title =
    methodology === undefined
      ? t('methodologyScript.title')
      : `${methodologyName === '' ? methodology.code : methodologyName}${
          version === undefined ? '' : ` · ${version.versionNumber}`
        }`;
  const versionsHref = `/admin/methodologies/${String(methodologyId)}/versions`;

  const problemCount = diagnostics.length;

  if (!known) {
    return <Alert color="statusError">{t('nav.notFound.title')}</Alert>;
  }

  return (
    <Stack gap="sm">
      <PageHeader
        title={title}
        description={
          methodology === undefined || version === undefined
            ? null
            : `${methodology.code} · ${t('methodologyScript.engine')}: ${t(
                `methodologyScript.engine.${script.data?.engine ?? version.engine ?? 'Script'}`,
              )}`
        }
        back={{ label: t('methodologies.versions'), href: versionsHref }}
        badge={
          version === undefined ? undefined : <StatusBadge kind="version" state={version.status} />
        }
        actions={
          <Group gap="xs">
            {!readOnly && (
              <Button
                loading={saveLoading}
                disabled={!canSaveScript(state, readOnly)}
                title={t('methodologyScript.saveHint')}
                onClick={saveNow}
              >
                {t('methodologies.save')}
              </Button>
            )}

            {publishVisible && (
              <Button
                variant="default"
                disabled={dirty || state.conflict}
                title={dirty ? t('methodologyScript.publishSaveFirst') : undefined}
                onClick={() => publishFlow.open(versionId)}
              >
                {t('methodologies.publish')}
              </Button>
            )}

            <Button
              variant="default"
              disabled={!loaded || (versions.data?.length ?? 0) < 2}
              onClick={() => setComparing(true)}
            >
              {t('methodologyScript.compare')}
            </Button>

            {!readOnly && (
              <Button
                variant="default"
                disabled={!loaded}
                title={constAtCursor === null ? t('methodologyScript.editConstTable.hint') : undefined}
                aria-disabled={constAtCursor === null}
                onClick={() => {
                  if (constAtCursor === null) {
                    showWarning(t('methodologyScript.editConstTable.hint'));
                    return;
                  }
                  openConstTable();
                }}
              >
                {t('methodologyScript.editConstTable')}
              </Button>
            )}
          </Group>
        }
      />

      {dirty && !readOnly && (
        <Text size="sm" c="dimmed" data-testid="script-unsaved">
          {t('methodologyScript.unsaved')}
        </Text>
      )}

      {state.conflict && (
        <Alert
          color="statusError"
          title={t('methodologyScript.conflict.title')}
          data-testid="script-conflict"
        >
          <Group justify="space-between" align="center">
            <Text size="sm">{t('methodologyScript.conflict.text')}</Text>
            <Button size="xs" variant="default" onClick={reload}>
              {t('methodologyScript.conflict.reload')}
            </Button>
          </Group>
        </Alert>
      )}

      {script.error !== null && (
        <ErrorAlert error={script.error} onRetry={() => void script.refetch()} />
      )}

      {version !== undefined && !isScript && (
        <Alert color="statusWarning">
          <Group justify="space-between">
            <Text size="sm">{t('methodologyScript.notScript')}</Text>
            <Button component={Link} to={versionsHref} size="xs" variant="default">
              {t('methodologies.versions')}
            </Button>
          </Group>
        </Alert>
      )}

      {isScript && version !== undefined && !version.isEditable && (
        <Alert color="statusWarning" title={t('methodologies.readOnly')}>
          {t('methodologies.readOnlyHint')}
        </Alert>
      )}

      <Group align="flex-start" wrap="wrap" gap="md">
        <Box style={{ flex: '1 1 480px', minWidth: 0 }}>
          <ScriptEditor
            ref={editor}
            value={state.text}
            onChange={(text) => dispatch({ type: 'edited', text })}
            onSave={() => saveNowRef.current()}
            onCursorChange={setCursor}
            diagnostics={diagnostics}
            completion={() => completionRef.current}
            ariaLabel={t('methodologyScript.editorLabel')}
            readOnly={readOnly}
            height="calc(100vh - 280px)"
          />
          <Text size="xs" c="dimmed" mt="xs" aria-live="polite" data-testid="script-status">
            {pausedSeconds !== null
              ? t('methodologyScript.checksPaused', { seconds: pausedSeconds })
              : checking
                ? t('methodologyScript.checking')
                : problemCount === 0
                  ? t('methodologyScript.problems.none')
                  : t('methodologyScript.problemCount', { count: problemCount })}
          </Text>
        </Box>

        <Box style={{ flex: '1 1 420px', minWidth: 0 }}>
          <Tabs
            value={tab}
            onChange={(value) => {
              if (value !== null) setTab(value as TabId);
            }}
            keepMounted
          >
            <Tabs.List>
              {previewUnavailable ? (
                <Tooltip label={t('methodologyScript.preview.moduleLevel')} multiline w={320} withArrow>
                  <Tabs.Tab
                    value="preview"
                    disabled
                    data-testid="script-preview-tab-disabled"
                    aria-describedby="script-preview-module-level"
                  >
                    {t('methodologyScript.tab.preview')}
                  </Tabs.Tab>
                </Tooltip>
              ) : (
                <Tabs.Tab value="preview">{t('methodologyScript.tab.preview')}</Tabs.Tab>
              )}
              <Tabs.Tab value="io">{t('methodologyScript.tab.io')}</Tabs.Tab>
              <Tabs.Tab value="tests">{t('methodologyScript.tab.tests')}</Tabs.Tab>
              <Tabs.Tab value="required">{t('methodologyScript.tab.required')}</Tabs.Tab>
              <Tabs.Tab
                value="problems"
                rightSection={
                  problemCount > 0 ? (
                    <Badge size="xs" color="statusError" circle>
                      {problemCount}
                    </Badge>
                  ) : undefined
                }
              >
                {t('methodologyScript.problems.title')}
              </Tabs.Tab>
            </Tabs.List>

            <Tabs.Panel value="preview" pt="sm">
              {previewUnavailable ? (
                <Alert color="gray" id="script-preview-module-level" data-testid="script-preview-module-level">
                  {t('methodologyScript.preview.moduleLevel')}
                </Alert>
              ) : (
              <Suspense fallback={null}>
              <ScriptPreviewPanel
                methodologyId={methodologyId}
                versionId={versionId}
                getText={() => stateRef.current.text}
                outline={outline}
                substanceEntryIds={substanceEntryIds}
                onCreateTest={readOnly ? undefined : createTestFromRow}
                onPeriodChange={setPreviewPeriod}
                onJump={(line, column) => editor.current?.reveal(line, column)}
              />
              </Suspense>
              )}
            </Tabs.Panel>

            <Tabs.Panel value="io" pt="sm">
              <Suspense fallback={null}>
                <ScriptOutlinePanel
                  methodologyId={methodologyId}
                  versionId={versionId}
                  outline={outline}
                />
              </Suspense>
            </Tabs.Panel>

            <Tabs.Panel value="tests" pt="sm">
              <Suspense fallback={null}>
              <ScriptTestsPanel
                methodologyId={methodologyId}
                versionId={versionId}
                editable={!readOnly}
                outline={outline}
                substances={substances.data}
                periodKey={previewPeriod}
                editing={testEditing}
                onEditingChange={setTestEditing}
              />
              </Suspense>
            </Tabs.Panel>

            <Tabs.Panel value="required" pt="sm">
              <Suspense fallback={null}>
                <MethodologyRequiredInputsPanel
                  methodologyId={methodologyId}
                  versionId={versionId}
                  editable={requiredInputsEditable}
                />
              </Suspense>
            </Tabs.Panel>

            <Tabs.Panel value="problems" pt="sm">
              <ScriptProblemsList
                diagnostics={diagnostics}
                onJump={(line, column) => editor.current?.reveal(line, column)}
              />
            </Tabs.Panel>
          </Tabs>
        </Box>
      </Group>

      {constRange !== null && (
        <Suspense fallback={null}>
          <ConstTableDialog
            opened
            onClose={() => setConstRange(null)}
            literal={constLiteral}
            constName={constRange.name}
            onApply={applyConst}
          />
        </Suspense>
      )}

      {comparing && (
        <Suspense fallback={null}>
          <ScriptCompareModal
            opened
            onClose={() => setComparing(false)}
            methodologyId={methodologyId}
            currentVersionId={versionId}
            currentText={state.text}
            versions={versions.data ?? []}
          />
        </Suspense>
      )}

      {publishFlow.dialogs}
    </Stack>
  );
}
