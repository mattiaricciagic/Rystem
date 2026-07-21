# Runtime scene and tool descriptions

Piano completo per aggiungere a `Rystem.PlayFramework` la risoluzione a runtime delle descrizioni delle scene e dei tool, mantenendo compatibilita con la configurazione statica esistente.

> Stato al 16 luglio 2026: implementato per `10.0.11-beta.22`, inclusi cataloghi immutabili, refresh background/manuale/`EveryRequest`, change notification opzionale, pin per execution, last-known-good memory/distributed/custom, discovery, telemetria strutturata e test automatici. Il documento resta il riferimento di design e di accettazione; la pubblicazione del package e l'adozione TimeVision sono attivita successive.

## 1) Contesto

Fino alla versione `10.0.11-beta.21` soltanto gli actor supportavano messaggi dinamici sincroni e asincroni:

```csharp
AddMainActor(Func<SceneContext, string> messageFactory, ...)
AddMainActor(Func<SceneContext, CancellationToken, Task<string>> asyncMessageFactory, ...)

AddActor(Func<SceneContext, string> messageFactory, ...)
AddActor(Func<SceneContext, CancellationToken, Task<string>> asyncMessageFactory, ...)
```

Le factory vengono invocate durante la richiesta e possono quindi risolvere servizi scoped attraverso `SceneContext.ServiceProvider`, per esempio un provider alimentato da file, memoria, database o Azure App Configuration.

Le descrizioni delle scene e dei tool seguono invece un ciclo di vita differente:

- `AddScene` riceve una descrizione statica;
- le configurazioni delle scene sono registrate come singleton;
- `ISceneFactory` e registrata come singleton;
- `SceneFactory` costruisce `Scene` e gli `AITool` una sola volta;
- `Scene` materializza `Description`, `AiTool`, `AiTools` e le descrizioni dei tool nel costruttore;
- Direct, Planning e Dynamic Chaining consumano successivamente questi oggetti gia materializzati.

Modificare soltanto `Scene.Description` non sarebbe sufficiente: la descrizione usata dal modello e incorporata negli `AITool` creati tramite `AIFunctionFactory`.

## 2) Obiettivo

Consentire la risoluzione a runtime di:

- scene description;
- descrizione principale dei service tool;
- descrizione principale degli endpoint tool;
- descrizione principale dei client interaction tool.

Le descrizioni sono **globali per l'applicazione**: una stessa versione del catalogo e condivisa da tutte le richieste e non varia per tenant, utente o contenuto della `SceneContext`.

Le factory dinamiche devono poter accedere a uno scope `IServiceProvider` e al `CancellationToken`. Non ricevono `SceneContext`: essendo globali, devono poter essere eseguite dal background coordinator senza costruire un contesto di richiesta artificiale e senza dipendere da utente o tenant.

Esempio atteso:

```csharp
builder.AddScene(
    "orders",
    async (context, cancellationToken) =>
        await context.Services
            .GetRequiredService<IAiPromptProvider>()
            .GetAsync("scene:orders:description", cancellationToken),
    scene => scene.WithService<IOrderService>(tools =>
    {
        tools.WithMethod(
            x => x.SearchAsync(default!, default),
            "search_orders",
            async (context, cancellationToken) =>
                await context.Services
                    .GetRequiredService<IAiPromptProvider>()
                    .GetAsync(
                        "tool:orders:search_orders:description",
                        cancellationToken));
    }));
```

## 3) Non obiettivi della prima iterazione

La prima iterazione non rende dinamici:

- nome di scene e tool;
- firma del metodo eseguito dal tool;
- route, metodo HTTP o tipi request/response degli endpoint tool;
- JSON Schema dei parametri;
- descrizioni dei singoli parametri nello schema;
- struttura dei client interaction tool;
- definizioni ricevute da server MCP;
- RAG e web search tool che seguono un ciclo di creazione differente.
- pin del catalogo per l'intera durata di una conversazione multi-turno.

Nomi e schema costituiscono il contratto operativo del tool e devono restare stabili durante una conversazione. La loro eventuale dinamicita richiede un progetto separato con versionamento dello schema e gestione delle chiamate pendenti.

## 4) Decisione architetturale

Le configurazioni registrate all'avvio rimangono template singleton. Le descrizioni globali vengono aggiornate fuori dal percorso della richiesta, automaticamente o on demand secondo la modalita scelta, e pubblicate come catalogo immutabile attraverso un atomic swap. Ogni richiesta standard legge il catalogo corrente senza effettuare I/O remoto nel percorso critico e produce, quando necessario, la proiezione di esecuzione contenente gli `AITool` associati a quella versione.

```text
Runtime description source
        |
        | startup / timer / change notification / manual
        v
Runtime description coordinator
        |
        | validazione + atomic swap
        v
Versioned global description catalog
        |
        +-- richiesta standard: lettura locale O(1)
        +-- EveryRequest: refresh forzato una volta per richiesta
        |
        v
MaterializedSceneCatalog vN
```

Non devono essere mutate le istanze singleton di `Scene`, `ISceneTool` o `AITool`. Una mutazione condivisa introdurrebbe race condition e contaminazione tra utenti, tenant o richieste concorrenti.

La coordinazione runtime deve essere confinata a due soli servizi:

- `RuntimeDescriptionCatalogManager`, che carica, valida, calcola l'hash, materializza, pubblica e implementa il refresh manuale;
- `RuntimeDescriptionBackgroundService`, adapter sottile per startup, timer e change notification; in `Manual` esegue al massimo l'eventuale startup refresh e termina.

Il manager dipende inoltre da un'unica astrazione infrastrutturale `IRuntimeDescriptionSnapshotStore`, necessaria per il last-known-good memory/distributed ma priva di logica di refresh o materializzazione. Non vengono introdotti resolver per livello, cache per scena, materializer registrati separatamente o factory aggiuntive di cataloghi.

## 5) Drawback principale della decisione

> **La rilettura a runtime trasforma un catalogo oggi costruito una sola volta in un catalogo globale che deve essere ricaricato, validato e rimaterializzato a ogni nuova versione.**

Questo comporta costi e complessita non eliminabili:

1. **Latenza aggiuntiva durante refresh e modalita `EveryRequest`.** Nel percorso standard il catalogo viene aggiornato in background o manualmente e la richiesta esegue una lettura locale. Un provider remoto lento entra nel percorso critico soltanto quando viene abilitata esplicitamente la modalita `EveryRequest`.
2. **Piu allocazioni e lavoro CPU durante il refresh.** Le declaration che incorporano una descrizione dinamica devono essere ricreate quando cambia la versione del catalogo. Nel percorso standard non vengono ricreate per richiesta; `EveryRequest` accetta invece esplicitamente questo costo.
3. **Maggiore complessita interna.** Direct, Planning, Dynamic Chaining, esecuzione della scena e discovery non possono piu assumere che `ISceneFactory.Scenes` e `ScenesAsAiTool` rappresentino necessariamente i metadata effettivi. La complessita viene confinata nel catalog manager; gli execution mode leggono soltanto il catalogo materializzato agganciato a `SceneContext`.
4. **Possibili snapshot differenti tra richieste o turni.** Il compromesso e configurabile: `Execution` mantiene la versione negli stati interrotti ed e il default; `Request` privilegia la versione piu recente a ogni chiamata. Il pin dell'intera conversazione e escluso dalla prima release. Dopo un refresh, una conversazione esistente puo conservare il MainActor storico e usare descriptions correnti: evitarlo richiede freeze applicativo o un nuovo contesto, non il solo pin del catalogo.
5. **Debug e riproducibilita piu difficili.** Il codice registrato e la versione del pacchetto non bastano piu a ricostruire il prompt effettivo: servono versione e origine dei metadata runtime.
6. **Nuove modalita di errore e dipendenza da snapshot potenzialmente obsoleti.** Timeout, cancellazioni, valori vuoti, errori del provider, store non disponibile e snapshot corrotti possono impedire il routing ancora prima dell'esecuzione di una scena. Il last-known-good aumenta la disponibilita ma puo mantenere descrizioni precedenti: la retention limita recovery dopo restart e versioni storiche, mentre un processo gia attivo conserva il proprio catalogo corrente anche durante outage piu lunghi. Memory non protegge dai restart e lo store distribuito aggiunge una dipendenza operativa. La mitigazione scelta mantiene sempre un catalogo completo, applica retention limitata allo store e rende ogni recovery o mancato refresh osservabile.
7. **API sincrone esistenti meno rappresentative.** Proprieta come `IScene.Description`, `IScene.AiTools` e `ISceneFactory.ScenesAsAiTool` continuano a descrivere template/fallback e non i valori effettivi di una richiesta. I consumer runtime devono migrare alla vista request-local gia materializzata su `SceneContext`; mantenere entrambi i contratti evita il breaking change ma richiede documentazione inequivocabile.
8. **Superficie pubblica e sensibilita dei contenuti maggiori.** La vista read-only per i planner custom espone le descriptions effettive al codice che possiede il `SceneContext`. Questo e necessario per l'estensibilita, ma aumenta il numero di consumer che devono evitare log, serializzazione o persistenza accidentale del testo.
9. **Integrita non equivale a sicurezza semantica.** Hash, template match, limiti e atomic swap impediscono corruzione e cataloghi misti, ma una description valida puo comunque contenere istruzioni pericolose o degradare routing e tool selection. La libreria non puo risolvere questo rischio genericamente: source governance e test avversariali restano obbligatori nell'applicazione.

La mitigazione e pubblicare snapshot globali immutabili, eseguire il refresh fuori dal percorso standard della richiesta, offrire `Manual` per change control esplicito, limitare `EveryRequest` a test e diagnostica, mantenere fallback statici e confinare la coordinazione runtime in due servizi. Il drawback rimane comunque parte strutturale della feature e deve essere accettato esplicitamente prima dell'implementazione.

## 6) Semantica di consistenza e caching

### 6.1 Catalogo globale versionato

Il refresh coordinator costruisce un nuovo snapshot fuori dal percorso delle richieste. Lo snapshot diventa corrente soltanto dopo che tutti i valori obbligatori sono stati caricati e validati. La pubblicazione avviene con atomic swap: una richiesta vede integralmente la versione precedente oppure quella nuova, mai un catalogo parzialmente aggiornato.

Lo snapshot contiene gia tutte le scene e tutti i tool locali materializzati come declaration. Non esiste una seconda fase lazy nel percorso della richiesta.

Il valore selezionato all'inizio di una richiesta deve restare stabile per tutta la singola esecuzione PlayFramework:

- catalogo e source stamp vengono acquisiti dallo stesso `PublishedRuntimeDescriptionState`;
- iterazioni multiple del tool calling loop riusano la stessa versione;
- una scena rieseguita in Dynamic Chaining riusa lo stesso snapshot;
- un refresh completato durante la richiesta diventa visibile soltanto alla richiesta successiva;
- richieste concorrenti possono terminare usando versioni diverse se l'atomic swap avviene tra i rispettivi istanti di avvio, ma ciascuna richiesta resta internamente coerente.

La consistenza tra richiesta iniziale e resume dopo `AwaitingClient` segue la modalita configurata descritta di seguito.

### 6.2 Modalita di refresh

```csharp
public enum RuntimeDescriptionRefreshMode
{
    Background,
    Manual,
    EveryRequest
}
```

#### `Background` - default di produzione

- refresh fuori dal percorso critico;
- caricamento iniziale all'avvio, con policy esplicita se la sorgente non e disponibile;
- refresh periodico configurabile;
- supporto opzionale a change notification quando la sorgente lo consente;
- lettura locale dello snapshot corrente durante la richiesta;
- una sola pubblicazione atomica per versione valida.

Il timer periodico rimane necessario anche quando sono disponibili notifiche, come safety net contro notifiche perse o errori temporanei.

Timer e change notification usano un tentativo non bloccante sul lock della factory PlayFramework. Se un refresh e gia attivo, il trigger viene ignorato; il timer periodico recupera eventuali variazioni non osservate. Il refresh manuale attende invece il lock e forza un nuovo ciclo dopo quello in corso.

#### `Manual` - pubblicazione globale on demand

- mantiene un catalogo globale condiviso, come `Background`;
- non avvia timer e non sottoscrive change notification;
- dopo l'eventuale caricamento iniziale, modifica il catalogo soltanto tramite `IRuntimeDescriptionRefresher.RefreshAsync`;
- `RefreshAtStartup` rimane indipendente e configurabile;
- se `RefreshAtStartup` e `true`, il background service esegue un solo ciclo iniziale e termina senza avviare loop o subscription;
- se `RefreshAtStartup` e `false`, l'host deve completare un refresh manuale prima delle richieste che richiedono un catalogo dinamico; in caso contrario si applica `FailureMode`;
- `BackgroundRefreshInterval` e `RefreshOnChange` non hanno effetto;
- discovery e richieste non causano refresh impliciti;
- un refresh riuscito pubblica globalmente e aggiorna lo snapshot store, a differenza del catalogo request-local di `EveryRequest`.

Questa modalita e adatta a deployment con change control esplicito, operazioni amministrative e batch di evaluation. Non introduce selezione di catalogo per richiesta: tutte le richieste della factory continuano a osservare lo stesso current catalog.

#### `EveryRequest` - test e diagnostica

- forza la rilettura della sorgente una volta all'inizio di ogni richiesta PlayFramework;
- non rilegge a ogni chiamata LLM, round di Dynamic Chaining o iterazione del tool calling loop;
- usa uno snapshot dedicato alla richiesta soltanto dopo validazione completa;
- non sostituisce il catalogo globale pubblicato dal background coordinator;
- e mutuamente esclusiva con il background service, che non viene avviato per quella configurazione;
- propaga il `CancellationToken` della richiesta;
- deve essere chiaramente marcata come modalita non raccomandata per produzione.

Questa modalita permette a un test di cambiare il valore della sorgente tra due richieste e verificare immediatamente che il nuovo valore venga utilizzato, senza timer, attese o race con il background service.

### 6.3 Refresh manuale

Esporre un servizio amministrativo/testabile:

```csharp
public interface IRuntimeDescriptionRefresher
{
    Task<RuntimeDescriptionRefreshResult> RefreshAsync(
        CancellationToken cancellationToken = default);
}
```

Il refresh manuale e utile per integration test deterministici e operazioni amministrative. Non deve essere direttamente controllabile attraverso `SceneRequestSettings` o input inviato dal client, per evitare che un utente possa trasformarlo in un meccanismo di denial of service verso la sorgente remota.

### 6.4 Responsabilita del framework

PlayFramework deve:

- coordinare refresh iniziale, periodico, notificato, manuale e `EveryRequest`;
- usare un solo `SemaphoreSlim` per factory PlayFramework e non sovrapporre refresh;
- creare un solo scope DI e una sola istanza di `RuntimeDescriptionContext` per ogni tentativo di refresh;
- invocare le factory in ordine deterministico e sequenziale nella prima release;
- pubblicare soltanto cataloghi completi e validi;
- conservare il riferimento alla versione scelta dalla richiesta;
- evitare risoluzioni duplicate nella stessa richiesta, anche in `EveryRequest`;
- propagare il `CancellationToken`;
- validare valori null, vuoti o composti soltanto da spazi;
- applicare la policy di fallback configurata;
- mantenere e validare snapshot last-known-good completi;
- non introdurre I/O dello snapshot store nel normale hot path;
- produrre log e telemetria senza registrare per default il contenuto completo.

L'esecuzione sequenziale evita di imporre thread-safety a servizi scoped applicativi arbitrari. Provider con molte descrizioni devono preferire un accessor scoped che carica una sola volta uno snapshot completo e serve poi lookup locali. La concorrenza configurabile tra resolver non viene introdotta nella prima release; verra rivalutata soltanto se i benchmark dimostrano che il caricamento snapshot non e sufficiente.

### 6.5 Responsabilita del provider applicativo

Il provider applicativo deve gestire:

- accesso alla sorgente Azure App Configuration, file, database o memoria;
- eventuali change notification e identificatore di versione;
- versionamento e audit dei contenuti alla fonte;
- timeout, retry, circuit breaker ed eventuale cache delle singole letture verso sorgenti remote.

