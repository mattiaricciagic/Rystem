# PlayFramework -> Azure Application Insights Agents View

Piano completo per rendere `Rystem.PlayFramework` visibile nella vista **Agents (Preview)** di Application Insights, basata su OpenTelemetry GenAI semantic conventions.

## 1) Obiettivo e definizione di done

### Obiettivo
- Far comparire le esecuzioni PlayFramework nella vista Agents con aggregazioni corrette per:
  - agente (`gen_ai.agent.id`, `gen_ai.agent.name`)
  - utilizzo token (`gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`)
  - operazioni modello e tool (`gen_ai.operation.name`)
  - errori/troubleshooting su trace correlate.

### Done (criteri minimi)
- I trace esportati verso Application Insights contengono attributi `gen_ai.*` su run reali.
- In Azure Portal, la vista Agents mostra almeno 1 agente PlayFramework con chiamate e token > 0.
- Tool calls e model calls sono drill-down navigabili dalla vista Agents/Search.
- Nessuna regressione su telemetria esistente `playframework.*`.

## 2) Stato attuale (baseline)

### Punti forti gia presenti
- `ActivitySource` centralizzato: `src/AI/Rystem.PlayFramework/Telemetry/PlayFrameworkActivitySource.cs`.
- `Meter` e metriche custom: `src/AI/Rystem.PlayFramework/Telemetry/PlayFrameworkMetrics.cs`.
- Tracing attivo nei punti core:
  - orchestrazione: `src/AI/Rystem.PlayFramework/Services/SceneManager.cs`
  - scene execution: `src/AI/Rystem.PlayFramework/Services/ExecutionModes/SceneExecutor.cs`
  - streaming LLM: `src/AI/Rystem.PlayFramework/Services/Helpers/StreamingHelper.cs`
  - tool execution: `src/AI/Rystem.PlayFramework/Services/Helpers/ToolExecutionManager.cs`
  - web/rag tools: `src/AI/Rystem.PlayFramework/Services/Tools/WebSearchTool.cs`, `src/AI/Rystem.PlayFramework/Services/Tools/RagTool.cs`

### Gap rispetto Agents View
- Tag prevalenti `playframework.*` (utili, ma non sufficienti per Agents View).
- Mancano attributi GenAI standardizzati (`gen_ai.agent.*`, `gen_ai.operation.name`, `gen_ai.usage.*`).
- Mancano linee guida end-to-end per wiring Azure Monitor (`UseAzureMonitor`, `AddSource`, `AddMeter`) nell'app host.
- Mancano test di conformita semantica `gen_ai.*`.

## 3) Principi architetturali

- Non rompere l'ecosistema attuale: mantenere `playframework.*` e aggiungere `gen_ai.*` in parallelo.
- Nessuna dipendenza Azure hard-coded nel core runtime (telemetria vendor-neutral via OpenTelemetry).
- Azure-specific resta nella documentazione/esempi host app, non nel motore PlayFramework.
- Attivazione controllata via setting (opt-in o opt-out configurabile).

## 4) Strategia di mapping semantico

### Identita agente
- Nuova policy configurabile (proposta):
  - `Factory` (default): un agente per factory PlayFramework.
  - `Scene`: un agente per scena, utile in orchestrazioni multi-scena.
  - `Custom`: delegate utente per id/name.

### Mappatura attributi principali
- `gen_ai.agent.id`:
  - mode `Factory`: `playframework:{factoryName}`
  - mode `Scene`: `playframework:{factoryName}:{sceneName}`
- `gen_ai.agent.name`:
  - mode `Factory`: `{factoryName}`
  - mode `Scene`: `{sceneName}`
- `gen_ai.operation.name`:
  - root run: `invoke_agent`
  - chiamata modello: `chat`
  - tool: `execute_tool`
- `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens` da token raccolti runtime.
- Modello provider/name con chiavi GenAI standard (in aggiunta alle esistenti `playframework.llm.*`).

Nota: la lista finale delle chiavi deve essere allineata all'ultima versione OTel GenAI semconv adottata da Azure Monitor.

## 5) Piano di implementazione (fasi)

## Fase 0 - Contratto tecnico e governance
- Definire versione target delle GenAI semantic conventions.
- Definire tabella ufficiale "PlayFramework event -> `gen_ai.*` attributes".
- Formalizzare policy privacy (prompt/response content) e default sicuri.

Deliverable:
- Documento mapping versionato in `Docs/`.

## Fase 1 - Fondazioni nel core
- Aggiungere costanti GenAI in telemetria, es. nuova sezione in:
  - `src/AI/Rystem.PlayFramework/Telemetry/PlayFrameworkActivitySource.cs`
- Introdurre helper centralizzato per valorizzare attributi `gen_ai.*` (evita duplicazione).
- Estendere `TelemetrySettings` con sezione Agent Observability, esempio:
  - `EnableGenAiSemantics`
  - `AgentIdentityMode`
  - `AgentIdFactory` / `AgentNameFactory`

Deliverable:
- API interna stabile per enrichment `gen_ai.*`.

## Fase 2 - Instrumentation delle operation critiche
- Root invocation (`SceneManager.Execute*`):
  - impostare `gen_ai.agent.id/name`
  - impostare `gen_ai.operation.name=invoke_agent`
- Chiamate LLM (`StreamingHelper` e flusso non streaming nel `SceneExecutor`):
  - `gen_ai.operation.name=chat`
  - tokens input/output
  - metadati modello
