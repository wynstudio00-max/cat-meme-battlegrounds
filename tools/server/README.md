# Dedicated server + WebGL demo

A browser (WebGL) build cannot host a match. Every match runs on a **dedicated server** and the browser joins it over
**WebSocket** (all clients, including the editor, use WebSocket so they can play together).

## 1. Build (Unity menu `CatBattleground > Build`)
| Menu item | Output | Notes |
|---|---|---|
| WebGL Client | `CatBattleground/Builds/WebGL` | Gzip + decompression fallback, works on any static host |
| Windows Dedicated Server | `CatBattleground/Builds/Server_Win/CatBattlegroundServer.exe` | for testing on this PC |
| Linux Dedicated Server | `CatBattleground/Builds/Server_Linux/CatBattlegroundServer.x86_64` | for a VPS; install **Linux Dedicated Server Build Support** for this Unity version in Unity Hub first |

Run `CatBattleground > Build Phase 2 Multiplayer Scene` once before building (the builds use `Phase2_Network.unity`).

## 2. Run the server
```
CatBattlegroundServer.exe -batchmode -nographics -server -port 7777
```
Options: `-port 7777`, `-maxplayers 16`, `-path /` (WebSocket path). The log prints `[Server] Dedicated server (WebSocket) on port 7777`.
Pass cards are still the **mock service** (reveals are stored in `mock_card_reveals.json` under the server's persistent data path).

## 3. Test locally (no TLS)
1. Start the server as above (or in the editor press Play and click "Dedicated server").
2. Serve the WebGL build: `python Tools/Server/serve_webgl.py` (serves `CatBattleground/Builds/WebGL` on http://localhost:8080).
3. Open `http://localhost:8080/?host=127.0.0.1&port=7777&autoconnect=1` (add `&card=301` to pick a pass card).

URL parameters: `host`, `port`, `secure=1` (use wss), `path`, `card`, `autoconnect=1`.

## 4. Going public: wss (TLS)
A page served over HTTPS may only open `wss://` connections. Keep the game server on plain WebSocket and put a TLS proxy in front
(see `Caddyfile`): Caddy gets a certificate automatically and forwards to the server on `localhost:7777`.
Then open `https://your-site/?host=server.example.com&port=443&secure=1&autoconnect=1`.
Open the firewall for 80/443 only; keep 7777 closed to the outside.

## Still open
- Where the server is hosted (VPS provider / game-server hosting) and the domain.
- Real card service (Robinhood Chain + backend) replaces `MockCardService`; the server keeps calling `ICardService.Resolve`.
- Rate limits / max sessions per IP, reconnect handling, server restart policy (systemd or Docker).