PlayFramework gestisce invece il last-known-good del **catalogo completo**. I due livelli non si sostituiscono: il provider protegge l'accesso alla sorgente, mentre lo snapshot store permette di continuare a usare una versione gia validata. PlayFramework non deve dipendere direttamente da uno specifico `IAiPromptProvider`: l'integrazione deve avvenire attraverso un contratto generico di runtime description source o un adapter applicativo.

### 6.6 Consistenza tra richiesta e resume

```csharp
public enum RuntimeDescriptionConsistencyMode
{
    Request,
    Execution
}
```

#### `Request`

Ogni richiesta acquisisce il catalogo corrente. Un resume dopo client interaction puo quindi vedere una versione piu recente rispetto alla richiesta che ha iniziato l'esecuzione.

#### `Execution` - default

La versione viene mantenuta per tutta l'esecuzione logica, inclusi resume dagli stati interrotti gia riconosciuti da PlayFramework:

- `AwaitingClient`;
- `ExecutingScene`;
- `Chaining`.

Quando l'esecuzione termina, un nuovo turno della stessa conversazione acquisisce il catalogo corrente. Questo protegge piano, chaining e client interaction senza impedire il runtime reload nelle conversazioni lunghe.

L'acquisizione di un nuovo catalogo non implica la ricostruzione del MainActor gia presente nella cronologia. Per una conversazione caricata da cache/repository, PlayFramework conserva l'`InitialContext` esistente e non sostituisce retroattivamente le istruzioni di sistema; gli actor di scena eventualmente rieseguiti osservano invece l'identita della richiesta corrente.

Ne consegue una consistenza eventuale intenzionale nelle conversazioni lunghe: dopo un refresh, un nuovo turno puo combinare il MainActor storico con scene/tool descriptions e actor di scena correnti. `RuntimeDescriptions.SourceVersion` identifica lo snapshot validato per la richiesta corrente, ma non certifica l'origine di testi actor gia persistiti o prodotti da factory che ignorano tale versione.

`ExecutionState` memorizza soltanto l'identita del catalogo, non declaration o executor:

```csharp
public string? RuntimeDescriptionCatalogId { get; set; }
```

Il campo viene copiato da e verso `SceneContext` insieme agli altri dati di resume. Poiche `SceneContext.LoadFromStoredConversation` ripristina `ExecutionState` soltanto per fasi interrotte, un nuovo turno completato non rimane accidentalmente vincolato alla vecchia versione.

La consistenza `Conversation` non viene implementata nella prima release. Richiederebbe pin congiunto di `CatalogId` e source stamp, retention storica potenzialmente lunga, regole per actor rieseguiti e una decisione esplicita sulla sostituzione del MainActor salvato. Un semplice pin del solo catalogo produrrebbe una falsa garanzia di coerenza dell'intero prompt.

Applicazioni che richiedono stabilita multi-turn devono congelare la sorgente per la durata logica desiderata oppure gestire a livello applicativo una nuova conversazione quando cambia l'effective prompt snapshot.

### 6.7 Versione pinned non disponibile

```csharp
public enum MissingRuntimeDescriptionVersionBehavior
{
    UseLatestAndWarn,
    Throw
}
```

`UseLatestAndWarn` e il default. Se il catalogo richiesto da `ExecutionState` non e disponibile, PlayFramework usa il catalogo corrente e produce warning, metrica e activity event con versione richiesta e utilizzata.

`Throw` e disponibile per applicazioni che privilegiano riproducibilita e fail-closed rispetto alla disponibilita.

### 6.8 Snapshot store

Introdurre un contratto pubblico e sostituibile:

```csharp
public interface IRuntimeDescriptionSnapshotStore
{
    ValueTask<RuntimeDescriptionSnapshot?> GetLatestAsync(
        string factoryName,
        CancellationToken cancellationToken = default);

    ValueTask<RuntimeDescriptionSnapshot?> GetAsync(
        string factoryName,
        string catalogId,
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        string factoryName,
        RuntimeDescriptionSnapshot snapshot,
        CancellationToken cancellationToken = default);

    ValueTask RefreshLatestExpirationAsync(
        string factoryName,
        string catalogId,
        CancellationToken cancellationToken = default);
}
```

Lo snapshot serializzabile contiene:

- identita completa `CatalogId`, `TemplateHash`, `ContentHash` e algoritmo;
- descrizioni risolte indicizzate tramite identificatori interni stabili di scena/tool;
- source version, source ed ETag diagnostici, se disponibili;
- `PublishedAt` e `LastValidatedAt`;
- versione del formato di serializzazione.

Non contiene `AITool`, declaration, executor, delegate, `MethodInfo`, `IServiceProvider` o altri riferimenti runtime. Dopo una lettura dallo store, il manager ricalcola gli hash, verifica formato e `TemplateHash`, quindi rimaterializza il catalogo usando il template statico dell'applicazione corrente.

Implementazioni previste:

- `MemoryRuntimeDescriptionSnapshotStore`, default, utile nello stesso processo ma non resiliente a restart o failover;
- `DistributedRuntimeDescriptionSnapshotStore`, opt-in, basato sull'`IDistributedCache` gia usato da PlayFramework e quindi compatibile con Redis;
- store custom registrabile tramite DI per requisiti di persistenza differenti.

La modalita `Distributed` deve essere scelta esplicitamente. Se `IDistributedCache` non e registrato, la validazione delle opzioni fallisce all'avvio invece di degradare silenziosamente a memory.

### 6.9 Ordine di recovery e fallback

Con `FailureMode.UseFallback`, quando la sorgente non produce un candidato completo e valido, il manager applica questo ordine:

1. mantiene il catalogo corrente compatibile, se esiste;
2. altrimenti cerca il last-known-good nello snapshot store e lo usa solo dopo verifica di formato, hash e `TemplateHash`;
3. altrimenti costruisce un catalogo completo usando esclusivamente valori statici e fallback statici registrati all'avvio;
4. se anche questo non e possibile, fallisce con un errore esplicito prima della chiamata LLM.

Un refresh parzialmente fallito non combina valori nuovi, valori precedenti e fallback in un candidato misto. Se esiste un catalogo corrente valido, resta pubblicato integralmente e il refresh termina con outcome `Failed` e recovery source `CurrentCatalog`. Se non esiste, il catalogo statico di emergenza e costruito integralmente dai fallback, senza riusare le sole letture dinamiche riuscite del tentativo fallito.

Il comportamento vale anche per errori dello snapshot store: uno store non disponibile non invalida mai il catalogo gia in memoria. In `EveryRequest`, `UseFallback` puo usare il catalogo globale corrente o uno snapshot compatibile; `Throw` rende invece osservabile l'errore al test/chiamante.

### 6.10 Persistenza e retention

Ogni catalogo dinamico modificato e validato viene salvato nello store senza bloccare le richieste che continuano a usare il catalogo corrente. Il tentativo di persistenza avviene dopo materializzazione completa e prima dell'atomic swap, cosi tutti gli eventi complementari precedono l'evento terminale. Viene atteso dal ciclo di refresh, ma un errore di scrittura non impedisce l'atomic swap di un candidato valido: il nuovo catalogo resta disponibile in memoria e viene emesso un warning strutturato dedicato. La modalita request-local `EveryRequest` non aggiorna il latest globale nello store.

Un refresh `Unchanged` non rimaterializza e non ripubblica il catalogo, ma rinnova source stamp, `LastValidatedAt` e scadenza del last-known-good. Questo evita che uno snapshot ancora confermato dalla sorgente scada soltanto perche il contenuto non cambia e consente di correlare prompt esterni modificati senza ricreare declaration. Un catalogo costruito esclusivamente dai fallback statici e marcato degraded e non sostituisce il last-known-good dinamico nello store.

Impostazioni iniziali per factory:

- `SnapshotRetention = 24 ore`;
- `MaxRetainedSnapshots = 10`, incluso il catalogo corrente;
- entrambi i valori sono configurabili e validati all'avvio;
- il catalogo corrente rimane recuperabile finche viene confermato da refresh riusciti;
- quando diventa storico, scade entro `SnapshotRetention` e non viene rinnovato dalle semplici letture;
- un resume oltre la retention applica `MissingVersionBehavior`.

`SnapshotRetention` governa snapshot persistiti e versioni storiche, non espelle il catalogo materializzato gia pubblicato in un processo attivo. Durante un outage prolungato quel processo continua a usare il current catalog e ogni refresh fallito resta osservabile. Dopo un restart, invece, uno snapshot store scaduto non puo essere recuperato e la catena passa ai fallback statici o all'errore.

La retention deve essere scelta in base al tempo massimo supportato per riprendere un'esecuzione interrotta, non alla durata totale della conversazione: i nuovi turni acquisiscono comunque il catalogo corrente.

Il manager mantiene sempre il riferimento materializzato corrente in memoria. Lo store distributed aggiunge recovery dopo restart e condivisione tra istanze, ma non entra nel normale hot path della richiesta. Viene interrogato durante startup recovery, version miss nella history locale o recovery di `EveryRequest`.

### 6.11 Limiti dello store distribuito

`IDistributedCache` non offre enumerazione, compare-and-swap o pruning atomico multi-key. L'adapter deve quindi mantenere un indice versionato per factory e applicare `MaxRetainedSnapshots` in modo best effort; la TTL derivata da `SnapshotRetention` resta il limite temporale effettivo anche in presenza di aggiornamenti concorrenti dell'indice.

Non viene aggiunto un distributed lock nella prima iterazione: aumenterebbe dipendenze e complessita per proteggere dati di recovery, mentre il catalogo operativo di ogni istanza rimane coerente grazie all'atomic swap locale. Una race nello store puo causare la perdita anticipata di una versione storica, mai la pubblicazione di un catalogo parziale; il successivo resume applica la policy di version miss gia definita.

Questo e un compromesso esplicito della scelta `IDistributedCache`. Applicazioni che richiedono retention storica forte possono fornire un `IRuntimeDescriptionSnapshotStore` custom con transazioni o primitive Redis native.

### 6.12 Sicurezza del recovery

Uno snapshot letto dallo store e input non trusted. Prima dell'uso devono essere verificati:

- versione del formato;
- completezza e limite dimensionale;
- ricalcolo di `ContentHash` e `CatalogId`;
- corrispondenza esatta del `TemplateHash`;
- presenza di tutti e soli gli identificatori previsti dal template corrente;
- scadenza rispetto a `SnapshotRetention`.

Snapshot corrotti, scaduti o incompatibili vengono ignorati con warning strutturato e non sono mai parzialmente recuperati. Cifratura, access control e protezione del contenuto nello store vengono approfonditi nel focus sicurezza del punto 8.

## 7) Disegno delle API pubbliche

### 7.1 Scene

Gli overload esistenti restano invariati:

```csharp
AddScene(string name, string description, Action<SceneBuilder> configure)
```

Nuovi overload:

```csharp
AddScene(
    string name,
    Func<RuntimeDescriptionContext, string> descriptionFactory,
    Action<SceneBuilder> configure,
    string? fallbackDescription = null)

AddScene(
    string name,
    Func<RuntimeDescriptionContext, CancellationToken, Task<string>> asyncDescriptionFactory,
    Action<SceneBuilder> configure,
    string? fallbackDescription = null)
```

`RuntimeDescriptionContext` espone almeno:

```csharp
public sealed class RuntimeDescriptionContext
{
    public required IServiceProvider Services { get; init; }
    public required RuntimeDescriptionRefreshReason Reason { get; init; }
}
```

`Services` appartiene a uno scope creato dal refresh coordinator e non allo scope della richiesta utente. `Reason` distingue startup, timer, change notification, manual refresh ed `EveryRequest` per diagnostica e implementazioni avanzate.

La stessa istanza di `RuntimeDescriptionContext` e lo stesso scope vengono passati a tutte le factory del tentativo. Un adapter applicativo puo quindi registrare uno scoped accessor che carica una sola volta un documento coerente e immutabile, per esempio un prompt snapshot, e restituisce da memoria le singole descrizioni. Lo scope non sopravvive al refresh e non viene conservato nel catalogo materializzato.

Il fallback serve per l'ultimo livello di recovery e per la discovery sincrona prima che esista uno snapshot valido. Se assente, PlayFramework prova comunque catalogo corrente e snapshot store; fallisce soltanto quando nessun livello produce un catalogo completo. Il comportamento della discovery resta separato e non attiva I/O.

### 7.2 Service tool

Mantenere l'overload statico e aggiungere:

```csharp
WithMethod<TResult>(
    Expression<Func<TService, TResult>> methodSelector,
    string toolName,
    Func<RuntimeDescriptionContext, string> descriptionFactory,
    string? fallbackDescription = null)

WithMethod<TResult>(
    Expression<Func<TService, TResult>> methodSelector,
    string toolName,
    Func<RuntimeDescriptionContext, CancellationToken, Task<string>> asyncDescriptionFactory,
    string? fallbackDescription = null)
```

### 7.3 Endpoint tool

Aggiungere gli overload sincroni e asincroni a entrambe le varianti:

- `WithAction<TResponse>`;
- `WithAction<TRequest, TResponse>`.

La descrizione generale del tool diventa dinamica. Route parameter, query parameter e request body schema rimangono statici.

### 7.4 Client interaction tool

Aggiungere overload equivalenti ai metodi del `ClientInteractionBuilder`. Nome, tipo di interazione, request schema e response schema rimangono statici.

### 7.5 Policy di errore

Evitare di moltiplicare parametri booleani negli overload. Introdurre impostazioni globali, eventualmente sovrascrivibili per descrizione:

```csharp
public enum RuntimeDescriptionFailureMode
{
    Throw,
    UseFallback
}

public enum RuntimeDescriptionSnapshotStoreMode
{
    Memory,
    Distributed
}

public sealed class RuntimeDescriptionSettings
{
    public RuntimeDescriptionFailureMode FailureMode { get; set; }
        = RuntimeDescriptionFailureMode.UseFallback;

    public bool RejectEmptyValues { get; set; } = true;

    public RuntimeDescriptionRefreshMode RefreshMode { get; set; }
        = RuntimeDescriptionRefreshMode.Background;

    public TimeSpan BackgroundRefreshInterval { get; set; }
        = TimeSpan.FromMinutes(5);

    public bool RefreshAtStartup { get; set; } = true;

    public bool RefreshOnChange { get; set; } = true;

    public RuntimeDescriptionConsistencyMode ConsistencyMode { get; set; }
        = RuntimeDescriptionConsistencyMode.Execution;

    public MissingRuntimeDescriptionVersionBehavior MissingVersionBehavior { get; set; }
        = MissingRuntimeDescriptionVersionBehavior.UseLatestAndWarn;

    public RuntimeDescriptionSnapshotStoreMode SnapshotStoreMode { get; set; }
        = RuntimeDescriptionSnapshotStoreMode.Memory;

    public TimeSpan SnapshotRetention { get; set; }
        = TimeSpan.FromHours(24);

    public int MaxRetainedSnapshots { get; set; } = 10;

    public int MaxDescriptionUtf8Bytes { get; set; } = 16 * 1024;

    public int MaxCatalogUtf8Bytes { get; set; } = 1024 * 1024;
}
```

`EveryRequest` e configurabile sul builder/DI dell'applicazione, non tramite payload o request setting controllabile dal client.

`Manual` e anch'essa una configurazione della factory e non puo essere selezionata dal client. `RefreshAtStartup` resta l'unica automazione opzionale della modalita; timer e change notification sono sempre inattivi.

`UseFallback` applica la catena current catalog, snapshot store e fallback statici definita nella sezione resilienza. Se nessun livello produce un catalogo completo deve comunque generare un errore chiaro, evitando di inviare al modello tool privi accidentalmente di descrizione.

`Throw` serve per applicazioni fail-closed e test di errore deterministici. Un refresh background fallito non elimina comunque un catalogo gia pubblicato: impedisce soltanto la pubblicazione del candidato. In startup ed `EveryRequest`, dove l'errore si trova nel percorso che deve acquisire il catalogo, `Throw` propaga invece il fallimento.

