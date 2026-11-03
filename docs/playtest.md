# Playtest mit Freunden

Anleitung für den ersten richtigen Playtest (M6): Du startest einen Server auf deinem Rechner, gibst ihn im Internet frei, und deine Freunde treten über das Spiel bei. Gespielt wird Team Deathmatch, Blau gegen Grün ([Regeln](game-design.md#2-spielmodus)).

## 1. Zwei Wege zum Beitreten

| | Weg A: Entwicklungs-Tickets (empfohlen für den ersten Test) | Weg B: echte Anmeldung |
|---|---|---|
| Spiel | ZIP mit einem Windows-Build, den du baust | über den Launcher (Discord-Anmeldung) |
| Server | `"dev": true` | `"site"` und `"address"` in der Config |
| Backend und Website | nicht nötig | müssen online sein |
| Was jeder braucht | die ZIP-Datei | Launcher + Discord-Konto + die aktuelle Spielversion im Launcher |
| Risiko | Wer Adresse und Port kennt, kann mit beliebigem Namen beitreten | keins (nur angemeldete Spieler, echter Discord-Name) |

**Warum Weg A zuerst:** Für Weg B muss die Spielversion mit M6 über den Release-Workflow in den Launcher kommen, und der schlägt seit M2.1 fehl (eigenes Thema). Weg A braucht davon nichts. Der Entwicklungsmodus ist für einen Abend mit Freunden in Ordnung: Ein Fremder müsste deine Adresse kennen und könnte höchstens mitspielen. Danach den Server beenden und die Portfreigabe wieder schließen.

**Wichtig für beide Wege:** Alle brauchen **genau dieselbe Version** wie der Server (Protokoll 3 und dieselbe `testmap.aos2map`). Sonst meldet das Spiel „Spiel und Server passen nicht zusammen“ oder „Die Map … ist eine andere Version“. Server und Spiel also aus demselben Stand des Repositorys bauen.

## 2. Server vorbereiten

### 2.1 Bauen

Im Repository-Ordner:

```powershell
dotnet publish server/AoS2.Server -c Release -o publish/server
```

In `publish/server` liegen danach `AoS2.Server.exe`, die Spieldaten (`data/`), die Map (`maps/`) und die Vorlage `server.example.json`. Auf dem Rechner muss .NET 10 installiert sein (ist es, wenn du das SDK hast).

### 2.2 Config

`publish/server/server.example.json` nach `publish/server/server.json` kopieren und für den Playtest anpassen. Für Weg A zum Beispiel:

```jsonc
{
  "name": "Playtest bei Jan",
  "port": 32887,
  "map": "testmap",
  "playersPerTeam": 8,      // passt zur Testmap und zur Runde; 16 geht auch
  "scoreLimit": 30,         // mit wenigen Spielern sind 50 Punkte lang
  "timeLimitMinutes": 15,   // damit Matches nicht ewig dauern
  "matchEndSeconds": 15,
  "friendlyFire": false,
  "dev": true,              // Weg A: Entwicklungs-Tickets
  "site": null,
  "address": null
}
```

Alle Einträge sind in der Vorlage erklärt ([server.md, Abschnitt 3](server.md#3-optionen-und-config-datei)). Für Weg B statt `dev`:

```jsonc
  "dev": false,
  "site": "https://<deine Website>",          // wie im Launcher eingestellt
  "address": "<öffentliche Adresse>:32887"    // genau so, wie deine Freunde sie im Spiel eintippen
```

### 2.3 Starten

```powershell
cd publish/server
./AoS2.Server.exe --config server.json
```

Der Server zeigt die Regeln (`Team Deathmatch: 8 gegen 8, bis 30 Punkte oder 15 min …`) und `Server lauscht auf UDP-Port 32887`. Im Entwicklungsmodus kommt zusätzlich eine deutliche Warnung. Beenden mit `Strg+C`. Solange jemand spielt, schreibt der Server jede Minute eine Statuszeile (Spieler, Punkte, Rechenzeit, Datenmenge).

## 3. Port 32887/UDP freigeben

Deine Freunde erreichen den Server über deinen Internet-Anschluss. Dafür müssen zwei Stellen den Port durchlassen: die **Windows-Firewall** und der **Router**.

### 3.1 Windows-Firewall

Beim ersten Start fragt Windows, ob `AoS2.Server` im Netzwerk erreichbar sein darf: **Zugriff zulassen** (auch für öffentliche Netzwerke, falls dein Netz so eingestuft ist). Oder fest per Regel, in einer PowerShell **als Administrator**:

```powershell
New-NetFirewallRule -DisplayName "AoS2 Server (UDP 32887)" -Direction Inbound -Protocol UDP -LocalPort 32887 -Action Allow
```

Nach dem Playtest wieder entfernen: `Remove-NetFirewallRule -DisplayName "AoS2 Server (UDP 32887)"`.

### 3.2 Router: Portweiterleitung

1. Lokale IPv4-Adresse des Server-Rechners herausfinden: `ipconfig` → „IPv4-Adresse“, z. B. `192.168.178.20`.
2. Im Router eine Portweiterleitung einrichten: **UDP**, Port **32887** (extern und intern), Ziel dieser Rechner. Bei einer FRITZ!Box: *Internet → Freigaben → Portfreigaben → Gerät für Freigaben hinzufügen* → den Rechner wählen → *Neue Freigabe* → *Portfreigabe*, Protokoll **UDP**, Port 32887, „Freigabe aktiv“. Andere Router heißen das oft „Port Forwarding“ oder „Virtueller Server“.
3. Damit die lokale Adresse gleich bleibt: im Router „diesem Gerät immer die gleiche IPv4-Adresse zuweisen“.

### 3.3 Öffentliche Adresse

Deine öffentliche IPv4-Adresse steht z. B. auf <https://ifconfig.me> oder in der Router-Übersicht. Sie ist das, was deine Freunde eintippen: `203.0.113.5:32887` (Beispiel). Die meisten Anschlüsse bekommen gelegentlich eine neue Adresse; für einen Abend reicht es, sie kurz vorher nachzusehen. Dauerhaft hilft ein DynDNS-Name (bei der FRITZ!Box: MyFRITZ!).

### 3.4 Wenn es keine eigene IPv4-Adresse gibt (DS-Lite)

Viele Kabel- und Glasfaser-Anschlüsse haben **DS-Lite**: Man teilt sich eine IPv4-Adresse mit anderen Kunden, eine IPv4-Portweiterleitung ist dann **unmöglich**. Erkennen: Der Router zeigt „DS-Lite“ oder eine IPv4-Adresse, die nicht zu ifconfig.me passt (oft `100.64…`). Auswege:

- **VPN unter Freunden** (am einfachsten): z. B. Tailscale oder ZeroTier. Alle installieren es und treten deinem Netz bei; deine Freunde verbinden sich mit deiner VPN-Adresse (`100.x.y.z:32887`). Keine Portfreigabe nötig, und nur Leute im VPN kommen an den Server (gut zu Weg A).
- **IPv6:** Der Server lauscht auch auf IPv6. Im Router eine IPv6-Freigabe für den Rechner einrichten, Freunde tippen `[2001:db8::20]:32887` (Adresse aus `ipconfig`, die dauerhafte, nicht die „temporäre“). Klappt nur, wenn alle IPv6 haben.
- **Kleiner Mietserver** (VPS) mit eigener IPv4-Adresse; dort läuft der Server genauso (`dotnet` installieren, Ordner `publish/server` hochladen). Das ist auch der Weg der offiziellen Server ab M7.

### 3.5 Prüfen

- Du selbst verbindest dich am Server-Rechner mit `127.0.0.1:32887` (die eigene öffentliche Adresse funktioniert von innen oft nicht, das ist normal).
- Von außen: am besten ein Freund probiert es vorab, oder ein Laptop über einen Handy-Hotspot. Kommt „Der Server ist nicht erreichbar“, liegt es fast immer an Firewall, Portweiterleitung oder DS-Lite.

## 4. Freunde verbinden

### Weg A: Windows-Build mit Entwicklungs-Ticket

1. Build erstellen: Unity-Editor, Projekt `client/` → Menü **AoS2 → Windows-Build erstellen**. Ergebnis: `client/Builds/StandaloneWindows64/`.
2. Den ganzen Ordner `StandaloneWindows64` als ZIP packen und deinen Freunden schicken (z. B. als Cloud-Link; das ZIP ist zu groß für die meisten Mail-Anhänge).
3. Deine Freunde entpacken das ZIP und starten `AoS2.exe`. Windows warnt, weil das Spiel nicht signiert ist: *Weitere Informationen → Trotzdem ausführen*.
4. Im Startmenü: **Name** eintragen, **Server** = deine Adresse (z. B. `203.0.113.5:32887`), Haken bei **Entwicklungs-Ticket** (ohne Launcher ist er schon gesetzt), **Verbinden**.
5. Team wählen, los. Alternativ direkt per Verknüpfung: `AoS2.exe -connect 203.0.113.5:32887 -name Max -devticket`.

### Weg B: Launcher mit Discord-Anmeldung

Voraussetzungen (alles aus [backend.md](backend.md) und [release.md](release.md)): Website und Backend laufen, die Discord-Anmeldung funktioniert, die Spielversion mit M6 ist über den Release-Workflow im Update-Speicher, und der Server-Rechner erreicht die Website (beim Start holt er dort die Schlüssel für die Join-Tickets).

1. Deine Freunde installieren den Launcher (`AoS2.Launcher-win-Setup.exe`), melden sich mit Discord an und lassen das Spiel aktualisieren.
2. **Spielen** → im Startmenü als Server **genau** die Adresse aus `"address"` eintragen (mit Port), Haken bei Entwicklungs-Ticket **aus**, **Verbinden**.
3. Kommt „Das Join-Ticket ist ungültig …“, passt die eingegebene Adresse nicht zu `"address"`: Das Backend stellt das Ticket genau für die eingegebene Adresse aus, und der Server nimmt nur Tickets für seine eigene Adresse an.

## 5. Am Playtest-Abend

1. Server starten (2.3), Log ansehen: Regeln, `lauscht auf UDP-Port 32887`.
2. Selbst mit `127.0.0.1:32887` verbinden, Team wählen.
3. Freunde verbinden lassen. Wer beitritt, steht im Server-Log (`… ist beigetreten`, `… spielt jetzt für Blau`).
4. Teams: Jeder wählt im Menü; ein Team darf höchstens einen Spieler mehr haben. **M** wechselt das Team, **Tab** zeigt die Punktetabelle.
5. Ein Match endet bei 30 Punkten (oder nach 15 Minuten), dann 15 Sekunden Ergebnis und automatisch das nächste Match auf frischer Map.
6. Danach: Server mit `Strg+C` beenden, Portweiterleitung und Firewall-Regel wieder entfernen (vor allem bei Weg A).

**Wie viel Internet braucht der Server?** Vor allem **Upload** (gemessen mit dem Lasttest, Werte mit Puffer):

| Spieler | Upload des Servers | Download pro Spieler |
|---|---|---|
| 4 | ca. 0,2 Mbit/s | ca. 50 kbit/s |
| 8 | ca. 0,6 Mbit/s | ca. 70 kbit/s |
| 16 | ca. 2 Mbit/s | ca. 120 kbit/s |
| 32 | ca. 7,5 Mbit/s | ca. 230 kbit/s |

Ein üblicher Anschluss mit 10 Mbit/s Upload reicht also auch für 16 gegen 16, solange niemand nebenher große Dateien hochlädt. Die Rechenzeit des Servers ist kein Thema (unter 1 ms pro Tick bei 32 Spielern).

## 6. Worauf achten, was notieren

Das ist der erste Test mit echten Leitungen. Hilfreich für die nächsten Meilensteine:

- **Treffer:** Fühlt sich das Treffen fair an (Lag-Ausgleich)? „Um die Ecke gestorben“ ist normal, aber wie oft und wie stark?
- **Ruckeln:** Springen eigene Bewegungen zurück (Korrekturen), ruckeln andere Spieler? Ping steht oben links.
- **Spawnzonen:** Ist die unsichtbare Wand verständlich? Wird vor dem Spawn gecampt?
- **Teams und Punkte:** Ist das Menü klar? Passt 30/50 Punkte zur Spielerzahl? Zeitlimit sinnvoll?
- **Map und Waffen:** Was fehlt, was nervt, was ist zu stark?
- **Server-Log:** Die Statuszeilen (Tick, Datenmenge) und Fehler aufheben, z. B. mit `./AoS2.Server.exe --config server.json | Tee-Object playtest.log`.

## 7. Wenn etwas schiefgeht

| Problem | Ursache / Lösung |
|---|---|
| „Der Server ist nicht erreichbar“ (nur Freunde) | Firewall (3.1), Portweiterleitung (3.2), falsche oder geänderte öffentliche Adresse (3.3), DS-Lite (3.4). |
| „Der Server ist nicht erreichbar“ (auch du lokal) | Server läuft nicht oder anderer Port; Server-Log ansehen. |
| „Spiel und Server passen nicht zusammen (Protokoll …)“ | Spiel und Server aus verschiedenen Ständen: alle brauchen denselben Build. |
| „Die Map … ist eine andere Version“ | Dasselbe für die Map: Server und Spiel aus demselben Stand bauen. |
| „Das Join-Ticket ist ungültig … Entwicklungs-Tickets nimmt nur ein Server mit --dev an“ | Weg A, aber der Server läuft ohne `"dev": true`. |
| „Das Join-Ticket ist ungültig …“ (Weg B) | Eingegebene Adresse ≠ `"address"`, oder die Uhr des Server-Rechners geht stark falsch. |
| „Der Server ist voll“ | `playersPerTeam` erhöhen (Server neu starten). |
| Server startet nicht: „Die Website … ist nicht erreichbar“ | Weg B: `site` falsch oder Website aus. Für Weg A `"site": null` und `"dev": true`. |
| Server startet nicht: „UDP-Port 32887 lässt sich nicht öffnen“ | Läuft schon ein Server (z. B. ein zweites Fenster)? Beenden oder anderen Port nehmen (dann auch die Freigabe ändern). |
| Hohe Pings, Ruckeln bei allen | Upload des Anschlusses ausgelastet (Abschnitt 5), oder WLAN am Server-Rechner: besser per Kabel. |
