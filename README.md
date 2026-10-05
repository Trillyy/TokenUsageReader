# Token Usage Reader

App per la tray di Windows (le icone in basso a destra, vicino all'orologio) che mostra il consumo di **Claude** e **GitHub Copilot**.

- L'icona mostra la **percentuale più alta** tra i servizi, su sfondo arancione Claude (grigio finché non ci sono dati).
- Passando sopra l'icona compare un riepilogo con una riga per servizio.
- Il clic (sinistro o destro) apre un menu con il dettaglio, più i comandi *Aggiorna ora*, *Configurazione…*, *Avvia con Windows* ed *Esci*.
- Il doppio clic apre direttamente la configurazione.
- Aspetto in stile Windows 11: il menu e la finestra di configurazione seguono il tema chiaro/scuro di Windows (anche se lo cambi mentre l'app è aperta) e usano il colore d'accento di sistema, i font Segoe UI Variable e gli angoli arrotondati.

## Requisiti

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download), solo per compilare: `winget install Microsoft.DotNet.SDK.10`

## Compilazione ed esecuzione

```powershell
dotnet run -c Release
```

Per creare un singolo `.exe` che non richiede .NET installato:

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
# output: bin\Release\net10.0-windows\win-x64\publish\TokenUsageReader.exe
```

Per avviarla all'accesso, usa la voce **Avvia con Windows** del menu. Scrive il percorso dell'eseguibile in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, quindi va attivata dall'`.exe` pubblicato e non da `dotnet run`.

Può girare una sola istanza alla volta.

## Fonti dei dati

| Servizio | Fonte | Autenticazione | Note |
|---|---|---|---|
| Claude: limiti dell'abbonamento | `https://api.anthropic.com/api/oauth/usage`, lo stesso endpoint di `/usage` in Claude Code | Token OAuth in `~/.claude/.credentials.json` | **Non documentato**, può cambiare. Mostra le finestre 5h e settimanale (e Opus/Sonnet se presenti). Il token non viene rinnovato dall'app: se è scaduto, apri Claude Code. |
| Claude: token locali | `~/.claude/projects/**/*.jsonl` | Nessuna | Token di oggi e del mese, cache compresa. I messaggi duplicati vengono contati una volta sola. |
| Copilot: CLI locale | `~/.copilot/session-state/*/events.jsonl` (eventi `session.usage_checkpoint` / `session.shutdown`) | Nessuna | Premium request di oggi e del mese consumate dalla CLI `copilot`. Non include l'uso da IDE o github.com. |
| Copilot (`internal`) | `https://api.github.com/copilot_internal/user` | Token GitHub | **Non documentato**. Mostra la % della quota premium e la data di reset. |
| Copilot (`billing`) | `GET /users/{username}/settings/billing/premium_request/usage` | PAT fine-grained con permesso *Plan: read* | API ufficiale. La percentuale è calcolata su `monthlyPremiumQuota`. |

Il token GitHub viene cercato in quest'ordine:
1. il token scritto nella finestra di configurazione (salvato nel Credential Manager di Windows alla voce `TokenUsageReader:copilot-token`);
2. il token salvato da `copilot login` nel Credential Manager di Windows, alla voce `{host}:{login}.copilot-cli` (host e login vengono letti da `~/.copilot/config.json`);
3. la variabile `GITHUB_TOKEN`;
4. l'output di `gh auth token`.

Se hai già fatto login con la CLI `copilot`, non serve configurare nulla.

## Configurazione

La configurazione si modifica dalla finestra **Configurazione…**, che ha tre schede: *Generale*, *Claude* e *Copilot*. Con **Salva** le modifiche vengono applicate subito, senza riavviare l'app, e parte un aggiornamento. **Annulla** (o Esc) chiude la finestra senza salvare.

Le impostazioni sono salvate in `%APPDATA%\TokenUsageReader\config.json`, creato al primo avvio. Il file si può aprire dal link *Apri config.json* nella finestra. Se lo modifichi a mano, devi riavviare l'app. Un file non valido viene copiato in `config.json.bak` e sostituito dai valori predefiniti.

```json
{
  "language": "it",
  "autoRefresh": true,
  "refreshMinutes": 5,
  "claude": {
    "enabled": true,
    "showSubscriptionLimits": true,
    "showLocalTokens": true
  },
  "copilot": {
    "enabled": true,
    "mode": "internal",
    "username": null,
    "monthlyPremiumQuota": 300,
    "showLocalCliUsage": true
  }
}
```

| Chiave | Significato |
|---|---|
| `language` | Lingua dell'interfaccia: `it` (italiano) o `en` (inglese). Se manca, segue la lingua di Windows (italiano se Windows è in italiano, altrimenti inglese). |
| `autoRefresh` | Attiva o disattiva l'aggiornamento automatico. Se è disattivato, i dati si aggiornano solo con *Aggiorna ora*. |
| `refreshMinutes` | Ogni quanti minuti avviene l'aggiornamento automatico (da 1 a 1440). Claude e Copilot vengono letti a ogni aggiornamento. |
| `copilot.mode` | `internal` oppure `billing`. |
| `copilot.showLocalCliUsage` | Mostra le premium request lette dalle sessioni locali della CLI `copilot`. |
| `copilot.username` / `monthlyPremiumQuota` | Servono solo in modalità `billing`. La quota è 300 per Pro e 1500 per Pro+. |

Il token GitHub non compare in `config.json`: quello scritto nella finestra va nel Credential Manager di Windows. Un `"token"` lasciato in `config.json` da versioni precedenti viene spostato lì al primo avvio e tolto dal file.

### Testi e lingue

Tutti i testi dell'interfaccia stanno in `Strings.resx` (inglese, usato anche come ripiego) e `Strings.it.resx` (italiano); il codice li legge tramite la classe `Strings`, dove ogni proprietà ha lo stesso nome della chiave. Per aggiungere una lingua: crea `Strings.<codice>.resx` con le stesse chiavi, aggiungi il valore ad `AppLanguage` e alla lista delle lingue in `SettingsForm`.

## Struttura del progetto

```
Program.cs              Avvio e controllo dell'istanza singola
AppConfig.cs            Modello della configurazione, caricamento e salvataggio di config.json
SettingsForm.cs         Finestra di configurazione
TrayContext.cs          NotifyIcon, menu, timer, cache, avvio automatico e applicazione della configurazione
TrayIconRenderer.cs     Disegno dell'icona (percentuale e colore)
Theme.cs                Tema chiaro/scuro, colore d'accento e font di Windows 11
Win11MenuRenderer.cs    Disegno del menu in stile Windows 11
WindowChrome.cs         Angoli arrotondati e bordo del menu tramite DWM
Shapes.cs               Rettangoli arrotondati
Strings.cs              Accesso tipizzato ai testi di Strings.resx / Strings.it.resx
Providers/
  IUsageProvider.cs     Interfaccia comune e modello UsageResult/UsageLine
  ClaudeProvider.cs
  CopilotProvider.cs
  CopilotCli.cs         Token e sessioni locali della CLI `copilot`
  JsonlTail.cs          Lettura incrementale dei log .jsonl (solo le righe nuove)
  WindowsCredentials.cs Lettura e scrittura nel Credential Manager di Windows
```

### Aggiungere un servizio

1. Crea una classe che implementa `IUsageProvider`:
   - `Name`: il nome mostrato nel menu.
   - `MinInterval`: l'intervallo minimo tra due chiamate reali; nel frattempo l'app usa il risultato in cache.
   - `FetchAsync`: restituisce le righe da mostrare. Se valorizzi `Percent`, la riga concorre al colore dell'icona.
2. Aggiungi la sua sezione in `AppConfig` e una scheda in `SettingsForm`.
3. Registrala in `TrayContext.ApplyConfig`.

Gli errori non vanno lanciati: restituisci `UsageResult.Fail(...)` e l'errore comparirà nel menu con l'icona ⚠.

## Problemi noti

- Gli endpoint `oauth/usage` (Claude) e `copilot_internal/user` (Copilot) sono interni e possono smettere di funzionare senza preavviso.
- Il tooltip di Windows è limitato a 127 caratteri: per il dettaglio completo usa il menu.