In modalita `Manual`, un errore operativo del provider o della validazione produce un `RuntimeDescriptionRefreshResult` con outcome `Failed`, mantiene il catalogo corrente e non viene convertito in successo attraverso la recovery chain, anche con `FailureMode.Throw`. La barriera amministrativa decide se aprire il batch in base al risultato. Cancellazione, opzioni invalide e violazioni del contratto di programmazione continuano invece a propagare un'eccezione. Questa distinzione rende il refresh awaitable e osservabile senza distruggere una versione corrente ancora valida.

`Distributed` richiede una registrazione esplicita di `IDistributedCache`; l'assenza del servizio e un errore di configurazione all'avvio e non causa un fallback silenzioso allo store memory.

### 7.6 Metadata avanzati della sorgente

Gli overload semplici continuano a restituire `string`. Per provider versionati aggiungere overload che restituiscono:

```csharp
public sealed record RuntimeDescriptionValue
{
    public required string Value { get; init; }
    public string? Version { get; init; }
    public string? Source { get; init; }
    public string? ETag { get; init; }
}
```

Firma asincrona avanzata:

```csharp
Func<RuntimeDescriptionContext,
    CancellationToken,
    Task<RuntimeDescriptionValue>>
```

`Version`, `Source` ed `ETag` sono metadata diagnostici e non sostituiscono l'identita calcolata dal framework. Gli overload `string` vengono adattati internamente a `RuntimeDescriptionValue` senza metadata sorgente.

`Version` puo rappresentare l'identita dello snapshot applicativo da cui e stato letto il valore. Lo stato di acquisizione espone una `SourceVersion` uniforme soltanto quando **ogni resolver dinamico** restituisce una versione non vuota e tutte le versioni coincidono. In presenza di valori mancanti o differenti, `SourceVersion` e `null` e `HasUniformSourceVersion` e `false`; il catalogo resta valido per non impedire applicazioni che combinano intenzionalmente sorgenti diverse.

## 8) Modello di configurazione interno

Introdurre un descrittore comune:

```csharp
internal sealed class RuntimeTextConfiguration
{
    public string? StaticValue { get; init; }
    public string? FallbackValue { get; init; }
    public Func<RuntimeDescriptionContext,
        CancellationToken,
        ValueTask<RuntimeDescriptionValue>>? Resolver { get; init; }

    public bool IsDynamic => Resolver is not null;
}
```

I builder normalizzano factory statiche, sincrone, asincrone e avanzate nell'unico `Resolver`, evitando branching duplicato nel catalog manager.

Applicazione prevista:

- `SceneConfiguration.Description` diventa un `RuntimeTextConfiguration` interno;
- `ServiceToolConfiguration.Description` usa lo stesso tipo;
- `EndpointToolConfiguration.Description` usa lo stesso tipo;
- le definizioni dei client tool conservano un fallback serializzabile e una factory separata non serializzabile.

Il catalog manager deve impedire configurazioni ambigue e validare i metadata restituiti dalla sorgente senza considerarli trusted per l'identita del catalogo.

## 9) Catalogo globale materializzato

### 9.1 Template statico

Il template costruito all'avvio contiene gli elementi che non cambiano con le descrizioni:

- nome normalizzato di scena e tool;
- executor `ISceneTool`;
- `MethodInfo` e target del service tool;
- JSON Schema di input;
- eventuale return schema;
- route e metadati HTTP;
- configurazione client interaction;
- riferimenti MCP.

Reflection e generazione degli schema avvengono una sola volta. Lo schema risultante viene conservato nel template e riutilizzato nelle versioni successive.

### 9.2 Catalogo materializzato

Ogni versione valida produce un catalogo completo e immutabile:

- `MaterializedSceneCatalog`;
- `MaterializedScene`;
- `MaterializedSceneTool`.

Il catalogo contiene:

- descrizioni risolte;
- declaration di routing delle scene;
- declaration di tutti i tool locali;
- riferimenti agli executor statici;
- identita composita del catalogo;
- versione, source ed ETag dichiarati dalla sorgente, quando disponibili;
- timestamp e refresh reason.

```csharp
public sealed record RuntimeDescriptionCatalogIdentity
{
    public required string CatalogId { get; init; }
    public required string TemplateHash { get; init; }
    public required string ContentHash { get; init; }
    public required string HashAlgorithm { get; init; }

    public string? SourceVersion { get; init; }
    public bool HasUniformSourceVersion { get; init; }
    public string? Source { get; init; }

    public DateTimeOffset LoadedAt { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
}
```

`RuntimeDescriptionCatalogIdentity.SourceVersion` descrive la sorgente osservata quando il catalogo e stato materializzato e resta immutabile con il contenuto. La source version piu recente validata, necessaria per correlare actor o altri prompt esterni al catalogo, appartiene invece a `RuntimeDescriptionExecutionInfo` e al published-state wrapper.

Il catalogo viene costruito interamente durante refresh iniziale, background, manuale o `EveryRequest`. Non sono previsti livelli `SceneDescriptions`, `SceneWithTools` o `FullCatalog`: con metadata globali e refresh fuori dal percorso critico, la materializzazione lazy aumenterebbe cache, sincronizzazione e percorsi di codice senza un beneficio sufficiente.

`MaterializedScene` implementa l'attuale `IScene`; `MaterializedSceneTool` implementa l'attuale `ISceneTool` e delega `ExecuteAsync` al tool statico del template. In questo modo cambiano description e declaration, mentre executor, actor, MCP e firma di `ISceneExecutor` restano invariati.

Il riferimento atomico del manager punta a un wrapper immutabile leggero:

```csharp
internal sealed record PublishedRuntimeDescriptionState(
    MaterializedSceneCatalog Catalog,
    string? SourceVersion,
    bool HasUniformSourceVersion,
    Guid LastValidationOperationId,
    DateTimeOffset LastValidatedAt,
    RuntimeDescriptionRecoverySource RecoverySource,
    bool UsedFallback);
```

Un refresh `Changed` pubblica un nuovo catalogo e un nuovo wrapper. Un refresh `Unchanged` riusa esattamente lo stesso `MaterializedSceneCatalog` e le stesse declaration, ma puo sostituire atomicamente il solo wrapper per aggiornare source version uniforme, validation operation, `LastValidatedAt` e stato di recovery. Questo permette di correlare actor modificati a uno snapshot applicativo piu recente anche quando le scene/tool descriptions hanno lo stesso contenuto.

### 9.3 Componenti runtime minimi

```text
RuntimeDescriptionCatalogManager (singleton per factory)
├── RuntimeTextConfiguration
├── refresh lock
├── source invocation
├── validation
├── canonical hash
├── materialization
├── current published-state reference
├── atomic publication
├── IRuntimeDescriptionSnapshotStore
└── IRuntimeDescriptionRefresher

RuntimeDescriptionBackgroundService
├── startup trigger
├── periodic timer
├── change notification
└── CatalogManager.RefreshIfIdleAsync
```

Hashing, validazione e materializzazione restano metodi interni del manager. Possono essere estratti in helper puri per i test, ma non devono diventare servizi DI o nuovi livelli architetturali senza una necessita dimostrata. Lo snapshot store e un adapter di persistenza sostituibile e non costituisce un terzo coordinator.

### 9.4 Aggancio alla richiesta

`SceneManager` acquisisce il catalogo una sola volta e lo assegna a una proprieta tipizzata interna di `SceneContext`:

```csharp
internal MaterializedSceneCatalog MaterializedRuntimeSceneCatalog
    { get; set; } = null!;
```

Espone inoltre una vista pubblica read-only della sola identita di esecuzione:

```csharp
public sealed record RuntimeDescriptionExecutionInfo
{
    public required RuntimeDescriptionCatalogIdentity CatalogIdentity { get; init; }
    public string? RequestedCatalogId { get; init; }
    public string? SourceVersion { get; init; }
    public required bool HasUniformSourceVersion { get; init; }
    public required RuntimeDescriptionRefreshMode RefreshMode { get; init; }
    public required RuntimeDescriptionConsistencyMode ConsistencyMode { get; init; }
    public required bool IsRequestLocal { get; init; }
    public required RuntimeDescriptionRecoverySource RecoverySource { get; init; }
    public required bool UsedFallback { get; init; }
    public required Guid LastValidationOperationId { get; init; }
    public required DateTimeOffset LastValidatedAt { get; init; }
    public required TimeSpan AcquisitionDuration { get; init; }
}

public RuntimeDescriptionExecutionInfo RuntimeDescriptions
    { get; internal set; } = null!;
```

`RuntimeDescriptions` consente ad actor, adapter applicativi e instrumentation di correlare la richiesta con il catalogo acquisito senza esporre scene, tool, description, declaration o executor. In particolare, un actor puo leggere `RuntimeDescriptions.SourceVersion` e chiedere al proprio provider lo snapshot applicativo esatto quando `HasUniformSourceVersion` e `true`.

La source version dell'execution info rappresenta l'ultimo snapshot applicativo uniformemente validato contro quel contenuto. Puo quindi essere piu recente della `SourceVersion` diagnostica conservata nell'identita al momento della materializzazione: se cambia soltanto un actor, il refresh delle descriptions termina `Unchanged`, mantiene lo stesso `CatalogId` e le stesse declaration, ma aggiorna atomicamente source stamp, operation ID e `LastValidatedAt` acquisiti dalle richieste successive. `RecoverySource` e `UsedFallback` rendono esplicito se l'identita deriva dalla sorgente attesa o da un livello di resilienza; `AcquisitionDuration` misura il solo costo sostenuto dalla richiesta per acquisire o costruire lo stato.

Questa informazione e una correlazione disponibile agli actor, non una prova che sia stata rispettata. PlayFramework non puo conoscere la sorgente effettivamente usata da una factory actor arbitraria e non persiste un `ActorSnapshotId`. Un'applicazione che necessita tale evidenza deve instrumentare il proprio provider actor.

Il catalogo non deve essere inserito in `SceneContext.Properties`: tale dizionario partecipa allo stato di esecuzione e puo essere persistito, mentre il catalogo contiene declaration e riferimenti agli executor che non sono serializzabili.

`ExecutionState.FromContext` salva `context.MaterializedRuntimeSceneCatalog.Identity.CatalogId`; `ApplyToContext` rende disponibile il `CatalogId` richiesto dal resume senza tentare di serializzare il catalogo materializzato. Anche `RuntimeDescriptions` non viene persistito integralmente: viene ricostruito a ogni richiesta a partire dal catalogo effettivamente acquisito.

Il catalogo materializzato con executor resta interno. `SceneContext` espone anche una proiezione pubblica, immutabile e priva di executor, destinata ai custom planner:

```csharp
public sealed record RuntimeSceneCatalogView
{
    public required RuntimeDescriptionExecutionInfo ExecutionInfo { get; init; }
    public required IReadOnlyList<RuntimeSceneDescriptor> Scenes { get; init; }
}

public sealed record RuntimeSceneDescriptor
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required AIFunctionDeclaration RoutingDeclaration { get; init; }
    public required IReadOnlyList<RuntimeToolDescriptor> Tools { get; init; }
}

public sealed record RuntimeToolDescriptor
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required AIFunctionDeclaration Declaration { get; init; }
}

public RuntimeSceneCatalogView RuntimeSceneCatalog
    { get; internal set; } = null!;
```

Le collection sono costruite con backing realmente immutabile e le declaration sono le stesse istanze read-only usate dalla richiesta. Non vengono esposti delegate, `IServiceProvider`, `ISceneTool` o executor. La vista e sincrona perche la risoluzione asincrona e gia terminata prima dell'invocazione del planner; non promette una rilettura della sorgente.

La stessa execution info viene propagata in modo tipizzato nella risposta:

```csharp
public sealed class AiSceneResponse
{
    // Proprieta esistenti omesse.
    public RuntimeDescriptionExecutionInfo? RuntimeDescriptions { get; init; }
}
```

Ogni risposta creata dopo l'acquisizione, incluse quelle intermedie e terminali, riceve la medesima istanza immutabile. Risposte prodotte prima dell'acquisizione, per esempio `Initializing` o `Unauthorized`, possono avere `null`. Una risposta terminale di un'esecuzione che ha acquisito il catalogo deve sempre valorizzarla. Il dizionario generico `Metadata` rimane disponibile ma non viene usato come contratto stabile per questa prova funzionale.

### 9.5 Compatibilita con `ISceneFactory`

Non modificare `ISceneFactory`, per evitare una breaking change per implementazioni custom. Le sue proprieta sincrone continuano a rappresentare il template/static fallback costruito all'avvio e sono adatte a configurazione, discovery e test di copertura, non a provare i metadata effettivamente usati da una richiesta.

I componenti standard non devono piu usare direttamente `ISceneFactory.ScenesAsAiTool` come catalogo effettivo della richiesta. Il planner standard e i planner custom che necessitano dei valori runtime leggono `SceneContext.RuntimeSceneCatalog`. La parte sincrona esistente non viene eliminata: viene mantenuta con una semantica piu precisa, mentre il nuovo accesso sincrono request-local e corretto perche legge dati gia materializzati.

## 10) Flusso di esecuzione

### 10.1 Inizializzazione della richiesta

1. `SceneManager` costruisce il `SceneContext` minimo.
2. Esegue autenticazione, autorizzazione e caricamento di cache/repository necessario a conoscere eventuale stato di resume.
3. Verifica l'accesso alla conversazione privata; richieste non autorizzate non acquisiscono il catalogo.
4. In consistency mode `Execution`, se esiste un `RestoredExecutionState`, legge il `CatalogId` pinned.
5. In consistency mode `Request`, ignora l'eventuale `CatalogId` pinned e usa sempre il catalogo corrente.
6. In refresh mode `Background` o `Manual`, acquisisce il catalogo globale richiesto o corrente senza attivare refresh impliciti.
7. In refresh mode `EveryRequest`, forza una singola risoluzione e materializzazione completa prima di acquisire il riferimento request-local.
8. Se la versione pinned non e disponibile, applica `MissingVersionBehavior`.
9. Assegna `context.MaterializedRuntimeSceneCatalog` e costruisce `context.RuntimeDescriptions`.
10. Costruisce la vista pubblica `context.RuntimeSceneCatalog` dallo stesso stato immutabile.
11. Soltanto dopo l'acquisizione inizializza il contesto dinamico ed esegue i MainActor necessari al nuovo contesto.
12. Actor di scena e tutti gli execution mode osservano la stessa identita per l'intera richiesta.
13. `ResponseHelper` copia la stessa `RuntimeDescriptionExecutionInfo` in ogni `AiSceneResponse` prodotta dopo l'acquisizione.

Direct, Scene, Planning, Dynamic Chaining e `SceneExecutor` non risolvono il manager e non conoscono refresh mode, provider, fallback, hashing o atomic swap.

### 10.2 Direct mode

1. Leggere le scene dal catalogo materializzato.
2. Passare le declaration di routing gia costruite a `ChatOptions.Tools`.
3. Dopo la selezione, recuperare la scena materializzata per nome.
4. Eseguire la scena usando executor statici e declaration della stessa versione.

### 10.3 Planning mode

1. Il planner riceve `SceneContext` e legge la vista request-local del catalogo scelto dalla richiesta.
2. `BuildSystemPrompt` elenca scene e tool usando esclusivamente tale catalogo.
3. Le fasi di esecuzione del piano riutilizzano la stessa versione.

In modalita `Background` e `Manual`, Planning non causa letture della sorgente o materializzazione aggiuntiva. In `EveryRequest`, il costo completo viene sostenuto prima della chiamata di planning.

### 10.4 Dynamic Chaining

1. Usare le scene gia materializzate per la selezione.
2. Recuperare la scena selezionata dallo stesso catalogo.
3. Riutilizzare catalogo e declaration nei round successivi.
4. Non rileggere la sorgente a ogni round, anche se il valore remoto cambia durante la richiesta.

### 10.5 Scene execution e tool calling loop

1. `SceneExecutor` riceve o recupera la `MaterializedScene`.
2. `ChatOptions.Tools` usa le declaration della `MaterializedScene`.
3. `ToolExecutionManager` continua a individuare l'executor attraverso il nome statico.
4. Le iterazioni successive riutilizzano gli stessi oggetti materializzati.

### 10.6 MCP

I tool MCP continuano a essere caricati dal server durante l'esecuzione. Non devono essere convertiti al nuovo modello nella prima iterazione. Il catalogo materializzato combina:

