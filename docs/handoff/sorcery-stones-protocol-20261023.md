# Sorcery Stones server protocol repair, 2026-10-23

This server change implements the original solo Sorcery Stones session behind service 25 (`SOBLOCKS_MESSAGES_25_PROTOCOL`). The previous generic minigame route treated message order 1 as Connect and order 3 as Rewards; for this generated protocol those orders mean AdvanceOff and Attack. EndGame is order 5 and carries uppercase `Score`. Ready is order 13, RequestRow 14, SelectGame 18, SendRow 19 and StartSwap 21. The generated ordering is alphabetical, not the declaration order of the input XML. Tests encode/decode generated messages and inspect the service/order/payload bytes independently.

## Derived behavior

A valid initial SelectGame mode 0, 1 or 2 echoes the selection, emits five initial rows without horizontal/vertical triples, emits one plain upcoming row without adjacent equal tiles, and signals minigame start. The initial six tile identities and the four later additions follow the original order. Upcoming rows avoid adjacent equal plain tiles. Specials are tested in priority order frozen, reinforced, pinwheel, bomb, jewel; their positive denominators give a one-in-denominator chance, and nonpositive values disable them. Client-to-server SendRow supplies five such denominators, while server-to-client SendRow supplies seven tiles. LevelUp adds the next available tile, bounded at the ten authored tile identities. The client owns its level speed; this server invents no SetLevel parameters.

Ready does nothing. Pause toggles PauseOn/PauseOff, StartSwap echoes only while unpaused, and AdvanceOn/AdvanceOff echo. Countdown is client-polled: first 4, then 3/2/1/0 at intervals strictly greater than one second, with no catch-up burst or change to the reward timer. A completed sequence can be polled again as in the original handler; it cannot reset the paid run.

EndGame Score = -1 remains a pre-game or in-game leaderboard query. A nonnegative result from an active round reaches the existing common reward routine with its uppercase Score intact. The shared `MinigameRun` minimum duration, cross-game per-wizard cooldown, score cap and once-paid decision are unchanged. A completed round's duplicate result is ignored, including retrying an early rejected result later. New paid runs require a completed-result ResetGame echo followed by a valid SelectGame. Duplicate active selections, Ready, Advance, Attack and active-round ResetGame do not restart timing or make rewards available. This stricter admission preserves the original client's observed post-result ResetGame→SelectGame sequence while preventing control-message resets.

A Soblocks job rejects other game services; service 25 is dropped on other minigame jobs. The minigame tunnel also drops a late message whose JobID no longer matches the active job, before decoding or forwarding it. No score-authoritative board simulation or proof of client score is claimed; the existing conservative reward guards remain necessary.

## Validation and limits

`SoblocksProtocolTests` covers generated wire identifiers and binary Score/row bytes, board/row constraints across seeded runs, special priority, chance updates, tile additions, countdown/pause controls, valid replay lifecycle, premature/repeated rewards, cooldown/caps, cross-game routing, late JobID tunnels and an actual Akka minigame actor's startup replies. Its ServerIntegrationTest project metadata explicitly removes the file from public pure-rules CI when private generated message/type inputs are unavailable. No game data, original Lua or generated private sources are committed.

The positive EndGame test exercises the real production actor, PlayerQuery exchange, common HandleRewards, loot/reward serialization and leaderboard persistence/retrieval. Only its clock and score repository are instance dependencies; production defaults remain system time and the existing score collection. It proves the upper-bound Score reaches the bounded native result and stored leaderboard, a duplicate records once, and an early replay result has empty loot and cannot later pay on retry.

Focused result: **38 passed, Failed 0, Skipped 0**, with zero `FATAL|crashed|Stack overflow` log matches. Public CI MSBuild evaluation excludes this integration file and retains the pure `SecurityWorldGuardsTests.cs` (171 compile items). Logs are private and no live database is accessed by the new fixtures.

Focused tests run serialized under the shared heavy lock with the owner game closed, `nice -n20`, MSBuild parallelism1 and node reuse disabled. Root performs the combined suite after integration. Live real-client board initialization, input, game-over/replay and rewards still need native validation after root's deployment; unit/actor tests alone do not establish rendered client correctness. This work branch does not merge or deploy itself.

## Private evidence identity

Evidence remains outside git under `~/w101c-private/research/minigames/remaining-lua-20261023/`. These are SHA256 identities of the examined files; only derived behavior and hashes are recorded here.

| Private relative file | SHA256 |
| --- | --- |
| Scripts/Soblocks/Server.lua | `3c0526457c8ba24046af51fbe60cb0a21e5970c121bff275ee7d8c921298a637` |
| Scripts/Soblocks/ServerCore.lua | `18ab87d52b7ad04620c0e7d467fb9a0360ed3ce4f8b3388772e7944a032e76e3` |
| Scripts/Soblocks/Config.lua | `705d2a942f573066b50090319a21191d3d4b52ef913d055b1086b12d099c0647` |
| Scripts/Soblocks/GameData.lua | `ba0ab3c0c03321970e8508769e29c37a98640b5cd12881eaad2003edcb7d38f8` |
| Scripts/Soblocks/Shared.lua | `45b465276f15c9605f045cd34b29f67e8d1fcddd3cad6d68f3d575f6217b546f` |
| Scripts/Soblocks/Client.lua | `832ae9437f97e50c855dc83e496316db8d623428b7491258cc0095a8c2672e90` |
| Scripts/Soblocks/ClientCore.lua | `2303d631261ec4c09715e3f7621933c08d1d29bf64439decf5bf6204d95b846b` |
| Messages/SoblocksMessages.xml | `28ce9b891dd23b3d67cbaaec290e274f1bc3fba481dbc6afcee08ba4748ccde9` |
