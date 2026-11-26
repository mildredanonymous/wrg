# wrg

Windows-11-Kommandozeile für die lokale Stichwortsuche in TXT, Markdown, DOCX und PDF. PDF-Scans werden offline mit deutschen und englischen OCR-Sprachdaten durchsucht.

```powershell
.\wrg.exe --help
.\wrg.exe 'Kündigungsfrist' '.\Verträge 2026'
.\wrg.exe 'TLS Zertifikat' .\Handbuecher --exact --format jsonl
.\wrg.exe 'fristlose Kündigung' .\Vertraege --phrase --type pdf --interactive
```

Mehrere Suchbegriffe stehen in einem PowerShell-Argument. Standardmässig müssen alle in derselben Zeile, demselben Absatz, derselben Tabellenzelle, derselben PDF-Seite oder demselben Metadatenfeld vorkommen. `--phrase` sucht die ganze Wortfolge. `--exact` schaltet Tippfehler- und Schreibvarianten ab. Ohne Pfad beginnt die Suche im aktuellen Ordner.

In der Trefferliste wählen Auf/Ab den Treffer. Enter öffnet das Original und lässt die Liste aktiv. Mit SumatraPDF ab 3.2 bekommt jeder PDF-Seitensprung ein eigenes Fenster. Ein Treffer in Dateiname, Titel oder Autor springt zum bestplatzierten Inhaltstreffer im selben PDF, wenn die Suche einen gefunden hat. Mit `--field content` werden nur Inhaltstreffer angezeigt. Esc beendet die Auswahl. Geöffnete Dokumente bleiben im Viewer offen. Für einen PDF-Seitensprung sucht `wrg` nach `SumatraPDF.exe` auf `PATH` und in den üblichen Windows-Installationsordnern. Mit `WRG_SUMATRA_PATH` kannst du den vollständigen Pfad zur EXE vorgeben. Die Windows-Dateizuordnung öffnet das Dokument alternativ und `wrg` zeigt die Zielseite im Terminal an.

## In grossen Ordnern suchen

`wrg` liest die Dateien bei jeder Suche direkt. Bei einer Autorensuche in Word-Dokumenten spart `--type docx --field author --exact` das Lesen der Dokumenttexte und der PDFs. Für eine Inhaltssuche ohne Scan-Erkennung lässt sich `--no-ocr` verwenden. Die sortierte Textausgabe erscheint nach der Suche und zeigt Dateiname, Fundstelle, Vorschau und bei Bedarf den Ordner. Fortschrittsmeldungen erscheinen im interaktiven Modus und mit `--verbose`. Temporäre Word-Sperrdateien mit dem Präfix `~$` werden übersprungen. Mit `--format jsonl` werden die Treffer während der Suche ausgegeben.

### Frühe Inhaltstreffer und wiederholte Scan-Suchen

```powershell
wrg --field content --stream 'Kündigungsfrist'
wrg --field content --stream --ocr-cache 'Kündigungsfrist'
```

`--stream` zeigt Texttreffer in der Reihenfolge ihrer Verfügbarkeit. Der Standardmodus und die interaktive Liste sortieren weiterhin nach Trefferqualität. Für PDFs liest `wrg` Text- und Bildinformationen in Blöcken von bis zu 16 Seiten. Ein OCR-Worker verarbeitet höchstens 64 vorgemerkte Scan-Seiten parallel zur laufenden Suche. Wenn die Warteschlange voll ist, wartet die Suche auf einen freien Platz. Eine Seite mit Textebene und OCR erscheint einmal mit dem bestplatzierten Treffer.

`--ocr-cache` speichert erkannten Scan-Text im Klartext unter `%LOCALAPPDATA%\wrg\ocr-v1` und ist pro Aufruf aktiv. Der Cache verwendet einen Fingerabdruck des gerenderten Bildes, der OCR-Engine und der Sprachmodelle. Er kann auch identische Bilder in verschiedenen PDFs wiederverwenden. Die PDF-Seite wird weiterhin gerendert. Der Platzbedarf der Einträge ist auf 256 MiB begrenzt. Der Cache-Ordner kann zum Leeren gelöscht werden. Unter Linux braucht der Cache für eine systemweite Tesseract-Installation einen gesetzten `TESSDATA_PREFIX` mit den verwendeten Sprachmodellen.

## Build und Prüfung

Für den Build werden das in `global.json` fixierte .NET SDK, Windows PowerShell 5.1 oder PowerShell 7, 7-Zip mit NSIS-Unterstützung sowie Internetzugang zum Abruf der festgelegten Komponenten benötigt.

```powershell
dotnet build src -c Release
dotnet run --project tests -c Release -- src/bin/Release/net10.0/wrg.dll
.\scripts\Build-Portable.ps1 -SevenZip "$env:ProgramFiles\7-Zip\7z.exe"
```

Das Skript erstellt `artifacts/wrg-win-x64.zip`. Das entpackte Verzeichnis enthält `wrg.exe`, Laufzeit, PDF-Tools und beide OCR-Modelle. Unter Linux verwenden die PDF-Smoke-Tests die lokalen Poppler- und Tesseract-Programme. Für den zusätzlichen PDF-Test übergibt man `tests/fixtures/gemischt.pdf` als zweites Argument an das Testprogramm.

## PDF-Heuristik und Grenzen

Bei mindestens 80 Buchstaben oder Ziffern auf mindestens drei Textzeilen wird die vorhandene Textebene verwendet. Sonst prüft Poppler, ob ein Bild mindestens 240 Pixel je Kante und 150’000 Pixel Fläche aufweist. Kandidaten werden mit maximal 2’400 Pixeln an der längsten Kante gerendert. Bei gleich starken Treffern auf derselben Seite hat die Textebene Vorrang. Eine reine Bildseite kann mit `--no-ocr` ausgeschlossen werden. Dateilimit 256 MiB, TXT und Markdown 32 MiB, entpacktes DOCX 128 MiB bzw. 10’000 ZIP-Einträge und je XML-Datei 32 MiB. PDF-Informations- und Extraktionsprozesse haben 20 Sekunden, Rendering 30 Sekunden und OCR 45 Sekunden. PDF-Dokumente werden bis zur 10’000. Seite verarbeitet.

[Umsetzungsplan](PLAN.md) · [JSONL-Schema und Exitcodes](JSONL.md) · [Lizenzhinweise](LIZENZHINWEISE.md)
