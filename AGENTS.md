# AGENTS.md (server)

This is the game server of Wizard101 Classic. Read `../AGENTS.md` in the parent repository first: the hard rules,
the merge → test → deploy routine and the lessons there apply here. Branch `classic`; our changes are marked
`// CLASSIC:`. Test with `MSBUILDDISABLENODEREUSE=1 nice -n 10 ~/.dotnet/dotnet test src/Imlight.sln -m:2` and check
the log for FATAL/crashed and a lower Total, not just "Passed!". Anything touching login, attach, zone transfer or
the protocol must be checked against what the real r806919 client sends, not only the playbot.
