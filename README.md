# Community-Mods für Scam With Your Friends

Dieses Repository enthält **ElevenLabs Agents 1.0.1**, **Wunschsumme 1.1.0** für Kreditkarten und Gift Cards sowie einen angepassten Windows-Launcher mit beiden Mod-Einträgen. Fertige Pakete stehen unter [Releases](https://github.com/h7sd/scam-with-your-friends-mods/releases).

Für die komplette Einrichtung das Paket `SWYF-Mods-1.1.0.zip` herunterladen und entpacken. Für eine bereits vorbereitete Spielinstallation gibt es zusätzlich das kleine Paket `Wunschsumme-SWYF-1.1.0.zip`.

## ElevenLabs Agents

Lokale BepInEx-Mod mit angepasstem Launcher. Spracheingabe, Gesprächsantworten und Sprachausgabe laufen über **ElevenLabs Agents**. Cursor wird beim Spielen nicht verwendet.

## Start

1. `Start-ElevenLabs.cmd` im Ordner `dist` öffnen. Die lokale Einrichtung erscheint unter `http://127.0.0.1:8765`.
2. ElevenLabs-API-Schlüssel und die gewünschte Voice-ID eintragen und **Lokal speichern** wählen.
3. **Neuen Spiel-Agenten anlegen** wählen. Dadurch werden ein Agent und das Client-Tool `submit_game_turn` in eurem ElevenLabs-Konto erstellt. Alternativ können ein passend eingerichteter Agent und seine Tool-ID verbunden werden.
4. **Agent prüfen**, dann **Antwort und Stimme testen**. Der Gesprächstest verbraucht ElevenLabs-Agents-Nutzung.
5. Im angepassten Launcher erscheint **ElevenLabs Agents** unter **Get mods**. Nach Installation steht die Mod auch unter **Mods** und im Spiel im F1-Menü.

Version 1.0.1 verwendet standardmäßig die **Charakter-Sprache** und `eleven_v4_turbo`. Die Einstellung **Deine Mikrofon-Sprache** bleibt davon unabhängig, beispielsweise Deutsch. Für bestehende Agenten aktiviert **Charakter-Stimmen aktivieren** die nötigen Stimmen-Overrides.

Sollen alle Anrufer Deutsch antworten, **Anrufer-Sprache → Deine Sprache** wählen und **Deine Mikrofon-Sprache → Deutsch** einstellen. Passende Charakter-Stimmen bleiben dabei erhalten.

Beim ersten Anruf wählt das Backend aus den verfügbaren ElevenLabs-Stimmen eine passende Stimme anhand der hinterlegten Sprache, des Geschlechts und des Alters. Diese Zuordnung bleibt pro Charakter erhalten. Unter **Anrufer und Stimmen** lassen sich erkannte Anrufer laden, Stimmen anhören und Sprache oder Stimme einzeln ändern. Die Vorschau speichert keine Änderungen. **Automatische Auswahl** setzt eigene Änderungen zurück. Fehlt eine explizite Charakter-Sprache, wird der Spielstandard Englisch verwendet; Nationalität oder Name bestimmen die Sprache nicht.

Die normale Modliste des originalen Launchers wird vom Projekt `swyf-modding` verwaltet. Deshalb wird für den zusätzlichen lokalen Eintrag eine separate Launcher-Version mitgeliefert.

## Was die Mod übernimmt

Die Mod erweitert die vorhandene AI-Backend-Schnittstelle. Sie leitet Dialoge, Zielprüfungen und Reviews an das lokale Backend weiter. Für Dialoge meldet der Agent Vertrauen und Emotion über ein begrenztes Client-Tool und spricht anschließend den Dialog. Die Mod gibt das erwartete JSON an das Spiel zurück; technische Statuswerte werden nicht vorgelesen.

Audio wird als PCM16 mit 24 kHz empfangen und in den bestehenden 20-ms-Audio- und Mehrspielerpfad des Spiels eingespeist. Anruf-ID, Untertitel und Abschlussmeldungen bleiben über die bestehenden Spielereignisse verbunden.

Für die Spracheingabe nutzt die Mod die vorhandene Aufnahme, Spracherkennungspausen, VAD und Resampling des Spiels. Ein fertiger Sprechabschnitt wird als PCM16 mit 16 kHz an eine ElevenLabs-Agents-Sitzung gesendet. Nach dem Transkript schließt diese Sitzung; die eigentliche Antwort wird mit dem anschließend verfügbaren Spielkontext in einer getrennten Agents-Sitzung erzeugt. Dadurch fallen zusätzliche Sitzungsstarts an. Kontinuierliches Mikrofonstreaming mit einer einzigen Gesprächssitzung ist in dieser Version nicht umgesetzt.

## Voraussetzungen und Grenzen

- Windows x64, Node.js ab 22.13 und die eingerichtete BepInEx-Installation.
- Die Mod wurde gegen die auf diesem PC installierten Spielassemblies gebaut. Andere Spielupdates können Änderungen an den Hooks erfordern.
- ElevenLabs-Zugang mit Agents-, Tool- und Agent-Erstellungsrechten; eine nutzbare Voice-ID und Zugriff auf die Stimmenbibliothek. Der Agent muss die Stimmen-Overrides erlauben.
- Tatsächliche Latenz, Erkennungsqualität und Mehrspieler-Audio müssen mit dem eigenen Konto im Spiel geprüft werden. Lokale Protokolltests ersetzen diesen Live-Test nicht.
- Die Auswahl hängt von den im Konto verfügbaren Stimmen und deren Metadaten ab. Fehlt eine passende Stimme oder ist die Bibliothek nicht erreichbar, bleibt die konfigurierte Standardstimme als gekennzeichneter Fallback verfügbar. Die ursprünglichen Spielstimmen werden nicht automatisch nach ElevenLabs geklont.

API-Schlüssel liegen lokal in `bridge/.env` als Klartext. Sie werden weder in der Spielkonfiguration gespeichert noch in Statusantworten ausgegeben. Der lokale Dienst bindet ausschließlich an `127.0.0.1` und weist fremde Origins und Hosts zurück.

Lokale Stimmenzuordnungen liegen in `bridge/caller-profiles.json`. Zugangsdaten, Stimmenzuordnungen und Laufzeitprotokolle sind vom ZIP-Paket ausgeschlossen. Bei Nutzung aus `dist` gelten entsprechend `dist/bridge/.env` und `dist/bridge/caller-profiles.json`.

Wenn der Dienst fehlt oder ElevenLabs eine Anfrage ablehnt, zeigt die aktivierte Mod einen Fehler. Deaktivieren im F1-Menü bzw. der Mod-Konfiguration stellt die ursprünglichen Provider wieder her.

## Entwicklung

```powershell
.\scripts\Get-BuildDependencies.ps1
cd bridge
npm ci
npm test
cd ..
.\mod\build.ps1 -NoCopy
.\payout-mod\build.ps1
.\scripts\Stage-Release.ps1
.\scripts\Build-Launcher.ps1
.\scripts\Package-Mod.ps1 -FileName SWYF-Mods-1.1.0.zip
.\scripts\Package-PayoutMod.ps1
```

`scripts/Get-BuildDependencies.ps1` lädt die unten genannten Quellabhängigkeiten mit festgelegten Git-Revisionen nach `upstream/`. Benötigt werden Git, Node.js ab 22.13 und das .NET-8-SDK. Ein optionaler portabler Compiler kann unter `build/tools/dotnet` liegen. Zum C#-Build werden die lokal installierten Spielassemblies und die vorbereitete BepInEx-Installation benötigt; Spielassemblies werden nicht mitgeliefert. Der Launcher enthält die offiziellen Setup-Dateien einschließlich ihrer Lizenzhinweise.

Verifiziert: lokale Node-Tests einschließlich getrennter Charakter-/Spielersprache, stabiler Stimmenzuordnung, paralleler Anrufer und identischer Begrüßungen; zusätzlich C#-Kontexttests, PCM-Puffertests und Hook-Signaturen der installierten Spielversion. Echte Agent-Antworten mit sprach- und stimmenspezifischen Overrides sowie ein sauber beendeter PCM-Audiostream wurden mit dem konfigurierten Konto geprüft. Das Backend sendet während der Sprachausgabe und nach Mikrofonaufnahmen stille Audio-Pakete weiter, damit Agents die Ausgabe und kurze Sprechabschnitte abschließt. Vollständige Gesprächs- und Mehrspielertests bleiben nötig.

Für eine manuelle Installation: `scripts/Install-Mod.ps1 -PackageDirectory dist`. Bestehende Dateien werden unter `BepInEx/elevenlabs-agents-backups` gesichert. Installation bei laufendem Spiel wird abgelehnt.

## Quellen

- [AI-Backend](https://github.com/swyf-modding/AI-Backend), [gemeinsame Mod-Bibliothek](https://github.com/swyf-modding/mod-lib), [Launcher](https://github.com/swyf-modding/Launcher), jeweils mit den mitgelieferten Lizenzhinweisen.
- [ElevenLabs Agents WebSocket](https://elevenlabs.io/docs/eleven-agents/api-reference/eleven-agents/websocket), [Konfigurations-Overrides](https://elevenlabs.io/docs/eleven-agents/customization/personalization/overrides), [Client-Ereignisse](https://elevenlabs.io/docs/eleven-agents/customization/events/client-events).
- [Sprachmodelle](https://elevenlabs.io/docs/overview/models), [Agent-Stimmen](https://elevenlabs.io/docs/eleven-agents/customization/voice), [Stimmenbibliothek](https://elevenlabs.io/docs/api-reference/voices/search).

Inoffizielle Community-Mod; keine Verbindung zum Spieleentwickler.

## Zusätzliche Mod: Wunschsumme

Die separate **Wunschsumme**-Mod ersetzt beim erfolgreichen Kreditkarten- oder Gift-Card-Scam die feste Belohnung durch den im Gespräch vereinbarten ganzen Betrag. Beispielsweise werden aus „Das kostet fünftausend Euro“ nach bestätigter Preiszustimmung und erfolgreichem Abschluss 5.000 Spielgeld. Bei Gift Cards muss weiterhin der richtige fiktive Gift-Code eingereicht werden. Die normale Erfolgskontrolle bleibt aktiv; das bloße Nennen einer Summe zahlt kein Geld aus.

Sie funktioniert unabhängig vom Sprach-Backend. Im Mehrspieler muss der Host sie laden. Der angepasste Launcher bietet dafür einen eigenen **Get mods**-Eintrag. Anleitung und Grenzen stehen in `payout-mod/README.md`; manueller Installer ist `scripts/Install-PayoutMod.ps1`.
