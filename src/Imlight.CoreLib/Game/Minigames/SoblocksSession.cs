// CLASSIC: Sorcery Stones' service 25 is a board/session protocol, not connect=1/rewards=3.
// Derived behavior and private evidence hashes: docs/handoff/sorcery-stones-protocol-20261023.md.
using System;
using System.Collections.Generic;
using Imcodec.MessageLayer;
using Imcodec.MessageLayer.Generated;

namespace Imlight.CoreLib.Game.Minigames;

internal sealed class SoblocksSession {
    internal const int Width = 7, StartingHeight = 5;
    private static readonly int[] InitialTiles = [1, 6, 3, 10, 7, 5];
    private static readonly int[] AddedTiles = [4, 8, 9, 2];
    private readonly Func<int, int> _next;
    private readonly List<int> _tiles = [];
    private Phase _phase;
    private bool _paused;
    private int? _countdown;
    private DateTimeOffset _countdownAt;
    private int _jewel, _bomb, _pinwheel, _reinforced, _frozen;
    private enum Phase { Selection, Playing, Finished }

    internal SoblocksSession(Func<int, int> next = null) => _next = next ?? Random.Shared.Next;

    internal void Receive(IMessage message, DateTimeOffset now, Action<IMessage> send,
                          Action startRun, Action<int> finish) {
        switch (message) {
            case SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME end when end.Score == -1:
                finish(-1); // Pre-game leaderboard query must not start or consume a run.
                return;
            case SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SELECTGAME select
                    when _phase == Phase.Selection && select.GameType is >= 0 and <= 2:
                _phase = Phase.Playing;
                _paused = false;
                _countdown = null;
                _tiles.Clear();
                _tiles.AddRange(InitialTiles);
                _jewel = 192; _bomb = 64; _pinwheel = 32; _reinforced = 32; _frozen = 64;
                send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SELECTGAME { GameType = select.GameType });
                SendInitialBoard(send);
                startRun();
                return;
            case SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_RESETGAME when _phase == Phase.Finished:
                _phase = Phase.Selection;
                _countdown = null;
                send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_RESETGAME());
                return;
        }
        if (_phase != Phase.Playing) return;
        switch (message) {
            case SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ENDGAME end when end.Score >= 0:
                _phase = Phase.Finished; // A duplicate result cannot become payable after an early refusal.
                finish(end.Score);
                break;
            case SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_REQUESTROW:
                send(Row(false));
                break;
            case SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SENDROW chances:
                // This direction carries chance denominators, not a row. Nonpositive means disabled.
                _jewel = Math.Max(0, chances.Index1); _bomb = Math.Max(0, chances.Index2);
                _pinwheel = Math.Max(0, chances.Index3); _reinforced = Math.Max(0, chances.Index4);
                _frozen = Math.Max(0, chances.Index5);
                break;
            case SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_LEVELUP:
                if (_tiles.Count < InitialTiles.Length + AddedTiles.Length)
                    _tiles.Add(AddedTiles[_tiles.Count - InitialTiles.Length]);
                break;
            case SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_PAUSE:
                _paused = !_paused;
                send(_paused ? new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_PAUSEON()
                             : new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_PAUSEOFF());
                break;
            case SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_STARTSWAP when !_paused:
                send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_STARTSWAP());
                break;
            case SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ADVANCEON:
                send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ADVANCEON());
                break;
            case SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ADVANCEOFF:
                send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_ADVANCEOFF());
                break;
            case SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_COUNTDOWN:
                if (_countdown is null) {
                    send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_COUNTDOWN { Number = 4 });
                    _countdown = 3;
                    _countdownAt = now;
                }
                else if (now - _countdownAt > TimeSpan.FromSeconds(1)) {
                    send(new SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_COUNTDOWN { Number = _countdown.Value });
                    _countdown = _countdown == 0 ? null : _countdown - 1;
                    _countdownAt = now;
                }
                break;
            // Ready, Attack, RowInfo and server-only messages do not start/reset/reward anything.
        }
    }

    private void SendInitialBoard(Action<IMessage> send) {
        var board = new int[StartingHeight, Width];
        for (var row = 0; row < StartingHeight; row++) {
            var values = new int[Width];
            for (var col = 0; col < Width; col++) {
                var choices = new List<int>(_tiles.Count);
                foreach (var tile in _tiles) {
                    if (col >= 2 && board[row, col - 1] == tile && board[row, col - 2] == tile) continue;
                    if (row >= 2 && board[row - 1, col] == tile && board[row - 2, col] == tile) continue;
                    choices.Add(tile);
                }
                values[col] = board[row, col] = choices[_next(choices.Count)];
            }
            send(RowMessage(values));
        }
        send(Row(true));
    }

    private bool Chance(int denominator) => denominator > 0 && _next(denominator) == denominator - 1;

    private SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SENDROW Row(bool plain) {
        var values = new int[Width];
        for (var col = 0; col < Width; col++) {
            // Uniform selection from allowed colors is equivalent to rejection sampling, without an unbounded loop.
            // The previous *emitted* tile matters: a frozen/reinforced/bomb tile is not an adjacent plain match.
            var choices = _tiles.FindAll(tile => col == 0 || tile != values[col - 1]);
            var tile = choices[_next(choices.Count)];
            values[col] = plain ? tile : Chance(_frozen) ? tile + 200 : Chance(_reinforced) ? tile + 100
                : Chance(_pinwheel) ? 21 : Chance(_bomb) ? tile + 10 : Chance(_jewel) ? 22 : tile;
        }
        return RowMessage(values);
    }

    private static SOBLOCKS_MESSAGES_25_PROTOCOL.MSG_SOBLOCKS_SENDROW RowMessage(int[] values) => new() {
        Index1 = values[0], Index2 = values[1], Index3 = values[2], Index4 = values[3],
        Index5 = values[4], Index6 = values[5], Index7 = values[6],
    };
}