- tool locali con descrizioni risolte;
- tool MCP restituiti dal server.

### 10.7 Discovery API

La discovery non deve causare I/O o attivare un refresh. Nella prima iterazione legge il catalogo globale pubblicato e restituisce:

- descrizione e versione dello snapshot globale corrente, se disponibile;
- fallback statico, se il caricamento iniziale non ha ancora prodotto uno snapshot valido e la policy consente l'avvio degradato;
- un indicatore `isRuntimeResolved` e, se compatibile con il DTO pubblico, `runtimeDescriptionVersion`.

La modalita `EveryRequest` non modifica la discovery, che continua a mostrare il catalogo globale e non lo snapshot locale di una specifica esecuzione.

Discovery descrive lo stato globale disponibile e non costituisce prova del catalogo realmente usato da una richiesta, in particolare con `EveryRequest`, version pin o recovery. Tale prova e `AiSceneResponse.RuntimeDescriptions`; telemetria e discovery restano strumenti operativi soggetti rispettivamente a sampling e diversa semantica temporale.

## 11) Declaration, executor e identita della versione

### 11.1 Decisione declaration-only

Scene routing e tutti i tool locali vengono esposti al modello come `AIFunctionDeclaration`:

```csharp
AIFunctionFactory.CreateDeclaration(
    template.Name,
    resolvedDescription,
    template.JsonSchema,
    template.ReturnJsonSchema);
```

L'esecuzione rimane separata:

- service tool tramite `ServiceMethodTool.ExecuteAsync`;
- endpoint tool tramite `EndpointHttpTool.ExecuteAsync`;
- client interaction tramite il relativo handler;
- scene routing tramite gli execution handler.

`ISceneTool.ToolDescription` resta di tipo `AITool`, evitando una breaking change; l'istanza concreta e una `AIFunctionDeclaration`. Gli adapter `IChatClient` devono quindi supportare `AIFunctionDeclaration` e non filtrare esclusivamente `AIFunction`.

L'adapter Azure usato nell'infrastruttura di test deve essere aggiornato in questo senso. Tale correzione e necessaria anche per endpoint e client tool che producono gia declaration.

I tool MCP restano invariati nella prima iterazione e continuano a usare gli `AIFunction` caricati dal server manager.

### 11.2 Materializzazione completa

Per ogni nuova versione il coordinator:

1. legge tutte le descrizioni;
2. valida il catalogo completo;
3. calcola l'identita del contenuto;
4. crea tutte le scene routing declaration;
5. crea tutte le local tool declaration riusando gli schema statici;
6. pubblica il catalogo con atomic swap.

Le richieste in modalita `Background` e `Manual` non creano declaration. `EveryRequest` ripete intenzionalmente la materializzazione completa, ma continua a riusare schema ed executor statici.

### 11.3 Identita composita e hash canonici

Il framework calcola tre identificatori distinti:

```text
TemplateHash = hash del contratto statico
ContentHash  = hash delle descrizioni dinamiche
CatalogId    = hash(HashAlgorithm + TemplateHash + ContentHash)
```

`TemplateHash` include in ordine deterministico:

- factory PlayFramework;
- scene e tool name normalizzati;
- tipo/source dei tool;
- JSON Schema e return schema;
- firme e metadata statici necessari a determinare compatibilita.

`ContentHash` include:

- scene name e scene description;
- tool name e tool description;
- eventuali altri metadata dinamici introdotti in futuro.

Gli hash non includono timestamp, refresh reason, ETag o source version. La serializzazione canonica usa UTF-8, ordine stabile, separatori non ambigui e una versione esplicita dell'algoritmo, inizialmente `sha256-v1`.

`CatalogId` e l'identita persistita in `ExecutionState` e usata nello store delle versioni. Se il `CatalogId` coincide con quello corrente:

- non vengono create nuove declaration;
- non viene creato o pubblicato un nuovo `MaterializedSceneCatalog`;
- il lightweight published-state wrapper viene sostituito atomicamente soltanto per aggiornare source stamp e `LastValidatedAt`;
- il refresh termina con outcome `Unchanged`;
- durata, metadata sorgente e risultato vengono comunque osservati.

Version, Source ed ETag dichiarati dalla sorgente non sono usati come identita effettiva: questa scelta evita dipendenze dalla correttezza del versionamento esterno e permette l'uso di file o provider semplici.

### 11.4 Template mismatch

Uno snapshot storico puo essere rimaterializzato soltanto se il suo `TemplateHash` coincide con quello dell'applicazione corrente. Se differisce, vecchie descrizioni e nuovo template non devono essere combinati.

Il mismatch applica `MissingVersionBehavior` con un evento specifico:

- `UseLatestAndWarn`: usa integralmente il catalogo corrente;
- `Throw`: interrompe prima della chiamata LLM.

### 11.5 Fast path statico

Le configurazioni completamente statiche continuano a riusare il catalogo costruito all'avvio e non attivano background coordinator, hashing o materializzazioni successive.

### 11.6 Valutazione della latenza

La latenza deve essere misurata separando quattro percorsi:

1. **Hot path `Background`.** Include soltanto lettura atomica del riferimento e lookup nel catalogo gia materializzato. Non comprende I/O, hashing, reflection o creazione di declaration.
2. **Refresh background.** Comprende lettura sorgente, validazione, hashing e, soltanto per contenuto modificato, materializzazione completa. Non blocca le richieste sul catalogo corrente.
3. **Startup refresh.** Puo influenzare il tempo di readiness se l'avvio richiede un catalogo valido. La startup/fallback policy viene definita nel punto resilienza.
4. **`EveryRequest`.** Include intenzionalmente sorgente, validazione, hashing e materializzazione nel percorso della richiesta; non e soggetto agli SLO di produzione della modalita `Background`.

La telemetria deve distinguere almeno:

- `source_duration_ms`;
- `validation_duration_ms`;
- `hash_duration_ms`;
- `materialization_duration_ms`;
- `publication_duration_ms`;
- `snapshot_store_duration_ms`;
- numero di scene e tool;
- outcome `Changed`, `Unchanged`, `Failed` o `SkippedBusy`;
- recovery source `None`, `CurrentCatalog`, `SnapshotStore` o `StaticFallback`;
- `used_fallback` e numero di fallback separati dall'outcome;
- esito di lettura, scrittura, rinnovo e validazione dello snapshot store.

Quality target iniziali, da confermare con la baseline:

- hot path `Background`: nessuna allocazione proporzionale al numero di scene/tool e regressione p95 non superiore a 1 ms rispetto al catalogo statico;
- refresh `Unchanged`: zero nuove declaration;
- refresh `Changed`: costo lineare rispetto al numero di scene e tool e nessun blocco delle richieste correnti;
- nessun refresh sovrapposto per la stessa factory PlayFramework: timer e change notification saltano il ciclo se il lock e occupato, mentre il refresh manuale attende e avvia un nuovo ciclo;
- `EveryRequest`: latenza registrata e verificata nei test, senza target di produzione.

Il limite di 1 ms e un quality target, non un'assunzione progettuale: se la baseline mostra che e troppo permissivo o irrealistico verra sostituito da una soglia relativa documentata.

La baseline deve confrontare resolver sequenziali che effettuano I/O individuale con il pattern raccomandato di scoped snapshot accessor. L'obiettivo architetturale e una sola lettura remota per refresh; la libreria non parallelizza delegate applicativi arbitrari per compensare provider progettati come lookup remoti per chiave.

## 12) Error handling

Il refresh coordinator e il materializer devono distinguere:

- cancellazione richiesta: propagare `OperationCanceledException`;
- factory che restituisce `null`, stringa vuota o whitespace: errore di validazione;
- errore totale o parziale del provider con catalogo corrente: mantenere integralmente il corrente ed emettere warning;
- errore del provider senza catalogo corrente: provare snapshot store, poi catalogo statico di fallback;
- fallback statici incompleti: eccezione con scene/tool name e factory name mancanti;
- descrizione oltre `MaxDescriptionUtf8Bytes` o catalogo oltre `MaxCatalogUtf8Bytes`: rifiutare integralmente il candidato; i default sono rispettivamente 16 KiB per valore e 1 MiB per catalogo;
- errore di materializzazione `AITool`: includere nome e tipo di tool senza loggare contenuti sensibili;
- snapshot store non disponibile durante un refresh con candidato valido: pubblicare in memoria ed emettere warning di persistenza fallita;
- snapshot store non disponibile durante recovery: continuare con il livello successivo della catena;
- snapshot scaduto, corrotto o con hash invalido: rifiutare integralmente ed emettere warning;
- versione pinned disponibile: usare esattamente il catalogo richiesto;
- versione pinned non disponibile con `UseLatestAndWarn`: usare il catalogo corrente ed emettere warning/metric/event;
- versione pinned non disponibile con `Throw`: interrompere prima della chiamata LLM con errore esplicito;
- snapshot storico con `TemplateHash` differente: non combinarlo mai con il template corrente e applicare `MissingVersionBehavior` con evento `template_mismatch`.

La cancellazione esplicita dell'operazione non attiva recovery automatico: viene propagata. Timeout ed eccezioni ordinarie della sorgente seguono invece `FailureMode`.

Null, empty, whitespace, Unicode non valido, carattere NUL e superamento dei limiti sono errori strutturali. Il framework non normalizza semanticamente il testo e non rimuove newline o spazi significativi: validazione, conteggio e hash operano sulla rappresentazione UTF-8 esatta accettata. In modalita `Manual` gli errori operativi sono restituiti come outcome `Failed`, cosi il chiamante puo applicare una barriera fail-closed verificabile; il catalogo corrente resta disponibile ma non rende riuscita l'operazione.

Non devono essere eseguiti retry automatici nel core: il provider e il posto corretto per timeout, retry e circuit breaker, evitando moltiplicazioni non controllate delle chiamate. Lo snapshot store e un livello di continuita operativa, non un meccanismo di retry.

## 13) Telemetria e diagnostica

Aggiungere activity/eventi e metriche aggregate:

- durata totale della risoluzione;
- numero di descrizioni richieste;
- letture dello snapshot globale e relativa versione;
- refresh background, manuali ed `EveryRequest` distinti;
- consistency mode `Request` o `Execution`;
- `CatalogId` richiesto e utilizzato;
- version miss e relativo comportamento;
- uso del fast path statico;
- fallback utilizzati;
- errori di risoluzione;
- sorgente logica o versione, se restituita dal provider applicativo.

Tag suggeriti:

- `playframework.runtime_metadata.scope`;
- `playframework.runtime_metadata.refresh_reason`;
- `playframework.runtime_metadata.consistency_mode`;
- `playframework.runtime_metadata.version`;
- `playframework.runtime_metadata.requested_catalog_id`;
- `playframework.runtime_metadata.used_catalog_id`;
- `playframework.runtime_metadata.version_miss`;
- `playframework.runtime_metadata.recovery_source`;
- `playframework.runtime_metadata.snapshot_store_operation`;
- `playframework.runtime_metadata.items`;
- `playframework.runtime_metadata.fallbacks`;
- `playframework.runtime_metadata.acquisition_duration_ms`;
- `playframework.runtime_metadata.duration_ms`.

Il contenuto delle descrizioni non deve essere registrato per default. Puo contenere istruzioni riservate o informazioni applicative sensibili.

### 13.1 Risultato strutturato del refresh

```csharp
public enum RuntimeDescriptionRefreshOutcome
{
    Changed,
    Unchanged,
    Failed,
    SkippedBusy
}

public enum RuntimeDescriptionRecoverySource
{
    None,
    CurrentCatalog,
    SnapshotStore,
    StaticFallback
}

public enum RuntimeDescriptionSnapshotStoreOutcome
{
    NotAttempted,
    Succeeded,
    Failed,
    Rejected
}

public sealed record RuntimeDescriptionRefreshResult
{
    public required Guid OperationId { get; init; }
    public required RuntimeDescriptionRefreshOutcome Outcome { get; init; }

    public string? PreviousCatalogId { get; init; }
    public string? CurrentCatalogId { get; init; }
    public RuntimeDescriptionCatalogIdentity? CatalogIdentity { get; init; }
    public string? TemplateHash { get; init; }
    public string? SourceVersion { get; init; }
    public bool HasUniformSourceVersion { get; init; }
    public DateTimeOffset? LastValidatedAt { get; init; }

    public RuntimeDescriptionRecoverySource RecoverySource { get; init; }
    public RuntimeDescriptionSnapshotStoreOutcome SnapshotStoreOutcome { get; init; }

    public int ChangedItemCount { get; init; }
    public int FallbackItemCount { get; init; }
    public bool UsedFallback => RecoverySource is not RuntimeDescriptionRecoverySource.None;

    public TimeSpan SourceDuration { get; init; }
    public TimeSpan ValidationDuration { get; init; }
    public TimeSpan HashDuration { get; init; }
    public TimeSpan MaterializationDuration { get; init; }
    public TimeSpan PublicationDuration { get; init; }
    public TimeSpan SnapshotStoreDuration { get; init; }
}
```

Il risultato viene restituito dal refresh manuale ed e la base comune per log strutturati, `ActivityEvent` e metriche.

### 13.2 Ciclo di eventi osservabile

Ogni trigger usa un `OperationId` correlabile. Il ciclo distingue la ricezione del trigger, la rilevazione di una variazione e la sua effettiva pubblicazione, in modo da non confondere una notifica ricevuta con un cambiamento gia applicato.

Eventi stabili:

1. `playframework.runtime_metadata.refresh_triggered`: il trigger startup, timer, change notification, manuale o `EveryRequest` e stato ricevuto dall'istanza.
2. `playframework.runtime_metadata.change_detected`: evento emesso soltanto dopo che il confronto canonico ha confermato una variazione semantica, indipendentemente dal tipo di trigger. Una change notification ricevuta ma non confermata termina quindi con `catalog_unchanged`, non con `change_detected`.
3. `playframework.runtime_metadata.refresh_started`: il manager ha acquisito il lock e ha iniziato la lettura. Non viene emesso per un trigger `SkippedBusy`.
4. Uno e uno solo degli eventi terminali per ogni `OperationId`:
   - `playframework.runtime_metadata.catalog_published`;
   - `playframework.runtime_metadata.catalog_unchanged`;
   - `playframework.runtime_metadata.refresh_failed`;
   - `playframework.runtime_metadata.refresh_skipped_busy`.
5. Eventi complementari, quando applicabili:
   - `playframework.runtime_metadata.source_resolution_failed`;
   - `playframework.runtime_metadata.current_catalog_retained`;
   - `playframework.runtime_metadata.snapshot_recovered`;
   - `playframework.runtime_metadata.snapshot_persisted`;
   - `playframework.runtime_metadata.snapshot_store_write_failed`;
   - `playframework.runtime_metadata.snapshot_rejected`;
   - `playframework.runtime_metadata.fallback_used`;
   - `playframework.runtime_metadata.pinned_catalog_miss`;
   - `playframework.runtime_metadata.template_mismatch`.

`catalog_published` viene emesso **soltanto dopo il completamento dell'atomic swap**. La sua presenza certifica che quella istanza ha recepito e reso disponibile il nuovo catalogo. `catalog_unchanged` certifica che il trigger e stato elaborato ma non ha prodotto una modifica semantica.

`source_resolution_failed`, `current_catalog_retained`, `snapshot_recovered`, `snapshot_store_write_failed`, `snapshot_rejected`, `fallback_used`, `pinned_catalog_miss` e `template_mismatch` vengono emessi come warning strutturati. `snapshot_persisted` e informativo. Nessun recovery viene quindi eseguito silenziosamente.

Se la sorgente fallisce ma uno snapshot o i fallback statici consentono di pubblicare un catalogo completo, l'operazione termina con `catalog_published` e mantiene `source_resolution_failed` come evento complementare. Se viene semplicemente conservato il catalogo corrente, termina con `refresh_failed` e `current_catalog_retained`: non viene emesso un falso `catalog_unchanged`.

`refresh_skipped_busy` e terminale per il singolo trigger ignorato. La successiva esecuzione periodica produrra una nuova operation correlabile alla source version corrente. La relazione `refresh_triggered` + singolo outcome terminale consente inoltre di rilevare operazioni perse o incomplete nell'exporter di telemetria.

