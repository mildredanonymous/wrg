# JSON Lines, Version 1

`--format jsonl` schreibt UTF-8 ohne BOM. Jede Zeile ist ein Objekt. `version` ist die Ganzzahl 1 und `type` einer der folgenden Werte. Nach `start` folgen Treffer und Meldungen. Bei regulärem Ende folgt genau ein `summary`. Bei erzwungenem Abbruch oder geschlossenem Ausgabekanal kann es fehlen. Konsumenten dürfen zusätzliche Felder ignorieren.

| Typ | Felder |
| --- | --- |
| `start` | `query` als String, `paths` als Array absoluter Pfade, `mode` als `default`, `exact` oder `phrase` |
| `match` | Absolute `path`, `filename`, `file_type` ohne Punkt, `modified_utc` als ISO-8601-UTC-Zeit oder `null`, `field` als `content`, `filename`, `title` oder `author`, `source` als `text`, `metadata` oder `ocr`, `page`, `line`, `paragraph` als positive Ganzzahlen oder `null`, `section` als String oder `null`, `match_kind` als `exact`, `spelling_variant`, `ocr_variant` oder `fuzzy`, `edit_distance` als Ganzzahl, `matched_text` als Array von Strings in Dokumentreihenfolge, `snippet` als String |
| `issue` | `code` und `message` als Strings, `path` absolut oder `null`, `page` als Ganzzahl oder `null`, `severity` als `info`, `warning` oder `error` |
| `summary` | `status` als `complete`, `partial`, `failed` oder `cancelled`. Ganzzahlen in `exit_code`, `matches`, `files_searched`, `files_skipped`, `files_excluded`, `pages_skipped` und `pages_excluded` |

JSONL hat keine globale Trefferrangfolge. Bei frühen CLI-Fehlern stehen ein leerer Suchtext und eine leere Pfadliste in `start`, sofern JSONL eindeutig angefordert wurde.

| Exitcode | Bedeutung |
| --- | --- |
| 0 | Vollständige Suche mit Treffer oder erfolgreicher Aufruf von Hilfe oder Version |
| 1 | Vollständige Suche ohne Treffer |
| 2 | Aufruf ungültig, alle Quellen wegen Fehler unverwertbar oder Öffnen fehlgeschlagen |
| 3 | Suche mit Lücken |
| 130 | Kontrollierter Abbruch |

| Kennung | Bedeutung |
| --- | --- |
| `invalid_arguments` | Aufruf oder Terminalmodus ungültig |
| `path_not_found` | Datei oder Ordner fehlt |
| `access_denied` | Zugriff nicht erlaubt |
| `unsupported_type` | Explizite Datei mit unbekanntem Typ |
| `unsupported_location` | Netzwerkpfad, Gerätepfad, Verknüpfung oder Onlineplatzhalter |
| `decode_failed` | Ungültige Zeichenkodierung |
| `pdf_locked` | PDF verlangt zum Öffnen ein Passwort |
| `pdf_unavailable` | PDF-Komponente fehlt |
| `parse_failed` | Dokument oder Seite konnte nicht verarbeitet werden, einschliesslich Ressourcenlimits |
| `ocr_unavailable` | OCR-Komponente oder Sprachdaten fehlen oder das OCR-Programm meldet einen Fehler |
| `ocr_timeout` | Rendering oder Texterkennung hat das Zeitlimit überschritten |
| `cleanup_failed` | Temporäre OCR-Dateien konnten nicht entfernt werden |
| `open_failed` | Gewähltes Original konnte nicht geöffnet werden |
| `cancelled` | Suche wurde kontrolliert abgebrochen |
