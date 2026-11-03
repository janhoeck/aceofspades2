# Spielserver und Netzwerk

Der Spielserver (`server/AoS2.Server`) ist eine .NET-10-Konsolenanwendung. Er rechnet das Spiel autoritativ: Bewegung, Blockänderungen und fallende Blöcke entscheidet er, die Clients zeigen an und sagen ihre eigene Bewegung voraus. Wie das im Einzelnen funktioniert, steht in [architecture.md](architecture.md#3-netzwerk).

## 1. Lokal testen: Server + zwei Clients

Für den lokalen Test braucht man weder Backend noch Discord. Der Server läuft dafür im **Entwicklungsmodus** (`--dev`) und nimmt Entwicklungs-Tickets an, die das Spiel selbst ausstellt.

### 1.1 Server starten

Im Repository-Ordner, in einem eigenen Terminal:

```powershell
dotnet run --project server/AoS2.Server -- --dev
```

Der Server zeigt eine deutliche Warnung zum Entwicklungsmodus und dann `Server lauscht auf UDP-Port 32887`. Beenden mit `Strg+C`.

Weitere Optionen: `dotnet run --project server/AoS2.Server -- --help` (Port, Map, Spieler pro Team, Punkteziel, Config-Datei, siehe Abschnitt 3).

### 1.2 Erster Client: Unity-Editor

1. Unity Hub → *Add* → Ordner `client/`, Projekt öffnen, Szene `Assets/Scenes/Game.unity`.
2. *Play* drücken. Es erscheint das Startmenü.
3. Name eintragen, Server `127.0.0.1:32887` lassen, Haken bei **Entwicklungs-Ticket** setzen (ohne Anmeldung über den Launcher ist er schon gesetzt), **Verbinden**.
4. Seit M6 erscheint das **Team-Menü**: Blau, Grün oder Automatisch wählen. Erst dann erscheint man im Spawn des Teams.

### 1.3 Zweiter Client: Windows-Build

1. Im Editor: Menü *AoS2 → Windows-Build erstellen* (oder `File → Build Profiles`). Der Build landet in `client/Builds/StandaloneWindows64/AoS2.exe`.
2. `AoS2.exe` starten, anderen Namen eintragen, verbinden wie oben.

Alternativ ohne Menü über die Kommandozeile: `AoS2.exe -connect 127.0.0.1:32887 -name Zweiter`.

Ein zweiter Rechner im selben Netz verbindet sich mit der IP-Adresse des Server-Rechners (z. B. `192.168.1.20:32887`). Die Windows-Firewall fragt beim ersten Serverstart, ob der Server im Netzwerk erreichbar sein darf.

### 1.4 Was man sehen sollte

- Beide Spieler sehen sich gegenseitig als würfeliges Spielermodell mit Animationen (Stehen, Gehen, Rennen, Ducken, Springen) und Namen darüber. Mit einer Waffe in der Hand zeigen die Arme in Blickrichtung, der Kopf neigt sich mit dem Blick.
- Die eigene Bewegung reagiert sofort (Vorhersage), die des anderen läuft flüssig, gut 100 ms verzögert (Interpolation).
- Abbauen und Bauen erscheinen sofort und beim anderen kurz danach. Lehnt der Server ab (z. B. Block in einen Mitspieler gebaut), verschwindet der Block wieder und unten steht der Grund.
- Fallende Blockgruppen fallen bei beiden gleich und landen an derselben Stelle.
- Oben links: Server, eigener Name, Ping und Spielerzahl.
- Wer später beitritt, sieht alle Änderungen seit dem Serverstart.

**Esc** gibt die Maus frei, ein zweites **Esc** trennt die Verbindung und geht zurück ins Startmenü.

### 1.5 Gegeneinander kämpfen (seit M5)

| Taste | Aktion |
|---|---|
| 1 | Spaten: Linksklick abbauen, Rechtsklick bauen, **B** wechselt den Baublock |
| 2 | Sturmgewehr (Linksklick gedrückt halten: Dauerfeuer) |
| 3 | Granate (ein Klick = ein Wurf, explodiert nach 3 s) |
| 4 | Raketenwerfer (explodiert beim Aufprall) |
| Mausrad | Waffe wechseln |
| R | nachladen (leere Magazine laden von selbst nach) |

So probiert man alles aus:

1. Beide Clients verbunden (Abschnitt 1.2 und 1.3), beide Fenster nebeneinander. Einer wählt Blau, der andere Grün (zwei in einem Team lässt der Ausgleich nicht zu, siehe 1.6). Beide laufen aus ihrem Spawn auf der Straße aufeinander zu: Im Spawn feuert keine Waffe.
2. Im einen Fenster **2** drücken, auf den anderen Spieler zielen und schießen. Bei jedem Treffer erscheint um das Fadenkreuz ein Kreuz (gelb bei Kopfschuss, rot beim tödlichen Treffer), der Getroffene leuchtet kurz auf, sein Bildschirm bekommt einen roten Rand auf der Seite des Schützen und unten links sinkt sein Leben. Rumpf 25, Kopf 50, Arme/Beine 17 Schaden; 100 Leben.
3. Bei 0 Leben zerfällt der Getroffene in Würfel; oben rechts steht der Kill. Er selbst sieht „Getötet von …“ und einen Countdown und erscheint nach **5 Sekunden** an einem Spawnpunkt mit vollem Leben und voller Munition.
4. Mit **4** eine Rakete auf eine Wand oder einen Turm schießen: Blöcke im Umkreis verschwinden, Ränder bekommen Risse, freistehende Teile fallen herunter. Spieler in der Nähe nehmen Schaden, auch man selbst.
5. Mit **3** eine Granate werfen: Sie prallt ab, bleibt liegen und explodiert nach 3 Sekunden.
6. Ein paar Schüsse mit dem Gewehr auf einen Block: Er bekommt Risse und bricht nach einigen Treffern (Erde und Gras eher, Stein später).

Werte (Schaden, Feuerrate, Magazine, Explosionsradius, Respawn-Zeit, Trefferzonen) stehen in `data/weapons.json` und `data/combat.json`. Nach einer Änderung Server **und** Spiel neu starten: Der Server entscheidet, das Spiel nutzt die Werte für die Vorhersage (Munition, Feuerrate) und die Anzeige.

Den **Lag-Ausgleich** sieht man am besten mit einem zweiten Rechner oder künstlicher Verzögerung: Man trifft den anderen dort, wo man ihn sieht, obwohl er auf dem Server schon ein Stück weiter ist. Wie das funktioniert: [architecture.md, Abschnitt Kampf](architecture.md#kampf-seit-m5).

### 1.6 Team Deathmatch ausprobieren (seit M6)

| Taste | Aktion |
|---|---|
| M | Team-Menü (Team wechseln) |
| Tab (gedrückt halten) | Punktetabelle: beide Teams, Kills und Tode |

1. Server mit kleinem Punkteziel starten, damit ein Match schnell vorbei ist:
   ```powershell
   dotnet run --project server/AoS2.Server -- --dev --score-limit 3
   ```
2. Zwei Clients verbinden (1.2 und 1.3). Im Team-Menü wählt der erste **Blau**. Der zweite sieht Blau ausgegraut (Blau hätte sonst zwei Spieler mehr) und wählt **Grün** oder **Automatisch**.
3. Oben in der Mitte steht der Punktestand. Die Spieler tragen ihre Teamfarbe, ebenso die Namen über den Köpfen und in der Kill-Anzeige.
4. **Spawnzonen ausprobieren:** Im eigenen Spawn steht unten „Spawnzone: …“, die Waffe feuert nicht. Als Grün zum blauen Spawn laufen: Man stößt an eine unsichtbare Wand. Von außen in den Spawn schießen: Die Leuchtspur endet an der Grenze, eine Rakete explodiert davor, wer drin steht, bleibt heil. Blöcke im Spawn und auf dem Weg zur Kampfzone lassen sich nicht abbauen („Hier kann man nichts verändern …“).
5. Dreimal den Gegner erwischen: „Team Blau gewinnt!“, Endstand, Punktetabelle und ein Countdown. 15 Sekunden lang kann niemand laufen, schießen oder bauen. Dann beginnt das nächste Match: Die Map ist wieder wie neu (auch gesprengte Löcher sind zu), 0 : 0, alle stehen in ihrem Spawn, die Teams bleiben.
6. **M** drücken und das Team wechseln (geht nur, wenn das andere Team danach nicht größer ist): Man stirbt und erscheint nach 5 Sekunden im anderen Spawn.

Mehr als zwei Clients auf einem Rechner: den Windows-Build mehrmals starten (`AoS2.exe -connect 127.0.0.1:32887 -name Dritter -devticket`). Viele Spieler auf einmal simuliert der Lasttest (Abschnitt 6).

### 1.7 Mit echter Anmeldung (lokales Backend)

Läuft das lokale Backend mit Website ([backend.md](backend.md#3-lokal-entwickeln)), kann der Server echte Join-Tickets prüfen:

```powershell
dotnet run --project server/AoS2.Server -- --site http://localhost:3000 --address 127.0.0.1:32887
```

Das Spiel muss dann über den Launcher gestartet sein (oder im Editor die gespeicherte Anmeldung des Launchers finden) und im Menü **ohne** Entwicklungs-Ticket verbinden. Die Server-Adresse im Menü muss genau der `--address` entsprechen, denn für diese Adresse stellt das Backend das Ticket aus.

## 2. Entwicklungsmodus

| | normal | `--dev` |
|---|---|---|
| Echte Join-Tickets (Backend) | ja, mit `--site` | ja, wenn `--site` angegeben |
| Entwicklungs-Tickets | nein | ja |
| Ohne `--site` startbar | nein | ja |

Entwicklungs-Tickets haben dasselbe Format wie echte (JWT), sind aber mit einem öffentlich bekannten Testschlüssel signiert (`AoS2.Shared/Net/DevJoinTicket.cs`). **Jeder kann sie fälschen**, mit beliebigem Namen. Deshalb:

- Der Entwicklungsmodus ist nie von selbst an, nur mit dem Schalter `--dev`.
- Beim Start zeigt der Server eine Warnung.
- Ein normaler Server lehnt Entwicklungs-Tickets ab und sagt dem Spieler, dass er sich über den Launcher anmelden muss.
- Öffentliche Server (offizielle wie Community) laufen nie mit `--dev`.

## 3. Optionen und Config-Datei

Einstellungen kommen aus einer **Config-Datei** und von der **Kommandozeile**; die Kommandozeile gilt vor der Datei.

| Option | Eintrag in der Datei | Standard | Bedeutung |
|---|---|---|---|
| `--config <datei>` | – | – | Config-Datei (JSON, Abschnitt 3.1) |
| – | `name` | `Ace of Spades 2` | Name des Servers (Log; ab M7 im Server-Browser) |
| `--port <zahl>` | `port` | 32887 | UDP-Port |
| `--map <name>` | `map` | `testmap` | Map aus dem `maps`-Ordner, ohne `.aos2map` |
| `--players-per-team <zahl>` | `playersPerTeam` | 16 | Spieler pro Team (1 bis 24); der Server nimmt doppelt so viele auf |
| `--score-limit <zahl>` | `scoreLimit` | 50 | Punkte zum Sieg |
| – | `timeLimitMinutes` | 0 (keins) | Zeitlimit; bei Ablauf gewinnt das führende Team, bei Gleichstand niemand |
| – | `matchEndSeconds` | 15 | Ergebnis-Anzeige nach dem Matchende, dann Neustart |
| – | `friendlyFire` | `false` | Teambeschuss |
| `--data <ordner>` | `data` | `data` neben der exe | `blocks.json`, `movement.json`, `player_rig.json`, `combat.json`, `weapons.json` |
| `--maps <ordner>` | `maps` | `maps` neben der exe | Maps |
| `--site <url>` | `site` | – | Adresse der Website; von dort holt der Server die öffentlichen Schlüssel für Join-Tickets (`/api/.well-known/jwks.json`) |
| `--address <host:port>` | `address` | – | Adresse dieses Servers, wie Spieler sie eingeben. Steht im Ticket; Pflicht mit `site` |
| `--dev` | `dev` | aus | Entwicklungsmodus (Abschnitt 2) |

Beim Bauen kopiert das Projekt `data/*.json`, `maps/*.aos2map` und `server.example.json` neben die exe; ohne Angabe nutzt der Server diese Kopien.

### 3.1 Config-Datei

Vorlage mit allen Einträgen und Erklärungen: [`server/AoS2.Server/server.example.json`](../server/AoS2.Server/server.example.json) (liegt auch neben der exe). Kopieren, z. B. nach `server.json`, anpassen und starten:

```powershell
dotnet run --project server/AoS2.Server -- --config server.json
```

- JSON mit `//`-Kommentaren; jeder Eintrag ist optional.
- Unbekannte Einträge sind ein Fehler (gegen Tippfehler wie `scoreLimt`), ebenso Werte außerhalb des erlaubten Bereichs. Der Server nennt den Eintrag und startet nicht.
- Relative Ordner in `data`/`maps` gelten relativ zur Config-Datei.
- Das Format bleibt für Self-Hosting (M7) gleich; dann kommen nur Einträge dazu.

### 3.2 Was der Server ins Log schreibt

Beim Start Map, Config und Regeln (`Team Deathmatch: 16 gegen 16, bis 50 Punkte, Teambeschuss aus, 15 s Pause nach dem Matchende`), danach Beitritte, Teamwahl, Kills, Matchende und Neustart. Solange Spieler da sind, kommt jede Minute eine Statuszeile:

```
Status: 6 Spieler (Blau 3, Grün 3), Punkte 12 : 9, Tick Ø 0.08 ms / max 1.2 ms, senden 41 kB/s
```

„Tick“ ist die Rechenzeit pro Simulationsschritt (Budget: 16,7 ms bei 60 Ticks/s), „senden“ die Nutzdaten an alle Clients. Steht dort „… Ticks zu langsam“, kommt der Rechner nicht hinterher.

## 4. Was beim Beitritt passiert

1. Der Client holt ein Join-Ticket (vom Backend oder als Entwicklungs-Ticket) und schickt es mit der Protokollversion in der Verbindungsanfrage.
2. Der Server lehnt sofort ab, wenn die Protokollversion nicht passt oder der Server voll ist. Sonst prüft er das Ticket (Signatur, Aussteller, Zielserver, Ablauf) und dass es noch nicht benutzt wurde.
3. Ist dasselbe Konto schon im Spiel, wird die alte Verbindung getrennt.
4. Der Server schickt die Begrüßung: Spieler-ID, Map-Name, SHA-256 der Map-Datei, Spawn. Der Client lädt die Map aus seinem `maps`-Ordner und vergleicht die Prüfsumme; passt sie nicht, trennt er mit der Meldung, das Spiel zu aktualisieren.
5. Danach kommen alle Blockänderungen seit Matchbeginn, gerade fallende Blockgruppen und die anderen Spieler. Erst dann beginnt der Client zu spielen.

Abgelehnte Clients bekommen immer einen Grund (Protokoll, Ticket, voll, …), der im Startmenü steht.

## 5. Wenn etwas schiefgeht

| Problem | Ursache / Lösung |
|---|---|
| „UDP-Port 32887 lässt sich nicht öffnen“ | Ein anderer Server läuft schon. Beenden oder `--port` ändern. |
| „Ohne --site kann der Server keine Join-Tickets prüfen“ | Zum lokalen Testen `--dev` angeben. |
| Menü: „Der Server ist nicht erreichbar“ | Server läuft nicht, falsche Adresse oder die Firewall blockiert UDP 32887. |
| Menü: „Das Join-Ticket ist ungültig … Entwicklungs-Tickets nimmt nur ein Server mit --dev an“ | Haken bei Entwicklungs-Ticket gesetzt, Server läuft ohne `--dev`. |
| Menü: „Die Map … ist eine andere Version“ | Client und Server haben verschiedene `.aos2map`-Dateien. Beide aus demselben Stand bauen. |
| Menü: „Spiel und Server passen nicht zusammen (Protokoll …)“ | Client und Server aus verschiedenen Versionen. `ProtocolInfo.Version` muss gleich sein. |
| Start: „Config-Datei …: unbekannter Eintrag …“ | Tippfehler in `server.json`. Erlaubte Einträge: Abschnitt 3. |
| Team-Menü: Team ausgegraut | Das Team ist voll oder hätte dann mehr Spieler als das andere. **Automatisch** wählt das passende. |
| Man kommt nicht weiter, ohne dass etwas zu sehen ist | Das ist die Spawnzone des Gegners (unsichtbare Wand). |

## 6. Lasttest

`tools/AoS2.LoadTest` startet viele simulierte Spieler (Bots) mit dem echten Client-Code über echtes UDP. Ohne Angabe läuft ein Server im selben Prozess, mit eigener Hauptschleife wie im Betrieb; dann misst das Werkzeug auch Tick-Dauer, Snapshot-Größe und Bandbreite:

```powershell
dotnet run -c Release --project tools/AoS2.LoadTest -- --bots 32 --seconds 30
```

Die Bots wählen automatisch ein Team, laufen zur Kartenmitte, schießen auf den nächsten Gegner, werfen Granaten, schießen Raketen und graben. Mit `--server host:port` spielen sie auf einem laufenden Server (der muss mit `--dev` laufen; Tick-Dauer und Bandbreite stehen dann in dessen Statuszeile). Ergebnisse von M6: [architecture.md, Team Deathmatch](architecture.md#team-deathmatch-seit-m6). Ein kürzerer Lauf mit 32 Bots ist Teil der Tests (`LoadTests`).

## 7. Playtest mit Freunden

Server aus dem Internet erreichbar machen, Freunde verbinden: [playtest.md](playtest.md).