### 13.3 Schema comune degli eventi

Ogni evento contiene, quando disponibile:

- `operation_id`;
- factory PlayFramework;
- refresh reason;
- source logica e source version;
- indicatore bounded `source_version_uniform`;
- previous, candidate e current `CatalogId`;
- `TemplateHash`;
- outcome e failure stage;
- numero totale di scene e tool;
- changed item count;
- fallback item count;
- recovery source;
- snapshot store operation e outcome;
- durate per stage;
- consistency e refresh mode.

`change_detected` include il `candidate_catalog_id` e il conteggio degli elementi modificati; `catalog_published` ripete lo stesso identificatore come `current_catalog_id`. Questa coppia consente di correlare il cambiamento validato con quello effettivamente applicato.

Identita dell'istanza, deployment ed environment devono provenire dalle resource attributes OpenTelemetry, evitando duplicazioni nei tag PlayFramework. Per verificare la propagazione multi-istanza si aggregano gli eventi `catalog_published` per source version/CatalogId e `service.instance.id`.

`CatalogId`, source version e `OperationId` appartengono a log strutturati e activity event, non alle label delle metriche aggregate: evitano cosi cardinalita non limitata nel backend delle metriche. Le metriche usano soltanto dimensioni bounded, come outcome, refresh reason, consistency mode, recovery source, snapshot store outcome e failure stage.

### 13.4 Privacy della telemetria

Per default non vengono emessi:

- testo delle descrizioni;
- valore precedente o successivo;
- URI, credenziali o connection string della sorgente;
- elenco dei tool o delle scene modificate;
- ETag non validati.

Gli identificatori degli elementi modificati possono essere abilitati esplicitamente solo a livello debug. In produzione restano disponibili conteggi, `CatalogId`, source version e outcome, sufficienti per confermare ricezione e pubblicazione del cambiamento.

### 13.5 Persistenza dell'audit

PlayFramework non introduce un audit database separato. Gli eventi strutturati vengono esportati attraverso l'observability configurata dall'host. Lo snapshot store memory/Redis e un meccanismo operativo di recovery soggetto a retention, pruning e possibili race; non e un archivio di evaluation e non offre da solo una garanzia di riproducibilita a lungo termine.

Hash e telemetria identificano una versione ma, da soli, non possono ricostruirne il contenuto. Un consumer che richiede riproducibilita completa deve conservare un proprio artefatto immutabile del prompt effettivo, con access control e retention adeguati al dominio.

### 13.6 Sicurezza e trust boundary

Le runtime descriptions sono configurazione privilegiata assimilabile a istruzioni di sistema. Il core applica garanzie strutturali, senza pretendere di certificare la sicurezza semantica del testo:

- resolver e refresh sono configurati dall'host; payload utente, tenant ID, conversation metadata e request settings non possono scegliere sorgente, candidate o catalogo;
- il refresh manuale e un servizio amministrativo e l'eventuale endpoint che lo espone deve essere autenticato, autorizzato, rate limited e auditato dall'applicazione;
- sorgente e snapshot store usano credenziali a privilegio minimo, separando permessi di lettura e scrittura dove il provider lo consente;
- cifratura in transito e at rest dipendono dal provider/store scelto e devono essere validate nella configurazione di deployment;
- le chiavi cache sono namespaced almeno per applicazione, environment, factory e versione dell'algoritmo; non contengono descrizioni in chiaro;
- `MaxDescriptionUtf8Bytes` e `MaxCatalogUtf8Bytes` sono hard limit validati sia sulla sorgente sia sui payload recuperati dallo store;
- gli snapshot recuperati vengono ricalcolati e confrontati con hash, template e formato attesi prima della rimaterializzazione;
- descrizioni, diff, payload dello store, URI sensibili e segreti non entrano in log, activity o metriche;
- `CatalogId` e hash attestano uguaglianza/integrita rispetto ai dati letti, non approvazione, provenienza trusted o assenza di prompt injection.

La validazione semantica di prompt injection, escalation delle istruzioni, disclosure, tool misuse e policy di dominio resta responsabilita del source owner e dell'applicazione. Non viene introdotto in v1 un `IRuntimeDescriptionSemanticValidator`: un hook generico creerebbe una falsa garanzia, aumenterebbe il numero di extension point e non potrebbe conoscere policy, actor, modello e tool dell'host. TimeVision puo eseguire lint, approval workflow e fixture avversariali prima di aggiornare la sorgente e prima di aprire la barriera del batch.

## 14) Piano di implementazione

### Fase 0 - Contratto e baseline

- Confermare lo scope: descrizioni principali, non schema o nomi.
- Definire semantica di fallback, discovery e resume.
- Caratterizzare il riuso di `InitialContext` e MainActor tra turni caricati da cache/repository.
- Documentare la consistenza eventuale tra MainActor storico e catalogo corrente.
- Aggiungere test di caratterizzazione per comportamento statico corrente.
- Misurare allocazioni e latenza della costruzione attuale di scene/tool.

Deliverable:

- contratto approvato;
- baseline funzionale e prestazionale.

### Fase 1 - Modello di configurazione e overload

- Introdurre `RuntimeTextConfiguration`.
- Introdurre `RuntimeDescriptionContext` e `RuntimeDescriptionRefreshReason`.
- Introdurre `RuntimeDescriptionValue` e overload avanzati.
- Introdurre `RuntimeDescriptionExecutionInfo` come vista pubblica della sola identita acquisita.
- Introdurre `RuntimeSceneCatalogView` e descriptor read-only privi di executor per i planner custom.
- Estendere `AiSceneResponse` con `RuntimeDescriptions` tipizzato e nullable per le risposte pre-acquisizione.
- Normalizzare tutti gli overload in un solo resolver interno.
- Adattare `SceneConfiguration` e configurazioni tool.
- Implementare overload sincroni e asincroni.
- Conservare invariati gli overload statici.
- Validare combinazioni di configurazione non ammesse.
- Aggiungere `RuntimeDescriptionConsistencyMode` e `MissingRuntimeDescriptionVersionBehavior` alle settings.

Deliverable:

- API compilabile e retrocompatibile, ancora non collegata ai runtime consumer.

### Fase 2 - Coordinator e catalogo materializzato

- Implementare template statico con schema ed executor riutilizzabili.
- Implementare `RuntimeDescriptionCatalogManager` singleton per factory.
- Implementare `RuntimeDescriptionBackgroundService` come adapter sottile.
- Implementare background coordinator e atomic publication.
- Implementare `PublishedRuntimeDescriptionState` per acquisire atomicamente catalogo e source stamp.
- Creare un solo scope e un solo `RuntimeDescriptionContext` per refresh.
- Eseguire i resolver sequenzialmente in ordine deterministico.
- Implementare refresh iniziale, periodico, notificato e manuale.
- Implementare refresh mode `Manual` senza timer o change subscription e con startup refresh opzionale.
- Implementare modalita `EveryRequest`.
- Implementare materializzazione completa tramite `AIFunctionDeclaration`.
- Implementare versione opzionale e hash canonico.
- Implementare `TemplateHash`, `ContentHash`, `CatalogId` e versionamento `sha256-v1`.
- Implementare rilevamento e policy per template mismatch.
- Saltare materializzazione e pubblicazione per refresh `Unchanged`.
- Implementare fast path statico.
- Implementare `IRuntimeDescriptionSnapshotStore` e formato snapshot versionato.
- Implementare store memory predefinito e adapter distributed basato su `IDistributedCache`.
- Validare esplicitamente la disponibilita di `IDistributedCache` in modalita `Distributed`.
- Implementare retention 24 ore e massimo 10 snapshot come default configurabili.
- Implementare hard limit UTF-8 per singola descrizione e catalogo completo.
- Implementare rinnovo del last-known-good per refresh `Unchanged`.
- Implementare policy di errore e catena current/store/static fallback.
- Impedire la pubblicazione di cataloghi misti dopo risoluzioni parziali.
- Ricalcolare hash e validare `TemplateHash` dopo ogni lettura dallo store.
- Rendere gli errori di persistenza non bloccanti per la pubblicazione in memoria.
- Aggiungere `SceneContext.MaterializedRuntimeSceneCatalog` come proprieta interna non persistita.
- Aggiungere `SceneContext.RuntimeDescriptions` come vista pubblica read-only non persistita.
- Aggiungere `SceneContext.RuntimeSceneCatalog` come proiezione pubblica immutabile per custom planner.
- Calcolare `SourceVersion` e `HasUniformSourceVersion` con semantica di acquisition state esplicita.
- Aggiungere `RuntimeDescriptionCatalogId` a `ExecutionState`.
- Ripristinare il `CatalogId` soltanto per esecuzioni interrotte, usando il comportamento gia presente in `LoadFromStoredConversation`.
- Implementare acquisizione per `CatalogId` e version miss policy nel catalog manager.
- Fare implementare `IScene` e `ISceneTool` ai modelli materializzati per preservare le firme esistenti.

Deliverable:

- catalogo globale immutabile, completo e pubblicabile senza chiamare il modello.

### Fase 3 - Direct mode e SceneExecutor

- Sostituire l'uso diretto di `ScenesAsAiTool` nel Direct mode.
- Acquisire il catalogo una sola volta in `SceneManager`.
- Spostare l'acquisizione dopo autorizzazione/load dello stato ma prima dell'inizializzazione dinamica e dei MainActor.
- Fare usare `context.MaterializedRuntimeSceneCatalog` a tutti gli execution mode standard.
- Usare declaration di routing e tool gia materializzate.
- Passare la scena materializzata a `SceneExecutor`.
- Verificare il tool calling loop e forced tools.
- Verificare client interaction e resume.

Deliverable:

- percorso Direct completo con scene e tool description runtime.

### Fase 4 - Planning e Dynamic Chaining

- Rendere il system prompt del planner basato sul catalogo materializzato.
- Aggiornare il planner standard a leggere il catalogo request-local da `SceneContext` e documentare lo stesso contratto per i planner custom.
- Riutilizzare lo snapshot durante i passi del piano.
- Integrare selezione ed esecuzione nel Dynamic Chaining.
- Verificare scene rieseguite e round multipli.

Deliverable:

- parita funzionale dei tre execution mode.

### Fase 5 - Endpoint, client tool e discovery

- Completare tutti i builder di tool locali.
- Applicare schema statico + descrizione dinamica agli endpoint tool.
- Gestire i client interaction tool.
- Implementare il comportamento fallback della discovery.
- Aggiornare i DTO solo se possibile senza breaking change.

Deliverable:

- copertura uniforme dei tool registrati localmente.

### Fase 6 - Telemetria, documentazione e hardening

- Aggiungere metriche e log diagnostici.
- Aggiungere `RuntimeDescriptionRefreshResult`.
- Aggiungere ciclo completo di eventi strutturati triggered/change-detected/started/terminal.
- Garantire che `catalog_published` venga emesso soltanto dopo l'atomic swap.
- Aggiungere warning strutturati per source failure, current retained, snapshot recovery, store failure, snapshot rejected e fallback statico.
- Distinguere recovery source e durata dello snapshot store nel risultato del refresh.
- Propagare l'execution info tipizzata in tutte le `AiSceneResponse` successive all'acquisizione.
- Documentare query multi-istanza per verificare il recepimento del cambiamento.
- Aggiungere esempi con file provider, memory provider e Azure App Configuration.
- Documentare caching, fallback, sicurezza e drawback.
- Documentare il trust boundary: limiti e integrita nel core, validazione semantica nell'applicazione.
- Eseguire benchmark e test di carico.

Deliverable:

- feature pronta per preview pubblica.

## 15) Piano di testing

### 15.1 Test unitari del descrittore

- valore statico restituito senza invocare factory;
- factory sincrona invocata con il `RuntimeDescriptionContext` corretto;
- factory asincrona riceve il `CancellationToken`;
- factory eseguita dentro lo scope DI creato dal coordinator;
- tutte le factory dello stesso tentativo ricevono la stessa istanza di `RuntimeDescriptionContext`;
- tutte le factory dello stesso tentativo risolvono la stessa istanza dei servizi scoped;
- i resolver vengono invocati sequenzialmente in ordine deterministico senza overlap;
- lo scope viene disposto al termine del refresh e non conservato nel catalogo;
- uno scoped snapshot accessor effettua una sola lettura remota per refresh;
- refresh reason valorizzata correttamente;
- factory invalida rifiutata in configurazione;
- null, empty e whitespace gestiti secondo policy;
- fallback usato solo nei casi previsti;
- cancellazione non convertita in fallback.
- overload `string` normalizzato in `RuntimeDescriptionValue`;
- Version, Source ed ETag avanzati preservati come metadata diagnostici;
- metadata sorgente non trusted non sostituiscono gli hash calcolati.
- tutte le versioni dinamiche presenti e uguali producono `HasUniformSourceVersion=true` e `SourceVersion` valorizzata;
- una versione mancante produce `HasUniformSourceVersion=false` e `SourceVersion=null`;
- versioni differenti producono `HasUniformSourceVersion=false` senza invalidare il catalogo;
- un catalogo completamente statico non dichiara una source version uniforme.

### 15.2 Test unitari del coordinator e materializer

- startup refresh pubblica la prima versione valida;
- background refresh sostituisce atomicamente la versione corrente;
- change notification attiva un refresh senza attendere il timer;
- refresh fallito non pubblica un catalogo parziale;
- refresh manuale e deterministico e awaitable;
- timer/change notification ignorano il trigger quando il lock e occupato;
- manual refresh attende il ciclo corrente e ne esegue uno nuovo;
- il timer successivo recupera una change notification ignorata durante un refresh;
- non esistono refresh sovrapposti per la stessa factory;
- `EveryRequest` invoca la sorgente una volta per richiesta;
- `EveryRequest` non invoca la sorgente a ogni iterazione LLM/tool;
- `EveryRequest` non avvia il background service;
- `Manual` con `RefreshAtStartup=false` non legge la sorgente durante startup;
- `Manual` con `RefreshAtStartup=true` esegue un solo refresh iniziale e non avvia timer o change subscription;
- in `Manual`, richieste e discovery non invocano la sorgente;
- refresh manuale riuscito pubblica globalmente e aggiorna lo snapshot store;
- refresh manuale restituisce catalog identity completa, operation ID e `LastValidatedAt` usabili da una barriera;
- un errore operativo in `Manual` restituisce outcome `Failed` senza mascherarlo con il catalogo corrente;
- piu richieste successive al refresh manuale riusano lo stesso `CatalogId` senza nuova materializzazione;
- un secondo refresh manuale pubblica la nuova versione soltanto dopo il completamento dei trial della precedente quando il runner applica la barriera;
- ogni refresh materializza tutte le scene e tutti i tool locali;
- schema ed executor statici vengono riusati;
- scene routing e tool locali producono `AIFunctionDeclaration`;
- versione sorgente presente o assente viene gestita correttamente;
- hash canonico e stabile rispetto all'ordine di enumerazione della sorgente;
- una modifica a una descrizione cambia `ContentHash` e `CatalogId`;
- timestamp e refresh reason non cambiano `ContentHash` o `CatalogId`;
- una modifica a schema o firma statica cambia `TemplateHash` e `CatalogId`;
- una modifica alle sole descrizioni cambia `ContentHash` e `CatalogId` ma non `TemplateHash`;
- una modifica a source version o ETag non cambia `CatalogId`;
- stesso template e contenuto producono lo stesso `CatalogId` su istanze diverse;
- uno snapshot con template incompatibile non viene rimaterializzato;
- template mismatch applica `UseLatestAndWarn` o `Throw` secondo configurazione;
- refresh con `CatalogId` invariato produce outcome `Unchanged`;
- refresh `Unchanged` non crea declaration o un nuovo `MaterializedSceneCatalog` e mantiene la stessa reference del catalogo;
- refresh `Unchanged` con nuova source version uniforme aggiorna atomicamente soltanto il published-state wrapper;
- una modifica ai soli actor puo cambiare `RuntimeDescriptions.SourceVersion` senza cambiare `CatalogId`;
- fast path statico restituisce gli oggetti pre-materializzati;
- tool executor e schema restano invariati;
- snapshot e collection risultano immutabili.

