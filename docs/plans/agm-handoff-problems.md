# AGM: проблемы и доработки (handoff)

> Дата: 2026-08-14  
> Контекст: fan-out в AGM для конкурентного анализа AgMemory vs [ai-memory](https://github.com/akitaonrails/ai-memory)  
> Потребитель артефакта: команда **ai-group-manager (AGM)**  
> Репозиторий наблюдения: `ag-memmory` (здесь только отчёт; правки — в AGM)

Связанный план продукта памяти (не AGM): [`docs/plans/competitive-ai-memory/`](../competitive-ai-memory/README.md).

---

## Краткий вердикт

AGM Cursor fan-out **дошёл до Completed**, но **не доставил файлы в целевой репозиторий**. Reviewer корректно дал `block`. OpenRouter на runner недоступен. Без фикса write-root / `workingDirectory` и провайдеров такой пайплайн нельзя считать надёжным для «команда агентов + AGM → MD в репо».

---

## P0 — блокеры доставки артефактов

### 1. Write-root / `workingDirectory` не указывает на целевой проект

| | |
| --- | --- |
| **Симптом** | Workspace `workingDirectory` = `null`. Фактический write-root агентов: `/Users/romanponomarev/Documents/Work/ai-group-manager`, а не `ag-memmory`. |
| **Эффект** | Analyst заявил 11 MD в `docs/plans/competitive-ai-memory/`; в целевом репо **0/11**. В write-root AGM файлов тоже не оказалось. |
| **Ожидание** | Workspace создаётся/привязывается с явным `workingDirectory` = корень целевого репо; folder alias `write` → `.` пишет **туда**. |
| **Доработка** | 1) Обязательный `workingDirectory` при create workspace (или fail-fast). 2) В `agm_fanout_get` / member output показывать **resolved write root**. 3) Acceptance: после Completed файлы существуют по ожидаемому path относительно workspace root. |
| **Воспроизведение** | Fan-out с задачей «создай MD в `docs/plans/...`» при workspace без `workingDirectory` / с root = AGM. |

### 2. Completed + заявленные файлы ≠ проверка на диске

| | |
| --- | --- |
| **Симптом** | Run status `Completed`, member outputs описывают файлы; reviewer `block` из‑за отсутствия файлов. |
| **Эффект** | Ложное чувство успеха у orchestrator/родителя; ручная материализация. |
| **Ожидание** | Terminal success для «write MD» задач учитывает filesystem evidence или явный `artifacts_missing`. |
| **Доработка** | Post-step hook / reviewer gate: `test -f` / list expected paths; статус `CompletedWithMissingArtifacts` или fail member. Не принимать «я написал» без path check. |

### 3. OpenRouter на runner = `Unauthorized`

| | |
| --- | --- |
| **Симптом** | Запрошены Cursor + OpenRouter; OpenRouter members не стартуют / auth fail. |
| **Эффект** | Нет второго провайдера; только Cursor. |
| **Ожидание** | Либо рабочий OpenRouter (`AGM_OPENROUTER_ENABLED` + `OPENROUTER_API_KEY` на Runner), либо **явный preflight fail** до fan-out с понятным текстом. |
| **Доработка** | 1) Preflight provider health в `agm_fanout_start` / `agm_service_status`. 2) Документ env для Runner. 3) Не молча пропускать запрошенный провайдер. |

---

## P1 — надёжность провайдеров и fan-out

### 4. Codex `ProviderTurnFailed`

| | |
| --- | --- |
| **Симптом** | Ранний Codex fan-out → `Failed` (`ProviderTurnFailed`). Пользователь затем исключил Codex как недоступный. |
| **Доработка** | Диагностика причины в compact member error (timeout / auth / model / runner). Retry policy и «provider unavailable» vs «task failed». |

### 5. Нет жёсткой привязки folder alias к целевому репо

| | |
| --- | --- |
| **Симптом** | Alias `write` → `.` при root = AGM пишет не туда, куда ожидает задача в другом git-репо. |
| **Доработка** | При старте fan-out принимать / валидировать `workspaceFolderAlias` + expected relative paths; предупреждение если alias root ≠ path из prompt. |

### 6. Идемпотентность «создай N файлов» без верификации

| | |
| --- | --- |
| **Симптом** | Повторный run не чинит missing artifacts автоматически. |
| **Доработка** | Шаблон задачи / skill: checklist путей → write → verify → только потом handoff reviewer. |

---

## P2 — DX / наблюдаемость

### 7. Слабая видимость write root в UI/MCP wait compact

| | |
| --- | --- |
| **Доработка** | В compact progress: `workspaceId`, `workingDirectory`, `writeRoot`, `folderAliases`. |

### 8. Ложные «counts match» аналогии (память) — не путать с AGM

