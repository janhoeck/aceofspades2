# Eigenen Server hosten

Jeder kann einen Ace-of-Spades-2-Server betreiben. Er erscheint im **Server-Browser** des Spiels, sobald er von außen erreichbar ist. Einen Server-Schlüssel oder ein Konto brauchst du dafür nicht.

Du brauchst:

- einen Rechner, der läuft, solange gespielt wird: Windows, Linux (x64 oder arm64, z. B. Raspberry Pi 4/5) oder irgendetwas mit Docker,
- eine **öffentliche Adresse**: einen Mietserver (VPS) oder einen Internet-Anschluss, bei dem du einen UDP-Port im Router weiterleiten kannst,
- genug **Upload**: Bei 32 Spielern verschickt der Server gut 7 Mbit/s, bei 16 Spielern etwa 2 Mbit/s (Messwerte: [architecture.md](architecture.md#team-deathmatch-seit-m6)). Rechenleistung braucht er kaum.

## 1. So funktioniert es

1. Du startest den Server mit einer Config (`server.json`). Darin stehen Name, Port, Map, Regeln, die Adresse der offiziellen Website (`site`) und die Adresse, unter der Spieler deinen Server erreichen (`address`).
2. Beim Start holt der Server von der Website die Schlüssel, mit denen er **Join-Tickets** prüft. Ein Join-Ticket bekommt jeder angemeldete Spieler vom Backend, für genau deinen Server. So weißt du, wer mitspielt (Discord-Name), ohne dass dein Server Zugangsdaten sieht.
3. Etwa alle 30 Sekunden schickt der Server einen **Heartbeat** an die Website: Name, Map, Spielerzahl, Version, Adresse.
4. Beim ersten Heartbeat prüft das Backend, ob dein Server **von außen erreichbar** ist: Es schickt ein UDP-Paket an `address`, und dein Server muss richtig antworten. Erst dann steht er in der Liste.
5. Im Spiel lädt der Server-Browser die Liste und misst den **Ping** zu jedem Server direkt.
6. Schickt dein Server 90 Sekunden lang keinen Heartbeat (abgestürzt, ausgeschaltet, keine Verbindung), fällt er aus der Liste. Beim normalen Beenden meldet er sich sofort ab.

Spieler mit einer anderen Spielversion (Protokollversion) sehen deinen Server nicht. Aktualisiere ihn deshalb nach jedem Release (Abschnitt 7).

## 2. Server holen

### 2.1 Download (Windows, Linux)

Auf [github.com/janhoeck/aceofspades2/releases](https://github.com/janhoeck/aceofspades2/releases) beim neuesten Release die Datei für dein System laden:

| System | Datei |
|---|---|
| Windows | `AoS2.Server-<Version>-win-x64.zip` |
| Linux, normaler PC oder VPS | `AoS2.Server-<Version>-linux-x64.tar.gz` |
| Linux auf ARM (Raspberry Pi, ARM-VPS) | `AoS2.Server-<Version>-linux-arm64.tar.gz` |

Der Server bringt seine .NET-Laufzeit mit, du musst nichts weiter installieren. Entpacken:

```bash
tar -xzf AoS2.Server-0.7.0-linux-x64.tar.gz      # Linux; unter Windows: Rechtsklick → Alle extrahieren
cd AoS2.Server
cp server.example.json server.json               # Windows: server.example.json kopieren und umbenennen
```

Im Ordner liegen `AoS2.Server` (unter Windows `AoS2.Server.exe`), `server.example.json`, die Ordner `data` und `maps` und eine kurze `README.txt`.

### 2.2 Docker

Das Image gibt es für amd64 und arm64: `ghcr.io/janhoeck/aos2-server`. Es erwartet die Config unter `/config/server.json`:

```bash
mkdir config
docker run --rm --entrypoint cat ghcr.io/janhoeck/aos2-server /app/server.example.json > config/server.json   # Vorlage holen
# config/server.json anpassen (Abschnitt 3), dann:
docker run -d --name aos2-server --restart unless-stopped -p 32887:32887/udp -v "$PWD/config:/config:ro" ghcr.io/janhoeck/aos2-server
docker logs -f aos2-server
```

Wichtig ist `/udp` bei `-p`: Ohne die Angabe gibt Docker nur TCP frei, und der Server wäre nicht erreichbar.

Mit Docker Compose:

```yaml
services:
  aos2-server:
    image: ghcr.io/janhoeck/aos2-server:latest
    restart: unless-stopped
    ports:
      - "32887:32887/udp"
    volumes:
      - ./config:/config:ro
```

Statt der Datei gehen auch Optionen auf der Kommandozeile (sie gelten vor der Datei), z. B. `docker run … ghcr.io/janhoeck/aos2-server --config /config/server.json --name "Mein Server"`.

## 3. Config

Die Vorlage `server.example.json` erklärt jeden Eintrag. Für einen öffentlichen Server reicht:

```jsonc
{
  "name": "Jans Grabenkrieg",                  // im Server-Browser, höchstens 40 Zeichen
  "port": 32887,
  "map": "testmap",
  "playersPerTeam": 16,
  "scoreLimit": 50,
  "site": "https://<offizielle Website>",      // dieselbe Adresse wie im Launcher
  "address": "203.0.113.5:32887",              // deine öffentliche Adresse mit Port (Abschnitt 4.3)
  "listed": true,                              // im Server-Browser anzeigen
  "dev": false
}
```

| Eintrag | Bedeutung |
|---|---|
| `name` | Name im Server-Browser. 1 bis 40 Zeichen, keine Zeilenumbrüche. |
| `port` | UDP-Port, auf dem der Server lauscht. Muss zur Portweiterleitung passen. |
| `site` | Adresse der offiziellen Website. Ohne sie gibt es weder Join-Tickets noch Server-Browser. |
| `address` | So erreichen Spieler deinen Server: öffentliche IP-Adresse oder Domainname, mit Port. Für genau diese Adresse stellt das Backend die Join-Tickets aus. Keine Adresse aus dem Heimnetz (`192.168.…`, `10.…`) und nicht `localhost`. |
| `listed` | `false` = nicht im Server-Browser. Spieler kommen dann nur über **Direkt verbinden** mit der Adresse auf den Server (z. B. für eine geschlossene Runde). |
| `dev` | Entwicklungsmodus. **Auf einem öffentlichen Server immer `false`**: Im Entwicklungsmodus kann jeder mit beliebigem Namen beitreten. Solche Server erscheinen nie im Browser. |

Alle Einträge und Kommandozeilen-Optionen: [server.md](server.md#3-optionen-und-config-datei). Ein unbekannter Eintrag (Tippfehler) ist ein Fehler, der Server nennt ihn und startet nicht.

## 4. Port freigeben

Spieler kommen über **UDP** auf deinen Server (Standard-Port 32887). Diesen Port müssen alle Stellen dazwischen durchlassen.

### 4.1 Mietserver (VPS)

Meist reicht die Firewall des Servers, bei manchen Anbietern zusätzlich eine Firewall in deren Weboberfläche:

```bash
sudo ufw allow 32887/udp          # Ubuntu/Debian mit ufw
```

### 4.2 Zu Hause

Zwei Stellen: die Firewall des Rechners und der Router.

- **Windows-Firewall:** Beim ersten Start fragt Windows, ob der Server im Netzwerk erreichbar sein darf. „Zulassen“ wählen, auch für öffentliche Netzwerke. Oder in einer PowerShell als Administrator:
  ```powershell
  New-NetFirewallRule -DisplayName "AoS2 Server (UDP 32887)" -Direction Inbound -Protocol UDP -LocalPort 32887 -Action Allow
  ```
- **Router:** eine Portweiterleitung für **UDP 32887** auf den Server-Rechner einrichten und dem Rechner im Router immer dieselbe lokale Adresse geben. Wie das bei einer FRITZ!Box geht und was bei **DS-Lite** (keine eigene IPv4-Adresse) zu tun ist, steht Schritt für Schritt in [playtest.md, Abschnitt 3](playtest.md#3-port-32887udp-freigeben).

### 4.3 Die richtige `address`

- **Öffentliche IPv4-Adresse:** steht auf <https://ifconfig.me> oder in der Router-Übersicht, z. B. `203.0.113.5:32887`.
- **Wechselnde Adresse zu Hause:** Die meisten Anschlüsse bekommen ab und zu eine neue IP-Adresse. Dann besser einen DynDNS-Namen nehmen (FRITZ!Box: MyFRITZ!, sonst z. B. DuckDNS), etwa `jan.duckdns.org:32887`. Ändert sich die Adresse, findet das Backend den Server beim nächsten Heartbeat unter dem Namen wieder.
- **Weitergeleiteter Port weicht ab:** Leitet der Router z. B. den externen Port 40000 auf 32887 weiter, schreibst du `"address": "…:40000"` und `"port": 32887`. Der Server weist beim Start darauf hin, dass die beiden Ports verschieden sind.

## 5. Starten und prüfen

```bash
./AoS2.Server --config server.json        # Linux
AoS2.Server.exe --config server.json      # Windows (Eingabeaufforderung oder PowerShell im Ordner)
```

Im Log steht nacheinander:

```
"Jans Grabenkrieg": Ace of Spades 2 Server 0.7.0, Protokoll 3, Map testmap (128 × 64 × 128), Config server.json, Join-Tickets von https://… für 203.0.113.5:32887
Team Deathmatch: 16 gegen 16, bis 50 Punkte, Teambeschuss aus, 15 s Pause nach dem Matchende
Server lauscht auf UDP-Port 32887
Im Server-Browser eingetragen (https://…): "Jans Grabenkrieg" unter 203.0.113.5:32887
```

Die letzte Zeile ist der Beweis, dass dein Server von außen erreichbar ist: Das Backend hat ihn über das Internet gefragt, und er hat geantwortet. Im Spiel steht er dann im Reiter **Server-Browser**.

Beenden mit `Strg+C` (Docker: `docker stop aos2-server`). Der Server trennt die Spieler mit einer Meldung und meldet sich aus der Liste ab.

**Dauerhaft betreiben (Linux):** am einfachsten mit Docker und `--restart unless-stopped`. Ohne Docker als systemd-Dienst, z. B. `/etc/systemd/system/aos2-server.service`:

```ini
[Unit]
Description=Ace of Spades 2 Server
After=network-online.target
Wants=network-online.target

[Service]
User=aos2
WorkingDirectory=/opt/aos2/AoS2.Server
ExecStart=/opt/aos2/AoS2.Server/AoS2.Server --config server.json
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

Dann `sudo systemctl enable --now aos2-server`, das Log zeigt `journalctl -u aos2-server -f`.

## 6. Fehlersuche

Steht im Log statt „Im Server-Browser eingetragen“ eine Warnung `Server-Browser: nicht eingetragen. … Neuer Versuch in 60 s.`, nennt sie den Grund:

| Meldung | Ursache / Lösung |
|---|---|
| „… von außen nicht erreichbar (keine Antwort über UDP)“ | Die häufigste: Portweiterleitung im Router fehlt oder zeigt auf den falschen Rechner, die Firewall blockiert, bei Docker fehlt `/udp`, oder `address` stimmt nicht (alte IP-Adresse, DS-Lite). Abschnitt 4 durchgehen. Der Server versucht es jede Minute erneut, du musst ihn nach der Korrektur nicht neu starten. |
| „… ist keine öffentliche Adresse (z. B. Heimnetz 192.168.… oder localhost)“ | In `address` steht die lokale Adresse. Die öffentliche eintragen (4.3). |
| „Der Name … lässt sich nicht auflösen (DNS)“ | Tippfehler im Domainnamen, oder der DNS-Eintrag fehlt noch. |
| „Unter … antwortet ein anderer Spielserver“ | Unter dieser Adresse läuft ein anderer Server, z. B. ein zweiter auf demselben Port. Jeder Server braucht seinen eigenen Port. |
| „Von dieser IP-Adresse sind schon 8 Server eingetragen“ | Gegen Spam stehen pro IP-Adresse höchstens 8 Server in der Liste. |
| „Zu viele Anmeldeversuche von dieser IP-Adresse“ | Viele fehlgeschlagene Prüfungen kurz hintereinander. Der Server wartet und versucht es dann von selbst wieder. |
| „Die Website … ist nicht erreichbar“ | `site` falsch, keine Internetverbindung, oder die Website ist gerade gestört. |
| Start: „Ohne site … kann der Server keine Join-Tickets prüfen“ | `site` fehlt in der Config. |
| Start: „Config-Datei …: unbekannter Eintrag …“ | Tippfehler in `server.json`. |
| Start: „UDP-Port … lässt sich nicht öffnen“ | Auf dem Port läuft schon etwas (ein zweiter Server?). Anderen Port nehmen und die Freigabe anpassen. |
| Im Log `Nicht im Server-Browser: Server im Entwicklungsmodus …` | `"dev": true`. Für einen öffentlichen Server auf `false` setzen. |
| Spieler: „Das Join-Ticket ist ungültig …“ | Bei **Direkt verbinden** eine andere Adresse eingetippt als in `address` (z. B. IP statt Domainname). Im Server-Browser passiert das nicht. Oder die Uhr des Server-Rechners geht stark falsch. |
| Spieler sehen den Server nicht, obwohl er eingetragen ist | Andere Spielversion (Protokollversion). Server und Spiel aktualisieren. Oder ein Filter im Browser („Volle ausblenden“, Map) ist aktiv. |
| Ping „keine Antwort“ bei einem Spieler, bei anderen nicht | Das Netz des Spielers blockiert ausgehendes UDP (manche Firmen- oder Hotel-WLANs). |

**Von außen selbst prüfen:** Im Spiel unter **Direkt verbinden** die Adresse eintragen und **Prüfen** klicken. Das Spiel fragt den Server direkt per UDP und zeigt Name, Map, Spieler und Ping, oder warum keine Antwort kommt. Das klappt am aussagekräftigsten von einem anderen Anschluss aus (z. B. Laptop am Handy-Hotspot). Aus dem eigenen Heimnetz klappt die öffentliche Adresse bei manchen Routern nicht, obwohl der Server von außen erreichbar ist.

## 7. Aktualisieren

Nach jedem Release die neue Version holen, sonst sehen Spieler mit der neuen Version deinen Server nicht:

- **Download:** neuen Ordner entpacken, deine `server.json` hineinkopieren, neu starten.
- **Docker:** `docker pull ghcr.io/janhoeck/aos2-server && docker rm -f aos2-server`, dann denselben `docker run`-Befehl wie in 2.2. Mit Compose: `docker compose pull && docker compose up -d`.

Die Config bleibt gleich; neue Einträge sind immer optional.

## 8. Was du als Betreiber wissen solltest

- Dein Server entscheidet alles im Spiel: Treffer, Blöcke, Punkte. Spieler müssen dir dabei nicht vertrauen, was ihre Anmeldung angeht: Dein Server sieht nur ein Join-Ticket mit Konto-ID und Discord-Namen, nie ein Passwort oder Sitzungstoken.
- Waffen- und Klassenwerte kannst du in `data/weapons.json`, `data/classes.json` und `data/combat.json` neben dem Server ändern (z. B. mehr Blöcke für alle). Nach einem Neustart bekommen die Spieler deine Werte beim Beitritt automatisch, sie brauchen kein anderes Spiel. Ungültige Werte meldet der Server beim Start mit Datei und Eintrag. Was die Werte bedeuten und wie man sie prüft: [server.md, Abschnitt 1.8](server.md#18-werte-nachjustieren-balancing). Beim Aktualisieren des Servers werden die Dateien überschrieben, heb deine Änderungen also auf.
- Statistiken oder Ranglisten (falls es sie einmal gibt) zählen nur auf offiziellen Servern.
- Offizielle Server stehen im Browser oben und tragen einen Stern. Wie sie betrieben werden: [backend.md, Abschnitt 4.6](backend.md#46-offizielle-spielserver).