### 15.2.1 Test di aggancio a `SceneContext`

- `SceneManager` acquisisce il catalogo una sola volta per richiesta;
- richieste non autorizzate non acquisiscono il catalogo;
- cache/repository e stato pinned vengono caricati prima dell'acquisizione;
- `RuntimeDescriptions` e valorizzato prima dell'esecuzione dei MainActor;
- MainActor effettivamente eseguiti e actor di scena osservano lo stesso `CatalogId` della richiesta;
- un actor puo usare la `SourceVersion` uniforme per recuperare lo snapshot applicativo esatto;
- `RuntimeDescriptions` non espone description, scene, tool, declaration o executor;
- `RuntimeSceneCatalog` espone description e declaration effettive ma non executor, delegate o service provider;
- le collection della vista pubblica sono realmente immutabili;
- planner standard e custom di riferimento leggono la stessa vista request-local;
- tutti gli execution mode osservano la stessa reference/versione;
- `SceneExecutor` riceve scene materializzate senza cambiare la propria firma pubblica interna;
- `MaterializedRuntimeSceneCatalog` non viene copiato in `ExecutionState`;
- `MaterializedRuntimeSceneCatalog` non viene serializzato in cache o repository;
- `RuntimeDescriptions` non viene serializzato integralmente in cache o repository;
- tutte le risposte successive all'acquisizione espongono la stessa istanza di `RuntimeDescriptionExecutionInfo`;
- le risposte pre-acquisizione consentite espongono `RuntimeDescriptions=null`;
- una risposta terminale successiva all'acquisizione non puo perdere l'execution info;
- il resume acquisisce il catalogo secondo la consistency policy configurata.

### 15.2.2 Test di consistency mode

- `Request` usa il catalogo corrente a ogni richiesta;
- `Request` usa il catalogo corrente anche durante un resume;
- `Execution` salva il `CatalogId` in `ExecutionState`;
- `Execution` riusa lo stesso `CatalogId` dopo `AwaitingClient`;
- `Execution` riusa lo stesso `CatalogId` dopo `ExecutingScene`;
- `Execution` riusa lo stesso `CatalogId` durante `Chaining`;
- un nuovo turno dopo una fase terminale usa il catalogo corrente;
- un nuovo turno caricato da cache/repository conserva l'`InitialContext` e non riesegue automaticamente il MainActor;
- dopo un refresh, una conversazione esistente puo usare il nuovo catalogo mantenendo il MainActor storico;
- una nuova `ConversationKey` costruisce un nuovo contesto ed esegue il MainActor contro la source version corrente;
- PlayFramework non persiste o deduce un `ActorSnapshotId`;
- `RuntimeDescriptions.SourceVersion` non viene trattato come prova che una factory actor lo abbia usato;
- record `StoredConversation` precedenti senza `CatalogId` restano deserializzabili;
- `UseLatestAndWarn` usa il catalogo corrente quando la versione pinned manca;
- `UseLatestAndWarn` emette warning, metrica e activity event una sola volta per richiesta;
- `Throw` interrompe prima della prima chiamata LLM;
- versione richiesta e utilizzata sono presenti nella telemetria senza descrizioni in chiaro;
- `Conversation` non e disponibile nella prima release.

### 15.3 Test Direct mode

- l'LLM riceve la scene description aggiornata;
- la scena selezionata riceve tool description aggiornata;
- nessuna lettura della sorgente o materializzazione avviene nel Direct hot path in modalita `Background`;
- forced tool usa nome/esecutor statico e declaration aggiornata;
- streaming e non-streaming producono lo stesso catalogo;
- una seconda richiesta vede un aggiornamento del provider.

### 15.4 Test Planning mode

- il system prompt contiene scene e tool description risolte;
- in modalita `Background` il provider non viene invocato dalla richiesta di planning;
- in modalita `EveryRequest` sorgente e materializzazione vengono completate una sola volta prima del planning;
- tutti i passi riusano lo stesso snapshot;
- una variazione del provider a piano iniziato non cambia la richiesta corrente;
- errori di risoluzione impediscono la creazione di un piano incoerente.

### 15.5 Test Dynamic Chaining

- la selezione usa scene description runtime;
- scene e tool provengono dallo stesso catalogo completo;
- round successivi non rileggono la sorgente e non ricreano declaration;
- scene rieseguite riusano lo snapshot;
- il limite `MaxDynamicScenes` resta rispettato.

### 15.6 Test per tipologia di tool

- service method tool;
- endpoint tool senza body;
- endpoint tool con body;
- endpoint tool con route e query parameters;
- client interaction tool;
- combinazione tool locali e MCP;
- forced scene tool e forced MCP tool.

Per ogni caso verificare separatamente:

- description inviata al modello;
- schema invariato;
- esecuzione corretta;
- nome invariato;
- risultato correttamente correlato alla function call.

Verificare inoltre che l'adapter Azure di test e gli adapter standard accettino `AIFunctionDeclaration`; un adapter che filtra esclusivamente `AIFunction` deve fallire un test di conformita esplicito.

### 15.7 Test concorrenza e isolamento

- richieste concorrenti vedono sempre una versione completa, precedente o successiva all'atomic swap;
- nessuna richiesta osserva un catalogo contenente una combinazione parziale di due versioni;
- nessuna richiesta combina il catalogo di un published state con il source stamp di un altro;
- un refresh `Unchanged` concorrente lascia stabile catalogo e source stamp gia acquisiti dalla richiesta in corso;
- un refresh background lento non blocca le richieste che usano lo snapshot corrente;
- `EveryRequest` usa il proprio snapshot senza sostituire quello globale;
- alta concorrenza non muta liste o `AITool` singleton;
- factory scoped viene risolta dal corretto scope DI.

Questo gruppo e obbligatorio: coerenza e pubblicazione atomica sono le ragioni principali per usare snapshot immutabili.

### 15.8 Test failure e resilienza

- timeout del provider;
- cancellazione propagata senza attivare recovery;
- provider non registrato;
- chiave non trovata;
- fallback presente o assente;
- errore parziale della sorgente non produce un catalogo misto;
- catalogo corrente completo preferito a store e fallback statici;
- store preferito ai fallback statici quando non esiste un catalogo corrente;
- fallback statici usati soltanto se formano un catalogo completo;
- `Throw` in startup ed `EveryRequest` propaga l'errore;
- `UseFallback` esaurisce la catena prima di fallire;
- valore oltre `MaxDescriptionUtf8Bytes` rifiutato senza pubblicazione;
- catalogo oltre `MaxCatalogUtf8Bytes` rifiutato senza pubblicazione;
- limiti applicati sui byte UTF-8 e non sul numero di caratteri .NET;
- Unicode non valido e NUL rifiutati, newline e whitespace significativi preservati;
- eccezione durante creazione declaration;
- errori ripetuti non causano retry impliciti;
- log privi del contenuto sensibile.

### 15.8.1 Test dello snapshot store

- memory store e il default e non richiede `IDistributedCache`;
- memory store non conserva snapshot dopo la ricostruzione del service provider/processo;
- modalita distributed senza `IDistributedCache` fallisce la validazione all'avvio;
- adapter distributed serializza e deserializza lo snapshot senza declaration o executor;
- un catalogo modificato viene salvato con identita e descrizioni complete;
- refresh `Unchanged` rinnova source stamp, `LastValidatedAt` e scadenza senza creare declaration;
- current snapshot confermato non scade mentre i refresh continuano a riuscire;
- catalogo materializzato corrente resta utilizzabile nel processo anche dopo la scadenza dello snapshot store durante un outage;
- snapshot storico scade dopo `SnapshotRetention` e una lettura pinned non ne rinnova la scadenza;
- memory store applica esattamente `MaxRetainedSnapshots`, incluso il corrente, e mantiene al massimo 10 elementi con i default;
- distributed store applica il limite in modo deterministico sulla singola istanza e best effort tra istanze concorrenti, lasciando alla TTL il limite temporale definitivo;
- retention e limite custom vengono rispettati;
- lookup per `CatalogId` recupera una versione pinned ancora disponibile;
- startup con sorgente non disponibile recupera il latest snapshot compatibile;
- recovery rimaterializza declaration usando il template statico corrente;
- snapshot con formato sconosciuto viene rifiutato;
- snapshot incompleto o sovradimensionato viene rifiutato;
- snapshot con `ContentHash` o `CatalogId` alterato viene rifiutato;
- snapshot con `TemplateHash` differente viene rifiutato;
- snapshot scaduto viene rifiutato;
- errore di lettura continua con fallback statico quando configurato;
- errore di scrittura non impedisce l'atomic swap del candidato valido;
- catalogo statico degraded non sovrascrive il last-known-good dinamico;
- `EveryRequest` request-local non aggiorna il latest globale nello store;
- store distributed non viene letto nel normale hot path `Background`;
- race multi-istanza sull'indice non produce snapshot parziali e applica version miss se una history entry viene persa;
- payload e chiavi dello store non compaiono nei log.

### 15.8.2 Test sicurezza e trust boundary

- input di richiesta, tenant e conversation metadata non possono selezionare source, candidate o catalogo;
- nessun `SceneRequestSettings` espone refresh manuale o scelta della versione;
- l'eventuale endpoint amministrativo di esempio richiede authorization esplicita e non viene registrato automaticamente dal framework;
- chiavi memory/distributed sono isolate per applicazione, environment, factory e algoritmo;
- una collisione di factory name tra environment non condivide snapshot;
- payload store alterato viene rifiutato prima della creazione delle declaration;
- log, eventi e metriche non contengono descrizioni, diff, segreti o URI completi;
- `CatalogId` e hash non vengono presentati come firma di autenticita o approvazione semantica;
- fixture di prompt injection e tool misuse dimostrano che il core preserva il testo ma non lo dichiara sicuro;
- un linter applicativo puo bloccare l'aggiornamento della sorgente prima della barriera senza un nuovo extension point PlayFramework.

### 15.8.3 Test degli eventi strutturati

- ogni refresh possiede un `OperationId` stabile;
- ogni trigger produce `refresh_triggered` ed esattamente un evento terminale;
- change notification produce `refresh_triggered` con il relativo reason e produce `change_detected` soltanto se il confronto canonico conferma la variazione;
- timer, manual refresh ed `EveryRequest` producono `change_detected` soltanto quando il confronto canonico rileva una variazione;
- `refresh_started` non viene emesso per `refresh_skipped_busy`;
- refresh con modifica produce `catalog_published` dopo l'atomic swap;
- nell'handler di `catalog_published`, `Current.CatalogId` e gia quello nuovo;
- refresh senza modifica produce `catalog_unchanged`;
- refresh occupato produce `refresh_skipped_busy`;
- source failure produce `source_resolution_failed` come warning;
- mantenimento del catalogo corrente produce `current_catalog_retained` e termina con `refresh_failed`;
- recovery dallo store produce `snapshot_recovered` e termina con `catalog_published` dopo lo swap;
- persistenza riuscita produce `snapshot_persisted` informativo;
- errore di persistenza produce `snapshot_store_write_failed` senza trasformare `catalog_published` in failure;
- snapshot invalido produce `snapshot_rejected` con motivo bounded;
- fallback statico produce `fallback_used`, recovery source e fallback item count;
- version miss produce `pinned_catalog_miss`;
- template mismatch produce `template_mismatch`;
- refresh fallito produce failure stage senza contenuto delle descrizioni;
- source version e `CatalogId` consentono di correlare eventi tra istanze;
- `CatalogId`, source version e `OperationId` non vengono usati come label delle metriche;
- descrizioni, valori precedenti/successivi e connection string non compaiono in log, activity o metriche;
- nomi degli elementi modificati sono assenti per default e presenti soltanto con debug opt-in.

### 15.9 Test discovery

- configurazione statica invariata;
- configurazione dinamica restituisce il valore dello snapshot globale corrente;
- fallback restituito soltanto prima della disponibilita di uno snapshot valido, se previsto dalla startup policy;
- discovery non invoca factory e non attiva refresh;
- `isRuntimeResolved` e `runtimeDescriptionVersion` sono valorizzati correttamente;
- `EveryRequest` non altera il valore esposto dalla discovery.
- discovery non viene usata come attestazione dell'identita effettiva di una risposta;
- `AiSceneResponse.RuntimeDescriptions` coincide con lo stato acquisito anche quando discovery mostra un catalogo globale differente.

### 15.10 Test di retrocompatibilita

- tutti i test esistenti PlayFramework restano verdi;
- codice compilato con overload statici non richiede modifiche;
- serializzazione e API discovery esistenti non subiscono breaking change;
- custom `ISceneFactory` continua a compilare;
- applicazioni senza descrizioni dinamiche non risolvono il nuovo servizio nel percorso critico.

### 15.11 Benchmark

Confrontare prima e dopo:

- richiesta completamente statica;
- 5 scene con 5 tool ciascuna;
- 20 scene con 20 tool ciascuna;
- 100 scene con 20 tool ciascuna;
- Direct con una sola scena selezionata;
- Planning con catalogo completo;
- provider in-memory;
- provider remoto simulato con latenza 10, 50 e 200 ms;
- resolver con I/O per singola descrizione e resolver basati su uno scoped snapshot accessor;
- modalita `Background` con snapshot gia pubblicato;
- modalita `EveryRequest` con le stesse latenze simulate;
- refresh `Changed` e `Unchanged` separati;
- snapshot store memory e distributed simulato con latenze 1, 10 e 50 ms;
- startup recovery, write, expiration refresh e pinned lookup separati;
- 1, 10 e 100 richieste concorrenti.

Metriche:

- tempo prima della prima chiamata LLM;
- tempo totale;
- allocazioni hot path per richiesta;
- allocazioni per refresh `Changed` e `Unchanged`;
- numero di factory invocation;
- numero di letture remote effettive per refresh;
- numero di declaration create;
- durata di source, validation, hash, materialization, snapshot store e publication;
- throughput;
- p50, p95 e p99.

## 16) Quality gate

La feature puo essere considerata pronta solo se:

