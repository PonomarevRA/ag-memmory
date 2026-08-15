# Roadmap: волны A–D

## Граф зависимостей

```text
        ┌─── A (Event ∉ catalog invalidate)
        │         │
        │         ▼
 B ∥ A  │         C (inbox / compiled wiki SPA)
        │
        └─── D (после A; процедура Web↔MCP; Q7)
```

| Волна | Параллельность | Блокеры |
| --- | --- | --- |
| **A** | старт | нет |
| **B** | параллельно с A | нет (независимо от catalog) |
| **C** | после A | A (eligibility/invalidate) |
| **D** | после A (можно после B) | A желательно; **Q7** обязателен для unify |

## Краткие цели волн

| ID | Цель в одном предложении |
| --- | --- |
| A | Chat Event пишется, но не дёргает wiki/catalog generation |
| B | Yuki inject только Fact/Decision/Constraint/Outcome/Summary; Event только в handoff |
| C | Inbox ≠ Event; tree = wiki metadata; home = existing tree API |
| D | Доказуемое выравнивание Web host и MCP process (или явный mismatch) |

## Порядок внедрения (рекомендация)

1. **A + B** одним PR или двумя узкими PR (можно параллельно разными людьми).
2. **C** после merge A.
3. **D** — сначала чеклист/док + тест процедуры; unify config только при закрытом Q7.

## Definition of Done всего пакета

См. [04-acceptance/criteria.md](../04-acceptance/criteria.md).  
Критерий продукта: меньше токенов на шум Event в inject + wiki Ready без rebuild от chat turns + ясный Web↔MCP статус.

## Что не входит в волны A–D

Реализация git-md, расширение MCP, production auth, vector DoD, type-priority, invalidate-on-Event «как отдельная фича» сверх A.