Отдельно для AgMemory (волна D): совпадение счётчиков ≠ один store.  
Для AGM аналогично: **Completed ≠ артефакты на месте**. Один и тот же класс ошибки доверия к метрике.

### 9. Документация handoff «команда агентов + AGM»

| | |
| --- | --- |
| **Доработка** | Короткий runbook: create workspace **с** workingDirectory целевого репо → folder add ReadWrite → fan-out members (Cursor/OpenRouter) → wait → verify files → только потом accept. |

---

## Что уже работает (не ломать)

- Cursor fan-out роли scout → architect → analyst → reviewer доходят до terminal.
- Reviewer **правильно** блокирует при отсутствии файлов (не ослаблять этот gate).
- MCP wait/get без busy-poll — ок как API surface.

---

## Предлагаемый порядок фикса в AGM

1. **P0.1** — `workingDirectory` обязателен / fail-fast + показ write root.  
2. **P0.2** — artifact verification перед Completed для write-задач.  
3. **P0.3** — OpenRouter preflight + docs env.  
4. **P1.4–6** — Codex errors, alias validation, write-verify template.  
5. **P2** — compact observability + runbook.

---

## Acceptance для AGM (минимальный)

- [ ] Workspace без `workingDirectory` нельзя использовать для write fan-out (или явный dry-run only).
- [ ] Fan-out «создай `docs/plans/foo/README.md`» в репо X → файл есть в X после Completed.
- [ ] Запрос OpenRouter при битых credentials → отказ **до** старта members с текстом причины.
- [ ] Reviewer/parent видит resolved write root в get/wait.

---

## Вне скоупа этого handoff

- Волны A–D AgMemory (`docs/plans/competitive-ai-memory/`) — отдельный продукт.  
- Расширение AgMemory MCP tools.  
- Production auth AGM shared-server (если не связано с P0 write-root).

---

## Приложение: факт прогона

| Поле | Значение |
| --- | --- |
| Workspace | `ab1529b8-0753-4547-8c19-dee7296f2c97` |
| Cursor fan-out | `b568d77b-72b6-4bc6-9ac8-5b4f7069ea91` → Completed |
| Reviewer | `block` (0/11 файлов в ag-memmory) |
| Write-root факт | `.../Work/ai-group-manager` |
| OpenRouter | Unauthorized на runner |
| Codex | ProviderTurnFailed / исключён пользователем |
| Обход | MD пакет записан вручную в `ag-memmory/docs/plans/competitive-ai-memory/` |

---

## Verification 2026-08-15 (ag-memmory)

**Вопрос:** можно ли через AGM изменить файл в текущем проекте `ag-memmory`?

**Ответ: нет (пока).**

### Факты

| Проверка | Результат |
| --- | --- |
| Host `workspaceWriteRoot` | `/Users/romanponomarev/Documents/Work/ai-group-manager` (не ag-memmory) |
| Core `AgentManager__WorkspaceWriteRoot` | то же |
| `.env` `AGM_WORKSPACE_WRITE_PATH` | то же |
| MCP create workspace с absolute path | `ValidationFailed` (нужен relative под write-root) |
| Compact fan-out `writeRoot` / `workingDirectory` | есть (`write` / `.`) — P2 частично закрыт |
| Codex chat / fan-out | `ProviderTurnFailed` |
| Cursor fan-out | `ProviderUnavailable` (doctor: CursorUnavailable) |
| Cursor `chat_send` + alias `write` | job `Succeeded`; файл в blob-артефактах AGM; **на диске репо нет** ни в ag-memmory, ни в ai-group-manager |
| Целевой probe path | `docs/plans/agm-write-probe-20260815.md` — отсутствует в обоих репо |

### Почему

1. **Wrong write root:** AGM write-root указывает на `ai-group-manager`. Даже успешный promote не попадёт в `ag-memmory`.
2. **Isolation + publish ≠ host file:** Cursor RW пишет в isolation; plain chat публикует blob (`data/artifacts/blobs/...`), но без developer/explicit publish путь не promote’ится в canonical tree.
3. **Providers:** Codex turn fail; Cursor недоступен для fan-out (хотя одиночный chat_send отработал).

### Что нужно, чтобы проверка стала зелёной

1. Выставить `AGM_WORKSPACE_WRITE_PATH=/Users/romanponomarev/Documents/Work/ag-memmory` (или parent `.../Work` + relative `ag-memmory`).
2. `./agm-standalone.sh update` и убедиться, что Runner `config.json` / Core `service.env` обновились.
3. Workspace с `workingDirectory: "."` (или `ag-memmory`) + ReadWrite alias `write`.
4. Повторный probe: developer fan-out или `.agm/publish-artifacts.json` + проверка `test -f` **в ag-memmory**.
5. Починить Cursor CLI для fan-out и/или Codex `ProviderTurnFailed`.