- nessuna mutazione di metadata singleton avviene durante la richiesta;
- i test di atomicita e concorrenza tra versioni sono verdi;
- catalogo e source stamp vengono sempre acquisiti dallo stesso published state immutabile;
- tutti gli execution mode usano il catalogo materializzato;
- schema, nome ed executor dei tool restano statici;
- scene routing e tool locali usano `AIFunctionDeclaration`;
- gli adapter standard e di test accettano `AIFunctionDeclaration`;
- il runtime usa soltanto `RuntimeDescriptionCatalogManager` e il background service come coordinator; lo snapshot store resta un adapter di persistenza;
- ogni richiesta acquisisce il catalogo una sola volta tramite `SceneManager`;
- ogni refresh usa un solo scope, un solo `RuntimeDescriptionContext` e resolver sequenziali;
- il pattern scoped snapshot accessor effettua una sola lettura remota per refresh nei test di riferimento;
- nessun execution handler contiene logica di refresh, fallback, hashing o materializzazione;
- il catalogo materializzato non viene inserito in `SceneContext.Properties` e non viene persistito; lo store contiene soltanto snapshot serializzabili validati;
- consistency mode `Execution` preserva la versione nei resume interrotti;
- un nuovo turno completato acquisisce il catalogo corrente;
- una conversazione esistente non sostituisce implicitamente il MainActor salvato dopo un refresh;
- la documentazione non presenta `RuntimeDescriptions.SourceVersion` come attestazione dell'origine degli actor;
- nessuna modalita `Conversation` o persistenza di `ActorSnapshotId` viene introdotta nella prima release;
- `RuntimeDescriptions` e disponibile prima dei MainActor e contiene soltanto identita read-only;
- `AiSceneResponse.RuntimeDescriptions` espone la stessa identita effettivamente acquisita ed e obbligatoria per le risposte successive all'acquisizione;
- refresh result e response execution info sono correlabili tramite catalog identity e validation operation ID;
- discovery e telemetria non vengono presentate come prova funzionale request-local;
- la vista pubblica `RuntimeSceneCatalog` espone description e declaration immutabili senza executor;
- planner standard e custom possono consumare il catalogo request-local senza modificare `ISceneFactory`;
- actor e execution mode osservano lo stesso `CatalogId` durante la richiesta;
- `RuntimeDescriptions.SourceVersion` viene esposta soltanto quando ogni resolver dinamico fornisce la stessa versione;
- un refresh `Unchanged` puo aggiornare il source stamp senza allocare declaration o cambiare `CatalogId`;
- version miss applica sempre la policy configurata ed e osservabile;
- `CatalogId` combina template e contenuto con algoritmo versionato;
- nessun template mismatch combina vecchie descrizioni e nuovi schema/executor;
- ogni trigger produce un evento terminale strutturato e correlabile;
- `catalog_published` viene emesso soltanto dopo l'atomic swap;
- la telemetria consente di verificare il recepimento per ciascuna istanza senza esporre descrizioni;
- ogni recovery e ogni errore dello snapshot store produce un warning strutturato;
- un errore parziale non pubblica mai un catalogo misto;
- un errore di persistenza non invalida il catalogo corrente e non impedisce la pubblicazione in memoria di un candidato valido;
- ogni snapshot recuperato viene verificato per formato, completezza, hash, template e scadenza;
- limiti UTF-8 per valore e catalogo sono applicati sia alla sorgente sia allo snapshot store;
- la retention dello store non invalida il catalogo materializzato corrente di un processo attivo;
- cataloghi statici degraded ed `EveryRequest` request-local non sovrascrivono il latest dinamico nello store;
- memory e distributed store rispettano retention e limite configurati secondo le garanzie documentate;
- la modalita distributed senza `IDistributedCache` fallisce la validazione delle opzioni;
- le configurazioni statiche usano il fast path;
- la modalita `Background` non esegue I/O della sorgente nel percorso della richiesta;
- la modalita `Manual` non esegue alcun refresh implicito dopo l'eventuale startup refresh;
- le modalita `Background` e `Manual` non eseguono I/O dello snapshot store nel percorso corrente, salvo il recupero di una versione pinned assente dalla history locale;
- `EveryRequest` forza al massimo un refresh per richiesta;
- cancellazione e fallback hanno comportamento documentato;
- discovery non esegue factory con contesto artificiale;
- input client, tenant e conversation metadata non possono selezionare source, candidate, refresh o catalogo;
- la documentazione distingue integrita strutturale da sicurezza semantica e non introduce un validator generico in v1;
- l'hot path `Background` rispetta il quality target di latenza concordato;
- refresh `Unchanged` non alloca nuove declaration;
- il costo del refresh `Changed`, dello startup e di `EveryRequest` e misurato e documentato;
- il drawback della rimaterializzazione per versione e riportato nelle release notes.

## 17) Strategia di rilascio

### Preview

- overload dinamici disponibili;
- comportamento opt-in: nessuna modifica per configurazioni statiche;
- log diagnostici e metriche attive;
- documentazione esplicita su latenza e fallback.

### Beta

- feedback da applicazioni multi-istanza;
- validazione Azure App Configuration;
- benchmark pubblicati;
- eventuali ottimizzazioni di schema/declaration caching.

### Stable

- contratto della vista pubblica read-only del catalogo request-local stabilizzato dopo il feedback preview/beta;
- migration note per consumer che leggono direttamente `ISceneFactory`;
- decisione separata su schema e parameter descriptions dinamici.

## 18) Rollback

La feature e opt-in per singola descrizione. Il rollback operativo consiste nel tornare agli overload statici o configurare sempre il fallback statico.

Non devono essere necessarie migrazioni di dati. Se viene introdotto un setting globale di abilitazione, disabilitarlo deve forzare il fallback statico senza cambiare nomi, schema o executor.

## 19) Backlog tecnico sintetico

1. Test di caratterizzazione del comportamento corrente.
2. `RuntimeTextConfiguration`, scope unico per refresh e risoluzione sequenziale.
3. Overload scene.
4. Overload service tool.
5. Overload endpoint tool.
6. Overload client interaction tool.
7. Template statico e catalogo globale materializzato.
8. `RuntimeDescriptionCatalogManager`, background service sottile e refresh mode `Manual`.
9. Aggancio interno del catalogo, `RuntimeDescriptionExecutionInfo` e `RuntimeSceneCatalogView` pubblici su `SceneContext`.
10. Consistency mode `Request` e `Execution`.
11. Persistenza del `CatalogId` in `ExecutionState`.
12. Missing version behavior e osservabilita.
13. `RuntimeDescriptionValue`, overload avanzati e source version uniforme.
14. `TemplateHash`, `ContentHash` e `CatalogId` versionati.
15. Template mismatch policy.
16. `IRuntimeDescriptionSnapshotStore` e formato snapshot versionato.
17. Memory snapshot store predefinito.
18. Distributed snapshot store basato su `IDistributedCache`.
19. Retention, pruning e rinnovo del last-known-good.
20. Recovery current/store/static fallback senza cataloghi misti.
21. Validazione di snapshot recuperati e gestione store failure.
22. `RuntimeDescriptionRefreshResult`, propagazione tipizzata su `AiSceneResponse` ed eventi strutturati.
23. Fast path statico.
24. Declaration-only per scene routing e tool locali.
25. Conformance update degli adapter.
26. Direct execution integration.
27. SceneExecutor integration.
28. Planning integration.
29. Dynamic Chaining integration.
30. Discovery fallback behavior.
31. Privacy, hard limit UTF-8, trust boundary e sicurezza di log/snapshot store.
32. Test concorrenza, atomic publication e multi-istanza.
33. Test memory/distributed store, expiration e recovery.
34. Benchmark e documentazione esempi.

## 20) Stima indicativa

- Fase 0-1: 2-3 giorni;
- Fase 2: 4-6 giorni;
- Fase 3: 3-4 giorni;
- Fase 4: 1-2 giorni;
- Fase 5: 2-3 giorni;
- Fase 6, benchmark e hardening: 4-6 giorni.

Totale indicativo: 17-26 giorni lavorativi, esclusa l'eventuale progettazione di schema dinamico e versionamento delle conversazioni. Lo store distributed e la retention aggiungono circa 3-4 giorni; modalita `Manual`, viste pubbliche tipizzate, propagazione sulla risposta, riordino dell'acquisizione e test di sicurezza/coerenza aggiungono circa 3-5 giorni rispetto al piano precedente alla review TimeVision.

## 21) Decisioni finali per l'esecuzione

- [x] Descrizioni globali per l'applicazione, non tenant/request-specific.
- [x] Refresh in background come default.
- [x] Refresh mode `Manual` con catalogo globale e aggiornamento soltanto on demand dopo l'eventuale startup.
- [x] `RefreshAtStartup` configurabile anche in `Manual`; timer e change notification sempre disabilitati.
- [x] Pubblicazione atomica di cataloghi completi e validi.
- [x] Modalita `EveryRequest` disponibile per test e diagnostica.
- [x] Refresh manuale disponibile tramite servizio, non controllabile dal client.
- [x] Catalogo completo materializzato durante il refresh, senza risoluzione lazy nella richiesta.
- [x] Scene routing e tool locali rappresentati da `AIFunctionDeclaration`.
- [x] Schema ed executor statici riusati tra versioni.
- [x] Versione sorgente opzionale con hash canonico calcolato dal framework.
- [x] Refresh con contenuto invariato non pubblica ne rimaterializza il catalogo.
- [x] Valutazione distinta della latenza di hot path, refresh, startup ed `EveryRequest`.
- [x] Due soli coordinator runtime: catalog manager e background service sottile, piu uno snapshot store infrastrutturale.
- [x] Nessun resolver per livello, cache per scena o materializer registrato separatamente.
- [x] Un solo scope DI e un solo `RuntimeDescriptionContext` per tentativo di refresh.
- [x] Resolver eseguiti sequenzialmente e in ordine deterministico nella prima release.
- [x] Nessuna opzione di parallelismo dei resolver finche i benchmark non ne dimostrano la necessita.
- [x] Catalogo acquisito una sola volta da `SceneManager` e agganciato internamente a `SceneContext`.
- [x] Acquisizione dopo autorizzazione/load dello stato e prima di MainActor e actor di scena.
- [x] `RuntimeDescriptionExecutionInfo` pubblico read-only su `SceneContext`, senza contenuti o executor.
- [x] Modelli materializzati compatibili con `IScene` e `ISceneTool` per preservare le firme esistenti.
- [x] Timer e change notification saltano il ciclo se un refresh e gia attivo.
- [x] Refresh manuale attende il lock e forza un nuovo ciclo.
- [x] `EveryRequest` e background service mutuamente esclusivi.
- [x] Nessuna selezione candidate/catalogo tramite request, trial ID, tenant o metadata.
- [x] Nessuna creazione dinamica di factory aggiunta per il solo scenario evaluation.
- [x] Consistency mode configurabile tra `Request` ed `Execution`.
- [x] `Execution` come consistency mode predefinita.
- [x] `CatalogId` persistito soltanto in `ExecutionState` per i resume interrotti.
- [x] Nuovi turni di conversazioni completate acquisiscono il catalogo corrente.
- [x] Consistency mode `Conversation` rimandata a un requisito futuro.
- [x] Nessuna riesecuzione o sostituzione automatica del MainActor salvato in una conversazione esistente.
- [x] Nessuna persistenza o deduzione automatica di `ActorSnapshotId` nel core PlayFramework.
- [x] `RuntimeDescriptions.SourceVersion` e una correlazione disponibile, non una garanzia sull'uso da parte degli actor.
- [x] `UseLatestAndWarn` come default quando la versione pinned non e disponibile.
- [x] `Throw` disponibile come comportamento fail-closed.
- [x] Overload semplici `string` e overload avanzati `RuntimeDescriptionValue`.
- [x] Version, Source ed ETag trattati come metadata diagnostici, non come identita trusted.
- [x] `SourceVersion` valorizzata soltanto se ogni resolver dinamico restituisce la stessa versione non vuota.
- [x] Versioni mancanti o differenti non invalidano applicazioni generiche ma producono `HasUniformSourceVersion=false`.
- [x] Refresh `Unchanged` aggiorna atomicamente il source stamp quando cambia soltanto la versione applicativa.
- [x] Nessun riferimento a `AiPromptSnapshot` o `IAiPromptProvider` nel contratto PlayFramework.
- [x] Identita composita `CatalogId = hash(TemplateHash + ContentHash)` con algoritmo versionato.
- [x] Template mismatch non combina mai vecchie descrizioni con schema/executor nuovi.
- [x] `RuntimeDescriptionRefreshResult` come risultato comune del refresh.
- [x] Telemetria strutturata per trigger ricevuto, change detected, refresh started e outcome terminale.
- [x] `catalog_published` emesso soltanto dopo l'atomic swap.
- [x] Nessun audit database separato nel core PlayFramework.
- [x] Snapshot store memory/Redis limitato al last-known-good operativo; artifact di riproducibilita demandati al consumer.
- [x] Nessuna descrizione o diff testuale nella telemetria di default.
- [x] `IRuntimeDescriptionSnapshotStore` come unica astrazione sostituibile per il last-known-good.
- [x] Store memory predefinito e store distributed esplicito basato su `IDistributedCache`.
- [x] Nessun fallback silenzioso a memory se `IDistributedCache` manca.
- [x] Snapshot composto da descrizioni e identita, mai declaration o executor.
- [x] Ordine di recovery: catalogo corrente, snapshot store, catalogo statico di fallback, errore.
- [x] Nessun catalogo misto dopo una risoluzione dinamica parzialmente fallita.
- [x] `UseFallback` come failure mode predefinita e `Throw` disponibile per fail-closed/test.
- [x] Retention configurabile con default 24 ore.
- [x] Massimo snapshot configurabile con default 10 per factory, incluso il corrente.
- [x] Rinnovo di last-known-good e `LastValidatedAt` dopo refresh `Unchanged`.
- [x] Snapshot storici non rinnovati dalle letture e version miss applicato dopo la scadenza.
- [x] La retention non espelle il current catalog gia pubblicato in un processo attivo.
- [x] Cataloghi statici degraded ed `EveryRequest` non sovrascrivono il latest globale nello store.
- [x] Errore dello store non invalida il corrente e non blocca l'atomic swap in memoria.
- [x] Nessun retry nel core; timeout, retry e circuit breaker restano nel provider.
- [x] Warning strutturati per source failure, current retained, snapshot recovery/rejection, store failure e fallback.
- [x] Pruning best effort multi-istanza di `IDistributedCache` accettato; retention forte richiede uno store custom.
- [x] Scope limitato alle descrizioni principali, senza nomi, schema o parameter descriptions dinamici.
- [x] Discovery senza I/O: catalogo globale corrente se disponibile, altrimenti fallback statico previsto dalla startup policy.
- [x] Fast path obbligatorio per configurazioni statiche.
- [x] `ISceneFactory` sincrona mantenuta come template/static fallback per retrocompatibilita.
- [x] Vista pubblica read-only request-local del catalogo per planner standard e custom, priva di executor.
- [x] `AiSceneResponse.RuntimeDescriptions` come prova tipizzata dell'identita effettivamente acquisita.
- [x] Discovery e telemetria non considerate prova funzionale request-local.
- [x] Hard limit configurabili con default 16 KiB UTF-8 per descrizione e 1 MiB per catalogo.
- [x] Sicurezza strutturale nel core; validazione semantica e approval workflow nell'applicazione.
- [x] Nessun `IRuntimeDescriptionSemanticValidator` generico nella prima release.
- [x] Accettazione esplicita del costo di rimaterializzazione per nuova versione e della modalita `EveryRequest`, confinata a contract test e diagnostica.
- [x] Nessun capability service dedicato: presenza delle API a compile time e risoluzione di `IRuntimeDescriptionRefresher` sono sufficienti.

## 22) Contratto di integrazione con framework di evaluation

Questa sezione raccoglie i requisiti emersi dall'adversarial review TimeVision e li traduce in capacita generiche di PlayFramework. Non introduce API legate a candidate, trial, dataset o grader: isolamento, scheduling e classificazione dei risultati restano responsabilita del runner di evaluation.

### 22.1 Isolamento delle candidate e batch manuali

Decisioni confermate:

- la v1 TimeVision usa una sola candidate per batch e per factory;
- il runner aggiorna la sorgente, invoca `IRuntimeDescriptionRefresher.RefreshAsync`, verifica il risultato e soltanto dopo apre il batch ai trial;
- i trial della stessa candidate possono essere paralleli;
- candidate differenti sono sequenziali e separate da una barriera che attende la conclusione di tutti i trial precedenti;
- `RuntimeDescriptionRefreshMode.Manual` mantiene il catalogo globale senza timer o change notification;
- per il massimo determinismo TimeVision configura normalmente `RefreshAtStartup=false` ed esegue esplicitamente il primo refresh;
- `EveryRequest` resta confinato ai contract test del reload e non viene usato come selector di candidate o profilo per evaluation massive.

Workflow batch minimo:

```text
freeze candidate source
        |
        v
RefreshAsync + verifica risultato
        |
        v
apertura barriera del batch
        |
        v
trial concorrenti sullo stesso catalogo
        |
        v
chiusura e attesa completa
        |
        v
candidate successiva
```

Le factory PlayFramework nominate possono isolare configurazioni conosciute all'avvio. Non viene aggiunta creazione dinamica di factory dopo la costruzione del `ServiceProvider`: candidate parallele arbitrarie richiedono factory pre-registrate o host separati e restano un'ottimizzazione futura da giustificare con benchmark.

Il framework non riceve candidate ID dal payload e non sceglie descrizioni in base a request metadata. Questo preserva la semantica globale delle descriptions ed evita contaminazione cross-request o l'introduzione di un secondo sistema di routing dei cataloghi.

Criteri di accettazione del punto:

- nessuna lettura della sorgente avviene tra due refresh manuali;
- nessun trigger automatico puo cambiare catalogo durante il batch;
- tutte le richieste successive al refresh acquisiscono il catalogo globale pubblicato;
- il refresh della candidate successiva non inizia finche il runner non ha chiuso il batch precedente;
- `EveryRequest` non viene presentato come meccanismo di isolamento tra candidate;
- la soluzione resta utilizzabile anche da applicazioni non di testing che richiedono change control manuale.

