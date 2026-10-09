/*
 * Imlight
 * Copyright (C) 2025 Revive101
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <http://www.gnu.org/licenses/>.
 *
 * ========================================================================
 * AMBIENT ZONE: GROUPS
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-09): the street side of grouping with ambient wizards
 * (owner: "we should be able to ask ambient wizards to join us in a
 * dungeon or whatever and build a group 'naturally' like the old days").
 *   1. Hearing a call (GroupManners.ParseCall): "anyone want to do
 *      jotun?", "need help with kraken", "LF group for hall of kings",
 *      "wanna group?", the client's "Would you like to join my group?".
 *      A Say reaches every wizard that hears it; the zone decides once.
 *      A wizard's first name in the line, or a whisper, asks that wizard
 *      only; the client's group invite (AmbientGroupService) likewise.
 *   2. Who answers (GroupManners.Answer): the wizards in earshot (1500
 *      units), each on its level for the player and the target, its
 *      temper and the group's schools. Some say yes, some "brb" and come
 *      back a minute later, one at most declines out loud, the rest let
 *      it pass. Each answer takes a human reading, thinking and typing
 *      time, staggered against the zone's other answers.
 *   3. A yes joins the player's group (AmbientCompanionGroup, made on the
 *      first yes): the wizard leaves the street's care where it stands
 *      and follows the player from there. A yes that finds the group full
 *      says so instead ("oh nvm ur full").
 *   4. Back from a group: in place when it still stands in its street,
 *      else it comes back into the street a few seconds later.
 *
 * USAGE EXAMPLE:
 * if (GroupHeard(wizard, speaker, text, whisper, now)) return;   // in Settled
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/09/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic.Ambient;

internal sealed partial class AmbientZone {

    /// <summary>The client's error code for a declined group invite (MSG_PARTYREQUESTRESPONSE; upstream Imlight's).</summary>
    internal const int InviteDeclined = 6;

    private readonly Dictionary<ulong, (string Text, DateTime At)> _callsHeard = [];
    private readonly Dictionary<AmbientWizard, (ulong Leader, bool Yes)> _answering = [];
    private int _groupSeq;

    private void ReceiveGroups() {
        Receive<AmbientCompanionBack>(OnCompanionBack);
        Receive<AmbientGroupInvite>(OnGroupInvite);
    }

    private static string FirstName(AmbientWizard wizard) => wizard.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    /// <summary>Free to be asked: in the street, not busy, not already answering or walking to a sigil or a duel.</summary>
    private bool FreeToGroup(AmbientWizard wizard)
        => wizard.Present && wizard.Driver is null && _wizards.Contains(wizard)
           && wizard.Activity is AmbientActivity.Idle or AmbientActivity.Walking or AmbientActivity.Shopping or AmbientActivity.Following
           && !_answering.ContainsKey(wizard) && !_askAbout.ContainsKey(wizard) && !_toSigil.ContainsKey(wizard) && !_onSigil.ContainsKey(wizard);

    /// <summary>
    /// A line from a real player: a call for company is decided here once for the zone (see the file header, 1-2). True
    /// when the line was such a call (nothing else answers it).
    /// </summary>
    private bool GroupHeard(AmbientWizard wizard, ulong speaker, string text, bool whisper, DateTime now) {
        if (!AmbientGroups.Settings.Enabled) {
            return false;
        }

        var call = GroupManners.ParseCall(text, FirstName(wizard), whisper);
        if (call.Kind == RecruitKind.None) {
            return false;
        }

        if (!whisper) {
            if (_callsHeard.TryGetValue(speaker, out var last) && last.Text == text && now - last.At < TimeSpan.FromSeconds(3)) {
                return true; // the same Say, heard by another wizard of the zone
            }

            if (_callsHeard.Count > 200) {
                _callsHeard.Clear();
            }

            _callsHeard[speaker] = (text, now);
        }

        Recruit(speaker, text, call, whisper ? wizard : null, invite: false, whisper, now);
        return true;
    }

    /// <summary>Who answers a call, and how (see the file header, 2).</summary>
    private void Recruit(ulong leader, string text, RecruitCall call, AmbientWizard asked, bool invite, bool whisper, DateTime now) {
        if (!ActiveWizardDirectory.TryGetByCharId(leader, out var player)) {
            return;
        }

        var settings = AmbientGroups.Settings;
        var open = GroupManners.OpenSlots(1, AmbientGroups.CompanionCount(leader) + _answering.Count(kv => kv.Value.Leader == leader && kv.Value.Yes),
            settings.MaxSize);
        var target = call.Target is { } t ? AmbientGroups.Target(t) : null;
        List<AmbientWizard> listeners;
        var direct = asked is not null;
        if (direct) {
            listeners = [asked];
        }
        else {
            var named = _wizards.Where(w => w.Present && AmbientChatBrain.Mentions(text, FirstName(w))).ToList();
            direct = named.Count > 0;
            listeners = direct ? named
                : [.. _wizards.Where(w => w.Present && string.Equals(player.Zone, _zone, StringComparison.OrdinalIgnoreCase)
                                          && Distance(w.Position, player.Location) <= HelpManners.HearingDistance)];
        }

        // The group's schools: the player's and its companions'.
        var schools = AmbientWizards.All.Where(w => AmbientGroups.LeaderOf(w.CharId) == leader).Select(w => AmbientWizards.SchoolOf(w.Identity.School))
            .Append(player.MagicSchoolBehavior.MagicSchool).ToList();
        var hasHealer = schools.Contains(AmbientWizards.SchoolOf(AmbientSchool.Life));
        Logger.Information("Ambient wizards in {Zone} heard {Player} ask for company{Target}{Invite}: {Count} can answer, {Open} place(s) open.",
            Logger.Args(_zone, leader, target is null ? call.Target is null ? "" : $" ({call.Target}: not known)" : $" ({call.Target}: level {target.Level})",
                invite ? " (group invite)" : direct ? " (by name)" : "", listeners.Count, open));

        int yes = 0, noSaid = 0;
        foreach (var wizard in listeners.OrderBy(_ => _rng.Next())) {
            GroupAnswer answer;
            if (AmbientGroups.LeaderOf(wizard.CharId) == leader) {
                continue; // already in this group
            }

            if (!FreeToGroup(wizard)) {
                answer = direct ? GroupAnswer.Decline : GroupAnswer.Ignore; // busy: hatching, in a fight, on its way somewhere
            }
            else {
                var facts = new RecruitFacts(wizard.Wizard.MagicSchoolBehavior.Level, player.MagicSchoolBehavior.Level, target?.Level ?? 0,
                    direct, invite, wizard.Identity.Temper, schools.Contains(AmbientWizards.SchoolOf(wizard.Identity.School)),
                    wizard.Identity.School == AmbientSchool.Life, hasHealer, open - yes);
                answer = GroupManners.Answer(facts, settings.Willing, _rng.NextDouble());
            }

            if (answer is GroupAnswer.Yes or GroupAnswer.Brb) {
                if (yes > open) {
                    answer = GroupAnswer.Ignore; // one extra yes may still find it full; more would be a crowd
                }
                else {
                    yes++;
                }
            }
            else if (answer != GroupAnswer.Ignore && !direct && noSaid++ >= 1) {
                answer = GroupAnswer.Ignore; // one "no thx" for the street is plenty
            }

            if (answer == GroupAnswer.Ignore && invite) {
                answer = GroupAnswer.Decline; // an invite always gets its answer
            }

            Logger.Debug("Ambient wizard {Name} (level {Level} {School}) answers {Player}'s call: {Answer}.",
                Logger.Args(wizard.Name, wizard.Wizard.MagicSchoolBehavior.Level, wizard.Identity.School, leader, answer));
            if (answer != GroupAnswer.Ignore) {
                AnswerCall(wizard, leader, text, answer, privately: whisper || (invite && !Near(wizard, player)), invite, open: !direct);
            }
        }
    }

    private bool Near(AmbientWizard wizard, Wizard player)
        => string.Equals(player.Zone, _zone, StringComparison.OrdinalIgnoreCase) && Distance(wizard.Position, player.Location) <= HelpManners.HearingDistance;

    /// <summary>The wizard answers after a human answer time; a yes then joins (see the file header, 3).</summary>
    private void AnswerCall(AmbientWizard wizard, ulong leader, string heard, GroupAnswer answer, bool privately, bool invite, bool open) {
        var persona = ChatPersona.For(wizard.Identity);
        var pool = answer switch {
            GroupAnswer.Yes => GroupLines.Yes, GroupAnswer.Brb => GroupLines.Brb, GroupAnswer.Done => GroupLines.Done,
            GroupAnswer.TooLow => GroupLines.TooLow, GroupAnswer.Full => GroupLines.Full, _ => GroupLines.Decline,
        };
        var line = GroupLines.For(pool, persona, wizard.Identity.Seed + wizard.Turn++);
        var styled = line is null ? null : ChatStyle.Apply(line, persona, _rng, ChatWordFilter.Current);
        var now = DateTime.UtcNow;
        var wait = ChatTiming.Answer(heard, styled ?? "ok", persona, busy: false, _rng)
                   + (open ? TimeSpan.FromSeconds(0.5 + _rng.NextDouble() * 4) : TimeSpan.Zero); // noticing a line to everyone
        var due = Chatter.Stagger(now + wait);
        _answering[wizard] = (leader, answer is GroupAnswer.Yes or GroupAnswer.Brb);
        Timers.StartSingleTimer($"group-{wizard.CharId}-{due.Ticks}", new Later(wizard, w => {
            _answering.Remove(w);
            if (answer is not (GroupAnswer.Yes or GroupAnswer.Brb)) {
                SayTo(w, leader, styled, privately);
                if (invite) {
                    InviteAnswer(leader, w, InviteDeclined);
                }

                return;
            }

            if (!FreeToGroup(w) || OnlinePlayerCollection.GetOnlinePlayer(leader) is null) {
                return; // it walked off, got busy, or the player left: the moment passed
            }

            if (GroupManners.OpenSlots(1, AmbientGroups.CompanionCount(leader), AmbientGroups.Settings.MaxSize) <= 0) {
                SayTo(w, leader, GroupLines.For(GroupLines.Full, persona, w.Identity.Seed + w.Turn++), privately);
                if (invite) {
                    InviteAnswer(leader, w, InviteDeclined);
                }

                return;
            }

            SayTo(w, leader, styled, privately);
            Halt(w);
            w.Activity = AmbientActivity.Idle;
            _answering[w] = (leader, true); // spoken for
            if (answer == GroupAnswer.Brb) {
                // Away from the keyboard a minute: it stands, then comes back and joins if there is still room.
                var back = GroupManners.BrbShortest + TimeSpan.FromSeconds(_rng.NextDouble() * (GroupManners.BrbLongest - GroupManners.BrbShortest).TotalSeconds);
                w.Until = DateTime.UtcNow + back + TimeSpan.FromSeconds(5);
                Timers.StartSingleTimer($"brb-{w.CharId}", new Later(w, b => {
                    _answering.Remove(b);
                    // Back at the keyboard: the player is still here (else the moment passed; a stranger does not chase them).
                    if (!FreeToGroup(b) || !ActiveWizardDirectory.TryGetByCharId(leader, out var still) || !Near(b, still)
                        || GroupManners.OpenSlots(1, AmbientGroups.CompanionCount(leader), AmbientGroups.Settings.MaxSize) <= 0) {
                        return;
                    }

                    SayTo(b, leader, GroupLines.For(GroupLines.Back, persona, b.Identity.Seed + b.Turn++), privately);
                    JoinCompanionGroup(b, leader);
                }), back);
                return;
            }

            w.Until = DateTime.UtcNow.AddSeconds(5);
            Timers.StartSingleTimer($"join-group-{w.CharId}", new Later(w, j => {
                _answering.Remove(j);
                if (j.Present && j.Driver is null && _wizards.Contains(j)) {
                    JoinCompanionGroup(j, leader);
                }
            }), TimeSpan.FromMilliseconds(700 + _rng.Next(900)));
        }), due - now);
    }

    private void SayTo(AmbientWizard wizard, ulong player, string line, bool privately) {
        if (string.IsNullOrEmpty(line)) {
            return;
        }

        if (!privately || !AmbientChat.Whisper(wizard, player, line)) {
            AmbientChat.Say(wizard, line);
        }
    }

    /// <summary>The wizard leaves the street's care where it stands and joins <paramref name="leader"/>'s group.</summary>
    private void JoinCompanionGroup(AmbientWizard wizard, ulong leader) {
        if (!AmbientGroups.TryGet(leader, out var group)) {
            ulong channel = RandomGen.GenerateGUID();
            ulong party = RandomGen.GenerateGUID();
            group = Context.ActorOf(AmbientCompanionGroup.Props(leader, channel, party, _server), $"group-{leader:x}-{++_groupSeq}");
            if (!AmbientGroups.Register(leader, group, channel)) {
                Context.Stop(group);
                if (!AmbientGroups.TryGet(leader, out group)) {
                    return;
                }
            }
        }

        Halt(wizard);
        if (wizard.DuelSigil == ulong.MaxValue) {
            wizard.DuelSigil = 0; // it was hunting; the group comes first
        }

        AmbientWizards.RevokeJoin(wizard.Endpoint);
        _wizards.Remove(wizard);
        _afterDuel.Remove(wizard);
        _askAbout.Remove(wizard);
        _toSigil.Remove(wizard);
        _answering.Remove(wizard);
        wizard.FollowCharId = 0;
        wizard.Activity = AmbientActivity.Grouped;
        wizard.Driver = group;
        Logger.Information("Ambient wizard {Name} joins {Leader}'s group from {Zone}.", Logger.Args(wizard.Name, leader, _zone));
        group.Tell(new AmbientCompanionGroup.Take(wizard));
    }

    /// <summary>A companion is back from a group (see the file header, 4).</summary>
    private void OnCompanionBack(AmbientCompanionBack back) {
        var wizard = back.Wizard;
        wizard.Driver = null;
        wizard.DuelSigil = 0;
        wizard.Wizard.IsInDuel = false;
        AmbientWizards.RevokeJoin(wizard.Endpoint);
        if (!_wizards.Contains(wizard)) {
            _wizards.Add(wizard);
        }

        var now = DateTime.UtcNow;
        if (back.ZoneActor is not null && wizard.Present && back.ZoneActor.Equals(_zoneActor)) {
            wizard.Activity = AmbientActivity.Idle;
            wizard.Until = now.AddSeconds(2 + _rng.Next(4));
            _afterDuel.Add(wizard); // it walks off rather than standing where the group left it
            return;
        }

        if (wizard.Present) {
            Leave(wizard);
        }

        wizard.Zone = _zone; // its chat and status go by this zone again once it is back
        wizard.Activity = AmbientActivity.Arriving;
        Timers.StartSingleTimer($"enter-{wizard.CharId}", new Enter(wizard.CharId), back.After > TimeSpan.Zero ? back.After : TimeSpan.FromSeconds(3));
    }

    /// <summary>The client's group invite to one of this zone's wizards: answered like a call by name (see the file header, 1).</summary>
    private void OnGroupInvite(AmbientGroupInvite invite) {
        var wizard = invite.Wizard;
        if (!_wizards.Contains(wizard) || !wizard.Present) {
            InviteAnswer(invite.Leader, wizard, InviteDeclined);
            return;
        }

        Recruit(invite.Leader, "", new RecruitCall(RecruitKind.Named, null), wizard, invite: true, whisper: false, DateTime.UtcNow);
    }

    /// <summary>Tells the inviting player's client that the invite was not taken (the client's group window, when on).</summary>
    private static void InviteAnswer(ulong leader, AmbientWizard wizard, int errorCode) {
        if (!AmbientGroups.Settings.Window || OnlinePlayerCollection.GetOnlinePlayer(leader) is not { ActorPath: { Length: > 0 } path }
            || AmbientChat.System is null) {
            return;
        }

        AmbientChat.System.ActorSelection(path).Tell(new GAME_5_PROTOCOL.MSG_PARTYREQUESTRESPONSE {
            DestinationCharacterID = leader, TargetCharacterID = wizard.CharId, TargetGlobalID = wizard.Wizard.GameObjectID,
            ErrorCode = errorCode, PlayerNameBlob = AmbientChat.NameBytes(wizard.Wizard),
        });
    }

}
