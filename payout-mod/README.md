# Wunschsumme für Scam With Your Friends

Diese separate BepInEx-Mod ersetzt die feste Belohnung des **Kreditkarten-Scams** oder **Gift-Card-Scams** durch den Preis, dem der Anrufer im Gespräch zugestimmt hat. Version **1.1.0** unterstützt beide Wege.

## Im Spiel

1. Einen neuen Anruf beginnen und den Kreditkarten- oder Gift-Card-Scam verwenden.
2. Einen eindeutigen ganzen Betrag nennen, zum Beispiel **„Das kostet 5.000 Euro“** oder **„Das kostet fünftausend Euro“**.
3. Den Anrufer überzeugen, diesem Preis zuzustimmen. Das Spiel muss das Preisziel bestätigen. Bei Gift Cards müssen zuerst das Hilfsangebot und die Lösung akzeptiert sein; die Mod ergänzt dafür pro Anruf ein optionales Preisziel.
4. Den Scam in der passenden App erfolgreich abschließen. Bei Gift Cards ist weiterhin der **korrekte fiktive Gift-Code** erforderlich. Erst dann wird die vereinbarte Summe statt der normalen 400 (Kreditkarte) beziehungsweise 200 (Gift Card) Spielgeld gutgeschrieben.

Die Zuordnung gilt getrennt pro Anruf und Scam. Kreditkarte und Gift Card können im selben Anruf verschiedene Preise haben. Die erste bestätigte Summe bleibt fest. Die vorhandene Erfolgskontrolle und die Einmal-Auszahlung bleiben aktiv. Spieler-, verfügbares und Teamgeld werden über den normalen Serverpfad aktualisiert. Das F1-Menü zeigt Preisbestätigung und Auszahlungsstatus.

Ohne bestätigten und eindeutig erkannten Preis bleibt die normale Belohnung bestehen. Dezimalbeträge werden nicht gerundet. Kartennummern und andere Nummern gelten nicht automatisch als Preis. Es gibt keine Währungsumrechnung: Die genannte Zahl ist der Betrag in Spielgeld.

Sehr große Beträge, die zusammen mit anderen möglichen Belohnungen die native Ganzzahlgrenze überschreiten würden, verwenden die Originalbelohnung. Der gemeinsame Spielkatalog wird nicht verändert; das optionale Gift-Preisziel lebt nur in einer privaten Anrufkopie.

## Installation

Das Spiel beenden und im angepassten Launcher unter **Get mods → Wunschsumme → Install…** installieren. Die Mod erscheint anschließend unter **Mods** und im F1-Menü.

Alternativ nach Entpacken des Pakets:

```powershell
.\scripts\Install-PayoutMod.ps1 -GameDirectory 'C:\Pfad\zum\Spiel' -PackageDirectory .
```

Voraussetzung ist die vorhandene BepInEx-Spielvorbereitung. Die gemeinsame Mod-Bibliothek ab 1.0.4 ist im Paket enthalten. Eine kompatible bereits installierte Bibliothek bleibt erhalten. Der Installer sichert ersetzte Dateien unter `BepInEx/requested-payout-backups`.

Im Mehrspieler muss der **Host** diese Mod laden. Sie benötigt weder ElevenLabs noch einen eigenen AI-Backend-Provider.

## Entwicklung

```powershell
.\payout-mod\build.ps1
```

Tests für Beträge und Anrufzustände sowie Tests gegen die nativen Spielmethoden mit Harmony-Patches prüfen Kreditkarte und Gift Card, falsche Codes, wiederholte Einreichungen, parallele Preise, Anrufende und Überlaufgrenzen. Zusätzlich werden Installation und Backups in isolierten Ordnern geprüft.

```powershell
dotnet run --project .\payout-mod\test\Payout.Tests.csproj
.\payout-mod\test\Run-NativeTests.ps1
.\scripts\Test-PayoutInstaller.ps1
```

Die Hooks sind an die auf diesem PC installierte Spielversion gebunden. Ein Spielupdate kann einen erneuten Build erfordern. Die Mod speichert keine Dialoge oder Kartennummern in ihren Protokollen.

Inoffizielle Community-Mod für das fiktive Spiel.