### 22.2 Coerenza tra snapshot applicativo, catalogo e actor

Decisioni confermate:

- PlayFramework crea un solo scope DI e un solo `RuntimeDescriptionContext` per refresh;
- i resolver vengono eseguiti sequenzialmente in ordine deterministico;
- TimeVision registra uno `ScopedPromptSnapshotAccessor` che carica una sola volta l'`AiPromptSnapshot` immutabile e serve da memoria tutte le description;
- ogni resolver TimeVision restituisce lo stesso `AiPromptSnapshotId` in `RuntimeDescriptionValue.Version`;
- PlayFramework espone la versione soltanto quando tutti i resolver dinamici forniscono la stessa versione non vuota;
- applicazioni che combinano sorgenti diverse restano supportate e ricevono `HasUniformSourceVersion=false` senza errore automatico;
- PlayFramework non conosce `AiPromptSnapshot`, candidate ID o `IAiPromptProvider`.

La correlazione con gli actor usa `SceneContext.RuntimeDescriptions`, valorizzato dopo il caricamento dello stato ma prima dell'inizializzazione dinamica e dei MainActor. L'actor continua a essere una factory indipendente: puo leggere `RuntimeDescriptions.SourceVersion` e chiedere al provider applicativo lo snapshot immutabile corrispondente, senza dipendere dal catalog manager.

```text
refresh scope
├── ScopedPromptSnapshotAccessor
│   └── una lettura AiPromptSnapshot
├── scene description resolver
├── tool description resolver
└── SourceVersion uniforme
            |
            v
PublishedRuntimeDescriptionState
├── MaterializedSceneCatalog
└── latest validated source stamp
            |
            v
RuntimeDescriptionExecutionInfo
            |
            +-- MainActor provider lookup by snapshot ID
            └-- scene actor provider lookup by snapshot ID
```

`RuntimeDescriptions` contiene soltanto identita e modalita di acquisizione. La distinta `RuntimeSceneCatalog` espone description e declaration read-only per planner e instrumentation, ma non executor o servizi. L'effective prompt snapshot, che comprende anche il testo degli actor, rimane un concetto TimeVision e non coincide necessariamente con `CatalogId`.

Criteri di accettazione del punto:

- una materializzazione causa una sola lettura dello snapshot applicativo nel golden test TimeVision;
- tutte le description del catalogo derivano dalla stessa istanza immutabile;
- `SourceVersion` coincide con l'`AiPromptSnapshotId` atteso;
- una modifica ai soli actor aggiorna la source version validata pur mantenendo invariato il `CatalogId`;
- `RuntimeDescriptions` e disponibile prima dell'esecuzione dei MainActor;
- MainActor, actor di scena ed execution mode osservano lo stesso `CatalogId` durante la richiesta;
- nessun tipo pubblico PlayFramework dipende da astrazioni TimeVision;
- versioni mancanti o miste sono osservabili senza impedire scenari applicativi generici.

Il riuso di MainActor gia salvati nella cronologia di una conversazione esistente non viene risolto da questo punto ed e trattato nella decisione successiva sulla consistenza multi-turn.

### 22.3 Consistenza multi-turn delle evaluation

Decisioni confermate:

- `RuntimeDescriptionConsistencyMode.Execution` continua a coprire una singola esecuzione e i resume interrotti, non l'intera conversazione;
- TimeVision congela la prompt source fino al completamento di tutti i turni e di tutti i trial del batch;
- il refresh della candidate successiva avviene soltanto dopo la barriera di chiusura del batch;
- ogni trial e ogni candidate usano una nuova `ConversationKey` e un namespace di cache isolato;
- ogni turno registra `CatalogId`, source version e `ActorSnapshotId` applicativo;
- un grader deterministico fallisce il trial ordinario se una di queste identita cambia;
- PlayFramework non riesegue o sostituisce automaticamente il MainActor presente nell'`InitialContext` di una conversazione esistente;
- PlayFramework non tenta di dedurre l'identita della sorgente realmente usata da factory actor arbitrarie.

Il nuovo `ConversationKey` e obbligatorio anche quando baseline e candidate usano lo stesso schema e gli stessi nomi: riutilizzare la conversazione conserverebbe il MainActor precedente e renderebbe ambiguo il confronto. L'isolamento della sandbox senza isolamento della conversation cache non e sufficiente.

L'adapter actor TimeVision usa `RuntimeDescriptions.SourceVersion` per richiedere lo snapshot immutabile atteso e registra autonomamente l'`ActorSnapshotId` effettivo. Questa instrumentation e la prova applicativa; la sola presenza della source version nel `SceneContext` non garantisce che una factory custom l'abbia rispettata.

Gli scenari che cambiano catalogo o actor tra turni sono esclusi dalla v1 della quality evaluation. Vengono rimandati all'hardening, richiedono un tag esplicito come `runtime-description-transition` e normalmente iniziano un nuovo contesto. Non vengono usati per giustificare una consistency mode `Conversation` nel core.

Criteri di accettazione del punto:

- nessun refresh avviene tra i turni di un trial ordinario;
- tutti i turni osservano lo stesso `CatalogId` e la stessa source version;
- l'actor provider registra lo stesso `ActorSnapshotId` atteso in ogni turno;
- una candidate non puo riusare conversation key o cache entry della baseline;
- ogni mismatch interrompe il trial prima della classificazione qualitativa;
- i resume `AwaitingClient`, `ExecutingScene` e `Chaining` continuano a usare il pin `Execution` di PlayFramework;
- la documentazione distingue esplicitamente identita del catalogo, source stamp e identita applicativa degli actor.

### 22.4 Barriera fail-closed e prova della candidate effettiva

Il profilo TimeVision per quality evaluation usa:

```text
RefreshMode = Manual
RefreshAtStartup = false
FailureMode = Throw
MissingVersionBehavior = Throw
SnapshotStoreMode = Memory
ConsistencyMode = Execution
```

`Throw` impedisce recovery trasparenti nel percorso di startup/richiesta; nel refresh amministrativo manuale un errore operativo viene rappresentato da outcome `Failed`, cosi il runner puo registrare il risultato prima di chiudere il batch. Cancellazione ed errori di configurazione continuano a propagarsi.

La barriera apre il batch soltanto se tutte le seguenti condizioni sono vere:

- outcome `Changed` oppure `Unchanged`, mai `Failed` o `SkippedBusy`;
- `RecoverySource=None` e `UsedFallback=false`;
- `SourceVersion` uniforme e uguale all'effective prompt snapshot atteso;
- `LastValidationOperationId` delle risposte coincide con l'`OperationId` del refresh che ha aperto il batch;
- `TemplateHash` coincide con quello della build/factory attesa;
- per una candidate che cambia descriptions, `ContentHash` e `CatalogId` coincidono con l'artefatto atteso e differiscono dalla baseline quando il contenuto differisce;
- per una candidate che cambia soltanto actor, e ammesso outcome `Unchanged` e lo stesso `CatalogId`, ma source version, validation operation e `LastValidatedAt` devono avanzare;
- ogni risposta del trial riporta la medesima identita e nessun recovery.

Non viene aggiunto un `ExpectedCatalogId` al payload della richiesta. In `Manual` non esistono trigger automatici e la barriera, insieme all'identita tipizzata della risposta, offre la garanzia richiesta senza rendere il catalogo client-selectable. Il catalogo corrente resta disponibile dopo un refresh fallito per resilienza operativa, ma il batch non puo considerarlo un successo.

`AiSceneResponse.RuntimeDescriptions` e il contratto funzionale per la singola richiesta. Include catalog identity, source stamp validato, operation ID, recovery, fallback e acquisition duration. Activity e log riportano gli stessi campi per diagnosi, ma non sono usati dal runner come unica prova perche possono essere campionati. Discovery resta dedicata a coverage e stato globale.

Criteri di accettazione del punto:

- una source failure con baseline ancora corrente restituisce `Failed` e non apre il batch;
- un recovery da memory/distributed/static fallback fallisce una quality evaluation ordinaria;
- una modifica actor-only viene riconosciuta senza forzare rimaterializzazione delle declaration;
- nessun input client puo scegliere il catalogo atteso o forzare il refresh;
- la risposta terminale dimostra catalogo e validation operation effettivamente usati;
- mismatch tra refresh result e prima risposta chiude il batch come errore infrastrutturale, non come risultato qualitativo.

### 22.5 Artefatti riproducibili e ruolo dello snapshot store

Rystem non aggiunge un'API di export in chiaro o un audit store permanente. `IRuntimeDescriptionSnapshotStore` resta un last-known-good operativo: memory scompare al restart, distributed e soggetto a TTL e pruning best effort. TimeVision conserva quindi un proprio artefatto immutabile dell'effective prompt snapshot usato dal run.

L'artefatto TimeVision contiene almeno:

- schema version dell'artefatto e versione del package Rystem;
- candidate ID e prompt-set/source version;
- MainActor e actor di scena effettivi;
- scene e tool descriptions effettive;
- `CatalogId`, `TemplateHash`, `ContentHash` e hash algorithm;
- `RuntimeDescriptionExecutionInfo` della richiesta di riferimento;
- fingerprint canonico dello snapshot applicativo e `ActorSnapshotId`;
- riferimenti a dataset, model configuration e build necessari alla ripetizione.

Il runner deduplica gli snapshot per fingerprint ma li riferisce da ogni trajectory. Gli artifact sono gitignored, access-controlled, cifrati at rest quando il supporto scelto lo consente e soggetti a retention esplicita. Non includono credenziali, connection string, segreti, contenuti tenant/user o dati personali non indispensabili. La policy TimeVision puo redigere o rifiutare un run prima della persistenza; una redazione che modifica istruzioni necessarie alla riproduzione deve essere dichiarata come artefatto non riproducibile.

Rystem fornisce identita e integrita; TimeVision conserva il contenuto necessario alla spiegabilita. Nessuno dei due livelli usa un hash come prova di approvazione editoriale o provenienza trusted.

Criteri di accettazione del punto:

- un run puo essere ripetuto dopo la scadenza dello snapshot store Rystem;
- actor e descriptions sono ricostruibili dallo stesso artefatto applicativo;
- trajectory e artefatto sono collegati da identificatori verificabili;
- nessun dato vietato compare nell'artefatto golden;
- eliminazione/retention degli artefatti non modifica il comportamento operativo di Rystem.

### 22.6 Latenza e conformita dell'adapter

TimeVision usa il refresh manuale una volta per candidate, fuori dalla finestra di esecuzione dei trial concorrenti. Le misure restano separate:

- durate `Source`, `Validation`, `Hash`, `Materialization`, `SnapshotStore` e `Publication` dal `RuntimeDescriptionRefreshResult`;
- `AcquisitionDuration` request-local dalla response execution info;
- `ModelExecutionDuration` dalla prima chiamata al modello alla risposta PlayFramework;
- `JudgeDuration` separata;
- `TrialTotalDuration` end-to-end, inclusa l'orchestrazione ma non usata da sola per attribuire regressioni al modello.

Il profilo rapido applica il budget principale a model execution e grading, riporta il refresh separatamente e include un budget infrastrutturale dedicato per non nascondere regressioni del reload. `EveryRequest` viene misurato nei contract test ma non entra negli SLO di produzione o nel latency grading qualitativo.

La conformita generale di `AIFunctionDeclaration` resta nella suite PlayFramework. TimeVision aggiunge un solo golden test end-to-end per l'adapter Azure realmente usato dal `ModelUnderTest`, verificando:

- declaration presente in `ChatOptions.Tools`;
- nome e schema stabili;
- description aggiornata;
- tool selezionabile dal modello/stub deterministico;
- executor corretto invocato;
- function call correlata alla richiesta HTTP della sandbox.

Non viene duplicata in TimeVision la matrice completa di adapter, execution mode, store, concorrenza e recovery gia coperta upstream.

### 22.7 Sicurezza semantica e governance TimeVision

Il confine e intenzionale:

- Rystem valida struttura, dimensioni, completezza, immutabilita, hash, template compatibility e isolamento del refresh;
- il provider applicativo governa accesso alla source, versioning e approval;
- TimeVision valida il comportamento semantico dell'effective prompt snapshot.

Prima di aprire il batch, TimeVision esegue lint e policy applicative. La quality suite include fixture avversariali per descriptions che tentano di sovrascrivere il MainActor, indurre tool non pertinenti, esporre nomi interni, rimuovere confirmation gate, allargare autorizzazioni, creare conflitti tra scene o degradare una culture. Tali test rilevano regressioni comportamentali, ma non sostituiscono code review, segregazione dei ruoli e audit della sorgente.

Il refresh manuale non viene esposto automaticamente come endpoint. Se TimeVision lo rende remoto, l'endpoint e admin-only, autenticato, autorizzato, rate limited e auditato; non accetta testo arbitrario ma soltanto il comando di rileggere una source gia configurata. Candidate e artifact non contengono segreti o dati tenant-specific.

Non viene introdotto un semantic validator nel core Rystem. Se in futuro piu consumer dimostrano la stessa necessita, un extension point verra progettato separatamente con una semantica verificabile, non aggiunto preventivamente per il solo runner.

### 22.8 Adozione, compatibilita e fallback legacy

TimeVision continua a supportare il rebuild legacy finche la feature non e pubblicata in una versione Rystem consumabile e i golden test non sono verdi. Non serve un capability service runtime dedicato:

- il package reference fornisce la capability a compile time;
- la startup validation risolve `IRuntimeDescriptionRefresher` per la factory nominata e verifica le opzioni attese;
- l'integration test verifica la presenza di `AiSceneResponse.RuntimeDescriptions` e della vista request-local;
- assenza o configurazione incompleta produce un errore di startup esplicito, non un downgrade silenzioso nello stesso run.

La versione minima esatta viene fissata nella documentazione TimeVision quando la release Rystem che contiene il contratto e pubblicata. Prima di allora l'adozione resta condizionale e il runner usa rebuild. Dopo l'adozione, rebuild rimane temporaneamente un rollback esplicito; non viene selezionato automaticamente dopo un refresh fallito, per evitare falsi risultati.

Gli scenari `runtime-description-transition`, il parallelismo tra candidate e le factory isolate dinamicamente restano nell'hardening. La v1 privilegia batch sequenziali e una sola semantica verificabile.

### 22.9 Matrice finale per approvazione TimeVision

| Tema | Decisione finale | Responsabile |
| --- | --- | --- |
| Candidate v1 | batch manuali sequenziali; trial paralleli solo nella stessa candidate | TimeVision |
| Refresh | `Manual`, startup disabilitato, barriera verificata | entrambi |
| Reload contract test | `EveryRequest`, sequenziale e fuori dagli SLO | entrambi |
| Failure | fail-closed; nessun recovery/fallback nei quality run | TimeVision su contratto Rystem |
| Version miss | `Throw` | configurazione TimeVision |
| Snapshot store evaluation | memory; distributed testato separatamente | TimeVision |
| Multi-turn | freeze del provider, nuova conversation key e verifica per turno | TimeVision |
| Prova request-local | `AiSceneResponse.RuntimeDescriptions` | Rystem |
| Actor identity | instrumentation `ActorSnapshotId` applicativa | TimeVision |
| Planner custom | vista pubblica read-only request-local | Rystem |
| Reproducibility | effective prompt snapshot completo negli artifact | TimeVision |
| Discovery | coverage/template e stato globale, mai attestazione del trial | entrambi |
| Latenza | refresh, acquisition, model, judge e total separati | entrambi |
| Adapter | conformance generale upstream; un golden Azure end-to-end | entrambi |
| Sicurezza | strutturale nel core, semantica/governance nell'applicazione | entrambi |
| Retention distributed | best effort con `IDistributedCache`; store custom per garanzie forti | Rystem/host |
| Adozione | condizionale alla release; rebuild come rollback esplicito | TimeVision |
| Transition e parallelismo candidate | hardening, non v1 | TimeVision |

Il piano e pronto per review TimeVision quando il team approva questa matrice, il profilo fail-closed e il formato dell'artefatto. L'implementazione Rystem puo quindi procedere senza API specifiche di evaluation e TimeVision puo adottarla senza affidarsi a stato globale non verificato.
