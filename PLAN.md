# Umsetzungsplan für wrg

**C# und .NET 10 LTS** sind die Basis. Die Laufzeit lässt sich für Windows x64 selbständig ausliefern. Die Standardbibliothek deckt Unicode, ZIP/XML, JSON, Prozessstarts, Abbruchsignale und Windows-Dateizuordnungen ab. Die anspruchsvollen PDF- und OCR-Funktionen bleiben über getrennte Prozesse austauschbar. Das erste Release verarbeitet Dateien in der Reihenfolge der Suche und hält keinen persistenten Index.

| Etappe | Arbeit und Prüfkriterium | Stand |
| --- | --- | --- |
| 1 · CLI und Suchvertrag | CLI, lokale Pfade, Exitcodes, Text und JSONL definieren. A1 und A7 mit synthetischen Dokumenten prüfen. | Kern implementiert, Streaming-Test mit absichtlich blockierter Folgequelle offen |
| 2 · Dokumente und Suchregeln | TXT, Markdown und DOCX inklusive Metadaten. Unicode, Phrasen, Tippfehler und Originaltext prüfen. A2, A4 und A5. | Implementiert und Smoke-Tests ausgeführt. Grenzfälle erweitern |
| 3 · PDF und OCR | Seitenweise Poppler-Extraktion, Bildheuristik und lokales Tesseract. Gemischte, leere, beschädigte und geschützte PDFs prüfen. A3 und A8. | Gemischtes PDF geprüft. Windows-native Abdeckung, Timeout und Abbruch ausbauen |
| 4 · Interaktive Navigation | Pfeiltasten, Kontext und Dateiauswahl. Test-Doppel für SumatraPDF und manueller Windows-Test für Dateizuordnung. A6. | Implementiert. Automatisierte und manuelle Abnahme offen |
| 5 · Portable Auslieferung | Native Binärdateien und Sprachdaten versions- und hashgebunden bündeln. Auf sauberem Windows 11 x64 mit PowerShell 5.1 und 7, offline und ohne Adminrechte prüfen. Lizenzhinweise und Weitergabepflichten vervollständigen. A9. | Buildskript und Windows-CI vorbereitet. Freigabe offen |

Die Windows-Abnahme prüft zusätzlich UNC-Pfade, gemappte Netzlaufwerke, OneDrive-Platzhalter und Reparse Points. Weitere Regressionstests prüfen Unicode-Grenzfälle, unlesbare Ordner, PDF-Seitensprung, lokale Mitternacht, Ctrl+C und JSONL während eines Quellenfehlers. Erst nach A1–A9 folgt ein freigabefähiges Release. Ein Index kommt erst nach Messungen an repräsentativen Beständen in Betracht.
