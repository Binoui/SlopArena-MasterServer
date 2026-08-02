# SlopArena Master Server

The backend that runs the SlopArena pre-match flow: authenticates players, registers game servers, runs the in-memory lobby, and hands a locked-in roster to a game server to fight a match.

## Language

**Game Server**:
A dedicated SlopArena game process that registers with the master server, heartbeats its liveness and load, and hosts matches. Each game server has one lobby bound to it.
_Avoid_: server, host

**Player**:
A human playing SlopArena, identified by their Steam ID.
_Avoid_: user, account

**Lobby**:
A pre-match gathering of players bound to a specific game server. Ephemeral: it exists only while the master server process is up.
_Avoid_: room, party

**Lobby Player**:
A player present in a lobby, with a username, character selection, lock-in state, and host flag.
_Avoid_: member

**Host**:
The first player to join a lobby; the only member who can advance the lobby to character select and start the match. On host departure the next-joined member is promoted.
_Avoid_: leader, captain

**Character**:
The fighter a player selects for a match.
_Avoid_: character class, class, hero

**Lock-in**:
Committing to a character selection; a locked-in player may change their pick until the match starts. All members must be locked in (minimum 2 players) for the match to start.
_Avoid_: ready

**Entity ID**:
The per-match player index (1-based) assigned at match start, used by the game server to bind hitboxes and netcode to players.
_Avoid_: player index, slot

**Match**:
A fight between 2–4 players hosted on a game server; recorded with its roster, winner, region, and timestamps. Starts when the host launches it from character select and ends when the result is reported.
_Avoid_: game, round

**Arena**:
The stage a match is fought on; "split" is the default until host arena select is wired.
_Avoid_: map, level

**Match Port**:
The UDP port a game server assigns to a running match, distinct from the port the server registered for control traffic.

**Heartbeat**:
A game server's periodic report of liveness and current match load.

**Fresh**:
A game server whose heartbeat is less than 15 seconds old.

**Full**:
A game server currently at its maximum concurrent matches.

**Server Browser**:
The list of fresh, non-full game servers offered to players.
_Avoid_: server list

**Matchmaking**:
The planned process of pairing players into a match; not yet implemented — the server browser is the current entry point.

**Character Select**:
The phase between lobby and match, entered when the host starts it, in which players lock in characters.
_Avoid_: char select, pick phase
