# tomyum-bpy-api

A [TomYum](https://github.com/osu-somtum/TomYum) plugin that serves **bancho.py's public v1 API** (from
bancho.py's `app/api/v1/api.py`, the osu!somtum fork) on top of TomYum's data, so tools and bots written
for bancho.py keep working against a TomYum server. bancho.py's own extras (its API-key and session
endpoints, matches, mappools and the Discord bot's actions) aren't part of it.

- Same paths, parameters, JSON shapes and errors as bancho.py, including FastAPI's `422 {"detail": [...]}`
  for parameters that don't validate, `404 {"detail": "Not Found"}` for unknown paths and `405` for a
  known path with the wrong method.
- Served on bancho.py's own host, `https://api.<domain>/v1/...`, and on the website's host,
  `https://osu.<domain>/bpy-api/v1/...`.
- Read-only: every query is a `SELECT`. Nothing is ever written.
- A **loose-source** plugin (a folder with `plugin.json` and `.cs` files that TomYum compiles at start-up).
  It references only `TomYum.Plugins.Abstractions`.

## Where the data comes from

| bancho.py | TomYum |
|---|---|
| `users` | the website's `phpbb_users`. Restricted (`user_warnings`) and deleted accounts are left out of everything, the same way the website and TomYum hide them. |
| `priv` | made from the website's groups: `UNRESTRICTED`, `VERIFIED` (visible accounts), `SUPPORTER` (osu!supporter), `WHITELISTED` (TomYum's anti-cheat whitelist), `ALUMNI` (alumni), `TOURNEY_MANAGER` (tc), `NOMINATOR` (bng, bng_limited), `MODERATOR` (gmt), `ADMINISTRATOR` (admin), `DEVELOPER` (dev), `NAT` (nat), `HELPER` (spt). TomYum has nothing for `PREMIUM` or `VOTER`. |
| `scores`, modes 0-3 (vanilla) | the website's `scores`: stable plays (mirrored there by TomYum) and lazer plays. Relax/Autopilot plays in it are left out, as on the website's vanilla boards. A stable play takes its exact score, mods and hit counts from TomYum's row; a lazer play's are converted the way the website converts them for osu!'s legacy API. |
| `scores`, modes 4-6 and 8 (Relax, Autopilot) | TomYum's `scores` (stable plays and the lazer RX/AP plays TomYum takes in). |
| `stats` | vanilla: the website's `osu_user_stats*`; Relax/Autopilot: TomYum's `user_stats` (grade counts and max combo from the player's best scores). |
| `maps` | the website's `osu_beatmaps` and `osu_beatmapsets`, with TomYum's ranked status (bancho.py's numbers: 0 pending, 2 ranked, 3 approved, 4 qualified, 5 loved). |
| `ranking_history` | vanilla: the website's daily rank history (`osu_user_performance_rank`); Relax/Autopilot: TomYum's `mode_rank_history`. |
| `clans` | the website's teams (`teams`, `team_members`). The team leader is the clan owner; everyone else is "Member". |
| `achievements` | the website's medals (`osu_achievements`, `osu_user_achievements`). |
| sessions online | the host's live view: osu!stable and IRC sessions, plus lazer players (from the spectator server) when the host can tell. |

### Score ids

Vanilla scores keep the website's score ids. TomYum's Relax/Autopilot scores get their TomYum id **plus
10,000,000,000** (`modeScoreIdOffset`), so the two kinds never share an id. `/get_score_info` and
`/get_replay` take either kind, and the website id of a lazer Relax/Autopilot play finds that play too.

### Things that work a little differently from bancho.py

- **Restricted players are hidden**: they aren't found by name or id (404 "Player not found."), and their
  scores, ranks and listings are left out. `/get_all_users?restricted=true` finds nobody, and
  `/statistics` gives `latest_restricted_player: null` (it still counts them).
- **Vanilla scores mix stable and lazer.** A stable play's `score` is its classic score; a lazer play's is
  lazer's (standardised) score. Map leaderboards are ordered by the website's standardised score, like
  the website. `status` 2 is a player's best play on the map by pp (then score), as bancho.py marks it.
- `time_elapsed` is known for lazer plays only (0 for stable); `client_flags` is 0 and `online_checksum` "".
- Relax/Autopilot stats have no play time (`playtime` 0, so sorting the Relax/Autopilot leaderboard by
  `playtime` lists nobody) and no replay views.
- Rank history has no daily country rank (`country_rank` 0).
- `donor_end` is when osu!supporter runs out; `silence_end` comes from the website's silences.
  `userpage_content` is the website's userpage (BBCode), `location`/`interests`/`occupation` its profile fields.
- `/search_players` lists at most 1000 players (`searchLimit`; bancho.py has no limit).
- There are no DAN maps: `is_dan` is always false and `dan_only=true` finds nothing.
- The fork's `somtum` block (`/get_map_info`, `/get_beatmapset_info`) is there for sets uploaded to this
  server (TomYum's custom sets), with the uploader and the cover and preview URLs; `votes` is always empty
  (TomYum has no vote queue).

## Endpoints

### Served from TomYum's data

| Endpoint | Notes |
|---|---|
| `GET /search_players` | |
| `GET /get_player_count` | `clans` counts the website's teams. |
| `GET /get_player_info` | `scope` = `stats`, `info`, `all` or `history`. |
| `GET /special_players` | Role labels from the website's groups (see `priv` above). |
| `GET /get_recent_scores` | Every mode. |
| `GET /get_recent_players` | Bots left out. |
| `GET /get_top_osu_std_scores` | osu! in Vanilla, Relax and Autopilot. |
| `GET /get_player_status` | Online players from the host (stable/IRC); `login_time` is when the plugin saw the login (null for players who logged in before the server started it). Lazer-only players show as offline with `last_seen`. |
| `GET /get_player_scores` | `scope` = `recent`, `best`, `first`; `mods`, `mode`, `limit`, `include_loved`, `include_failed`. |
| `GET /get_player_achievements` | The website's medals. |
| `GET /get_player_most_played` | |
| `GET /get_map_info` | |
| `GET /get_beatmapset_info` | |
| `GET /get_map_scores` | `scope` = `recent`, `best`. |
| `GET /get_score_info` | |
| `GET /get_replay` | Stable replays get bancho.py's header; lazer replays are the spectator server's complete `.osr`. `include_headers=false` gives the replay frames only. |
| `GET /get_leaderboard` | `sort` = `tscore`, `rscore`, `pp`, `acc`, `plays`, `playtime`; `country`. |
| `GET /statistics` | |
| `GET /get_all_users` | Bots left out. |
| `GET /get_top_play` | |
| `GET /get_clan` | A website team. |
| `GET /get_clans_leaderboard` | |
| `GET /search_beatmaps` | Official and uploaded sets. `osu_status` matches an official set's status on the website. |
| `GET /get_total_scores` | The website's scores plus TomYum's stable Relax/Autopilot plays. |
| `GET /get_rank_one_history` | |

## Configuration

Settings go under `Plugins:tomyum.bpy-api` (all optional):

```yaml
plugins:
  trusted:
    - "tomyum.bpy-api"            # required: the plugin reads the database through IPluginContext.Data
  "tomyum.bpy-api":
    osuWebDatabase: "osu"         # the website's database
    tomyumDatabase: "tomyum"      # TomYum's database
    apiSubdomain: "api"           # bancho.py's host: api.<domain> ("" = every host)
    apiPrefix: "/v1"
    websiteSubdomain: "osu"       # also on osu.<domain>/bpy-api/v1 ("" = every host)
    websitePrefix: "/bpy-api/v1"  # "" turns this one off
    modeScoreIdOffset: 10000000000
    replaysPath: "data/replays"   # TomYum's Replays:Path (stable replays)
    lazerReplaysPath: "/app/lazer-replays"  # TomYum's Circleguard:LazerReplaysPath (lazer replays)
    domain: ""                    # for links to uploaded sets' covers; "" = the request's host without its first label
    version: "1.0.0+tomyum-bpy-api"   # what /statistics reports
    searchLimit: 1000
    inactiveDays: 0               # the rankings' inactivity rule, as TomYum's Ranking:InactiveDays (0 = off)
```

As environment variables: `TOMYUM_PLUGINS__TRUSTED__<n>=tomyum.bpy-api`, and for example
`TOMYUM_PLUGINS__tomyum.bpy-api__searchLimit=500`.

Without `Plugins:Trusted` the plugin still loads, logs a warning, and the endpoints that need the database
answer `503 {"status": "tomyum.bpy-api has no database access: add it to Plugins:Trusted."}`.

The plugin needs `Plugins:AllowLooseSource` (on by default) and TomYum's database user must be able to read
the website's database (TomYum's own user can).

## Deploying (blueskychan.dev)

1. **Mount the plugin** into the server container and trust it, in
   `TomYum/deploy/production/docker-compose.override.yml` (the server-specific, untracked file):

   ```yaml
     server:
       volumes:
         - ../../plugins/tomyum-metrics:/app/plugins/tomyum-metrics:ro
         - ../../../tomyum-bpy-api/plugin:/app/plugins/tomyum-bpy-api:ro
       environment:
         TOMYUM_PLUGINS__TRUSTED__0: tomyum.metrics
         TOMYUM_PLUGINS__TRUSTED__1: tomyum.bpy-api
   ```

   Mount only `plugin/`: the loader compiles every `.cs` under the folder, and `tests/` must stay out.

2. **Route the hosts to TomYum** in the stack's Caddy (`TomYum/deploy/production/Caddyfile`). A new site
   for `api.`:

   ```caddyfile
   # bancho.py's v1 API (the tomyum-bpy-api plugin).
   http://api.{$DOMAIN} {
   	import access-log
   	import tomyum
   }
   ```

   and, inside the `http://osu.{$DOMAIN}` site (before the final `handle { import osu-web }`):

   ```caddyfile
   	# bancho.py's v1 API (the tomyum-bpy-api plugin).
   	handle /bpy-api/* {
   		import tomyum
   	}
   ```

   Caddy keeps the `Host` header, which is how the plugin's routes tell `api.` and `osu.` apart.

3. **Host Caddy** (`/etc/caddy/Caddyfile`): add `api.blueskychan.dev` to the HTTPS site list with c., osu.,
   and the rest (the Cloudflare origin certificate already covers `*.blueskychan.dev`), then
   `sudo systemctl reload caddy`.

4. **DNS**: an `api` record in Cloudflare, proxied, like `osu`.

5. **Restart**:

   ```sh
   cd ~/somtum-dev/TomYum/deploy/production
   sudo docker compose up -d server      # new volume and environment: recreates the container
   sudo docker compose restart caddy     # the stack's Caddyfile changed
   sudo docker compose logs server | grep -i bpy-api
   ```

   The log shows `plugin loaded: tomyum.bpy-api …` and
   `tomyum.bpy-api: bancho.py's v1 API on api.<domain>/v1, osu.<domain>/bpy-api/v1`.

6. **Check it**:

   ```sh
   curl -s https://api.blueskychan.dev/v1/get_player_count
   curl -s 'https://osu.blueskychan.dev/bpy-api/v1/get_player_info?scope=info&id=2'
   ```

Updating the plugin later is `git pull` in this repo and `sudo docker compose restart server`.

## Development

```
plugin/   the plugin (mounted into TomYum); plugin.json + .cs
tests/    xunit tests; compiles plugin/*.cs against ../TomYum/src/TomYum.Plugins.Abstractions
```

The tests expect TomYum checked out next to this repo (`../TomYum`). They build the plugin's sources the way
the server does (C# 13, nullable on, no implicit usings) with analyzers and warnings as errors.

```sh
cd tests
dotnet build      # 0 warnings
dotnet test
```

The live checks (`LiveDatabaseTests`, `LiveExplainTests`) run every endpoint against a real database, with
the connection switched to read-only first. They're skipped unless `BPY_LIVE_DB` is set:

```sh
BPY_LIVE_DB='Server=<db host>;User ID=<user>;Password=<password>;Database=tomyum' \
BPY_LIVE_DUMP=/tmp/bpy-dump \
dotnet test
```

`BPY_LIVE_DUMP` saves every answer and the `EXPLAIN` of every query. The ids they use (`BPY_LIVE_USER`,
`BPY_LIVE_MAP`, …) default to ones on blueskychan.dev; `BPY_LIVE_REPLAYS` and `BPY_LIVE_LAZER_REPLAYS` point
`/get_replay` at copies of the replay folders.
