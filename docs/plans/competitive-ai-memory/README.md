# План: конкурентный анализ AgMemory vs ai-memory

> Источник: AGM fan-out (Cursor: scout → architect → analyst → reviewer), 2026-08-14.  
> Референс: [akitaonrails/ai-memory](https://github.com/akitaonrails/ai-memory).  
> Продукт плана: **ag-memmory / AgMemory**, не AGM Core и не `Agm.Memory.*`.

## Цель

Сопоставить AgMemory с ai-memory, взять **идеи** (не стек) и разложить доработки в проверяемые work-packages так, чтобы память экономила токены, wiki оставалась понятной, а Yuki/MCP отвечали по одной согласованной памяти.

## Краткие выводы

| Зона | Вердикт |
| --- | --- |
| Adopt | compiled wiki, bounded handoff, authority-aware recall, entity-assisted recall, cross-agent continuity, capture exclusions |
| Reject | git-md как primary store, lifecycle hooks на всех клиентов, MCP `memory_write_page` / `handoff_accept`, shared-server auth, замена LanceDB |
| Уже закрыто | lazy wiki, relations UI, Ready compiled reads, Yuki 8+800 + handoff, K + `readerAligned` (`0807400`) |
| Дальше | волны **A → B → C → D** (см. `02-roadmap/waves.md`) |

## Порядок чтения

1. [00 overview](00-competitive-analysis/overview.md) · [adopt-reject](00-competitive-analysis/adopt-reject.md)
2. [01 done](01-current-baseline/done.md) · [gaps](01-current-baseline/gaps.md)
3. [02 waves](02-roadmap/waves.md)
4. Work-packages: [A](03-work-packages/A-event-catalog.md) · [B](03-work-packages/B-typed-inject.md) · [C](03-work-packages/C-compiled-wiki-spa.md) · [D](03-work-packages/D-web-mcp-alignment.md)
5. [04 criteria](04-acceptance/criteria.md)

## AGM / провайдеры

- Запрошены Cursor + OpenRouter; **OpenRouter** на runner = `Unauthorized` → не использовался.
- Codex по запросу пользователя не использовался.
- Fan-out Cursor завершён; reviewer дал `block` из‑за отсутствия файлов в write-root AGM (`ai-group-manager`). Этот пакет записан **в репозиторий ag-memmory**.
- Handoff проблем AGM: [`../agm-handoff-problems.md`](../agm-handoff-problems.md).

## Non-goals пакета

Production-код, расширение MCP tools, vector RRF, production auth, миграция на git-md wiki.