- Tool execution (`ToolExecutionManager`, `RagTool`, `WebSearchTool`, MCP):
  - `gen_ai.operation.name=execute_tool`
  - identificazione tool e outcome.

Deliverable:
- Trace complete con gerarchia coerente e attributi GenAI nei nodi giusti.

## Fase 3 - Compatibilita e rollout controllato
- Mantenere telemetria legacy `playframework.*` invariata.
- Se necessario, feature flag per attivare/disattivare enrichment GenAI.
- Logging diagnostico (debug) per vedere quando enrichment viene applicato.

Deliverable:
- Rollout senza regressioni per consumer esistenti.

## Fase 4 - Integrazione host con Azure Monitor
- Aggiornare documentazione d'uso host app con wiring consigliato:
  - OpenTelemetry + Azure Monitor exporter (`UseAzureMonitor`)
  - `AddSource(PlayFrameworkActivitySource.SourceName)`
  - `AddMeter(PlayFrameworkMetrics.MeterName)`
  - `APPLICATIONINSIGHTS_CONNECTION_STRING`
- Fornire snippet ASP.NET Core e worker service.

Deliverable:
- Guida quickstart Azure in `Docs/` + sezione README.

## Fase 5 - Verifica funzionale su Azure
- Smoke test su app campione con PlayFramework.
- Verifiche in Application Insights:
  - presenza agenti in vista Agents
  - token/calls non nulli
  - drill-down su traces tool/model.
- Query KQL di validazione (vedi sezione 8).

Deliverable:
- Checklist firmata e evidenze query/screenshot.

## 6) Dettaglio backlog tecnico (work items)

1. Aggiungere namespace costanti GenAI nel layer telemetria.
2. Aggiungere `AgentObservabilitySettings` e binding nel builder (`WithTelemetry`).
3. Implementare helper enrichment per `Activity` (single point).
4. Root span enrichment in `SceneManager`.
5. LLM span enrichment in `StreamingHelper` + path non-streaming.
6. Tool span enrichment in `ToolExecutionManager`, `RagTool`, `WebSearchTool`, MCP client.
7. Test unitari su mapping attributi e fallback identity.
8. Test integrazione: esecuzione scena con tool + verifica activity tags.
9. Aggiornare `Docs/TELEMETRY.md` con sezione Agents View.
10. Aggiungere quickstart Azure Application Insights Agents.

## 7) Piano test e quality gates

### Unit test
- Mapping `Factory` vs `Scene` per `gen_ai.agent.id/name`.
- Presenza `gen_ai.operation.name` sui vari span.
- Tokens mapping corretto su run streaming/non-streaming.

### Integration test
- Esecuzione scena con almeno:
  - 1 chiamata modello
  - 1 tool call
  - 1 final response
- Assert sulle activity emesse e attributi minimi richiesti.

### Quality gates
- Build/test verdi.
- Nessuna breaking change API pubblica non pianificata.
- Documentazione aggiornata e coerente.

## 8) KQL di validazione (post-deploy)

```kusto
dependencies
| where timestamp > ago(24h)
| where customDimensions has "gen_ai.agent.name"
| project timestamp, name, operation_Id, customDimensions
| take 100
```

```kusto
dependencies
| where timestamp > ago(24h)
| where customDimensions["gen_ai.operation.name"] in ("invoke_agent", "chat", "execute_tool")
| summarize calls=count() by op=tostring(customDimensions["gen_ai.operation.name"])
```

```kusto
dependencies
| where timestamp > ago(24h)
| where customDimensions has "gen_ai.usage.input_tokens"
| extend inTok=toint(customDimensions["gen_ai.usage.input_tokens"]), outTok=toint(customDimensions["gen_ai.usage.output_tokens"])
| summarize totalInput=sum(inTok), totalOutput=sum(outTok)
```

## 9) Rischi e mitigazioni

- Rischio: variazioni future semconv GenAI.
  - Mitigazione: costanti centralizzate + versione target esplicita.
- Rischio: cardinalita alta su agent id/name.
  - Mitigazione: strategy `Factory` default + naming policy controllata.
- Rischio: esposizione dati sensibili.
  - Mitigazione: default no prompt/response content; flag espliciti per ambienti protetti.
- Rischio: overhead telemetria.
  - Mitigazione: sampling e attribute length cap gia presenti in `TelemetrySettings`.

## 10) Piano di rilascio

### Milestone A - Internal preview
- Feature dietro flag `EnableGenAiSemantics`.
- Test su sample app interna con Application Insights.

### Milestone B - Beta pubblica
- Docs complete + esempi host Azure.
- Checklist compatibilita con metriche esistenti.

### Milestone C - GA
- Default on (valutare in base a feedback).
- Changelog ufficiale e migration notes.

## 11) Out of scope dichiarato

- Implementare UI/feature del portale Azure Agents View (non controllata dal progetto).
- Sostituire completamente telemetria custom `playframework.*`.
- Forzare dipendenze Azure Monitor dentro il core package.

## 12) Stima ad alto livello

- Fase 0-1: 1-2 giorni.
- Fase 2: 2-4 giorni.
- Fase 3-4: 1-2 giorni.
- Fase 5 + hardening: 1-2 giorni.

Totale indicativo: 5-10 giorni lavorativi, in base a profondita test e ambienti Azure disponibili.

## 13) Checkpoint operativo finale

- [ ] Semconv GenAI mappate e documentate.
- [ ] Enrichment attivo su root/model/tool spans.
- [ ] Quickstart Azure Monitor validato.
- [ ] KQL validation pass.
- [ ] Agents View mostra agenti, token, tool/model traces.
