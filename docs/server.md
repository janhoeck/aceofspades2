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

Weitere Optionen: `dotnet run --project server/AoS2.Server -- --help` (Port, Map, Spielerzahl, siehe Abschnitt 3).

### 1.2 Erster Client: Unity-Editor

1. Unity Hub → *Add* → Ordner `client/`, Projekt öffnen, Szene `Assets/Scenes/Game.unity`.
2. *Play* drücken. Es erscheint das Startmenü.
3. Name eintragen, Server `127.0.0.1:32887` lassen, Haken bei **Entwicklungs-Ticket** setzen (ohne Anmeldung über den Launcher ist er schon gesetzt), **Verbinden**.

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

1. Beide Clients verbunden (Abschnitt 1.2 und 1.3), beide Fenster nebeneinander. Die Spieler erscheinen nebeneinander auf der blauen Spawn-Plattform.
2. Im einen Fenster **2** drücken, auf den anderen Spieler zielen und schießen. Bei jedem Treffer erscheint um das Fadenkreuz ein Kreuz (gelb bei Kopfschuss, rot beim tödlichen Treffer), der Getroffene leuchtet kurz auf, sein Bildschirm bekommt einen roten Rand auf der Seite des Schützen und unten links sinkt sein Leben. Rumpf 25, Kopf 50, Arme/Beine 17 Schaden; 100 Leben.
3. Bei 0 Leben zerfällt der Getroffene in Würfel; oben rechts steht der Kill. Er selbst sieht „Getötet von …“ und einen Countdown und erscheint nach **5 Sekunden** an einem Spawnpunkt mit vollem Leben und voller Munition.
4. Mit **4** eine Rakete auf eine Wand oder einen Turm schießen: Blöcke im Umkreis verschwinden, Ränder bekommen Risse, freistehende Teile fallen herunter. Spieler in der Nähe nehmen Schaden, auch man selbst.
5. Mit **3** eine Granate werfen: Sie prallt ab, bleibt liegen und explodiert nach 3 Sekunden.
6. Ein paar Schüsse mit dem Gewehr auf einen Block: Er bekommt Risse und bricht nach einigen Treffern (Erde und Gras eher, Stein später).

Werte (Schaden, Feuerrate, Magazine, Explosionsradius, Respawn-Zeit, Trefferzonen) stehen in `data/weapons.json` und `data/combat.json`. Nach einer Änderung Server **und** Spiel neu starten: Der Server entscheidet, das Spiel nutzt die Werte für die Vorhersage (Munition, Feuerrate) und die Anzeige.

Den **Lag-Ausgleich** sieht man am besten mit einem zweiten Rechner oder künstlicher Verzögerung: Man trifft den anderen dort, wo man ihn sieht, obwohl er auf dem Server schon ein Stück weiter ist. Wie das funktioniert: [architecture.md, Abschnitt Kampf](architecture.md#kampf-seit-m5).

### 1.6 Mit echter Anmeldung (lokales Backend)

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

## 3. Optionen

| Option | Standard | Bedeutung |
|---|---|---|
| `--port <zahl>` | 32887 | UDP-Port |
| `--map <name>` | `testmap` | Map aus dem `maps`-Ordner, ohne `.aos2map` |
| `--max-players <zahl>` | 32 | höchstens so viele Spieler (bis 48) |
| `--data <ordner>` | `data` neben der exe | `blocks.json`, `movement.json`, `player_rig.json`, `combat.json`, `weapons.json` |
| `--maps <ordner>` | `maps` neben der exe | Maps |
| `--site <url>` | – | Adresse der Website; von dort holt der Server die öffentlichen Schlüssel für Join-Tickets (`/api/.well-known/jwks.json`) |
| `--address <host:port>` | – | Adresse dieses Servers, wie Spieler sie eingeben. Steht im Ticket; Pflicht mit `--site` |
| `--dev` | aus | Entwicklungsmodus (Abschnitt 2) |

Beim Bauen kopiert das Projekt `data/*.json` und `maps/*.aos2map` neben die exe; ohne Angabe nutzt der Server diese Kopien.

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
