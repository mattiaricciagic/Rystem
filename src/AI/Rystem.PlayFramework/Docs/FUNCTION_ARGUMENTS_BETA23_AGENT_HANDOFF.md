# Handoff agente: `FunctionArguments` in PlayFramework beta 23

## Obiettivo

Aggiornare il consumer a `Rystem.PlayFramework` `10.0.11-beta.23` e usare
`AiSceneResponse.FunctionArguments` come fonte primaria per verificare gli
argomenti delle chiamate tool.

## Bug corretto upstream

Fino alla beta 22, `AiSceneResponse.FunctionArguments` esisteva nel contratto
pubblico ma non veniva mai valorizzato. Gli argomenti erano usati internamente
per eseguire il tool e poi persi prima della produzione degli eventi pubblici.

La beta 23 propaga ora gli argomenti per:

- tool locali e servizi;
- endpoint HTTP;
- tool MCP;
- client tool;
- esecuzione streaming e non-streaming;
- completamenti ed errori relativi a un tool.

Anche `Rystem.PlayFramework.Adapters` è stato allineato alla versione
`10.0.11-beta.23`.

## Contratto da assumere

Quando un `AiSceneResponse` identifica un singolo tool tramite
`FunctionName != null`:

- `FunctionArguments` contiene un documento JSON valido;
- una chiamata senza argomenti produce `"{}"`, non `null`;
- gli eventi `FunctionRequest` e `FunctionCompleted` della stessa chiamata
  espongono gli stessi argomenti.

PlayFramework può emettere anche un evento aggregato:

```text
Status = FunctionRequest
Message = "LLM returned N function call(s)"
FunctionName = null
FunctionArguments = null
```

Questo evento non rappresenta un tool specifico e non deve essere valutato
come una function call individuale.

## Attività richieste all'agente consumer

1. Aggiornare entrambi i pacchetti alla beta 23:

   ```xml
   <PackageVersion Include="Rystem.PlayFramework" Version="10.0.11-beta.23" />
   <PackageVersion Include="Rystem.PlayFramework.Adapters" Version="10.0.11-beta.23" />
   ```

2. Ripristinare i pacchetti ed eseguire build e test del consumer.

3. Nel grader degli argomenti, considerare soltanto gli eventi con
   `FunctionName` valorizzato.

4. Usare `FunctionArguments` come fonte primaria e analizzarlo come JSON.
   Non confrontare la stringa JSON testualmente, perché ordine e formattazione
   delle proprietà non fanno parte del contratto.

5. Conservare temporaneamente il journal HTTP come fallback solo se i test
   devono continuare a supportare esecuzioni registrate con beta 22 o
   precedenti. Per nuove esecuzioni beta 23 il fallback non deve essere
   necessario.

6. Non registrare indiscriminatamente il JSON degli argomenti: può contenere
   dati personali o sensibili. Nei log preferire nome tool, esito e hash o una
   rappresentazione redatta.

## Esempio consumer

```csharp
var toolEvents = responses.Where(response =>
    response.FunctionName is not null &&
    (response.Status is AiResponseStatus.FunctionRequest
        or AiResponseStatus.FunctionCompleted));

foreach (var toolEvent in toolEvents)
{
    using var arguments = JsonDocument.Parse(toolEvent.FunctionArguments!);
    // Valutare arguments.RootElement senza dipendere dall'ordine delle proprietà.
}
```

## Criteri di accettazione nel consumer

- Un endpoint chiamato con argomenti complessi espone lo stesso JSON negli
  eventi `FunctionRequest` e `FunctionCompleted`.
- Una chiamata senza parametri espone `{}`.
- La verifica funziona sia con streaming abilitato sia disabilitato.
- L'evento aggregato con `FunctionName == null` viene ignorato dal grader.
- Il journal HTTP non viene consultato nelle nuove esecuzioni beta 23 quando
  `FunctionArguments` è presente.
- I log di test e CI non espongono argomenti sensibili in chiaro.

## Validazione upstream eseguita

- 8 test mirati sulla propagazione degli argomenti.
- Copertura di tool locali, endpoint, MCP, client tool, errori, JSON vuoto e
  JSON complesso.
- Copertura streaming e non-streaming.
- 227 test complessivi PlayFramework superati.
- Pacchetti Release core e adapter beta 23 generati con successo.
