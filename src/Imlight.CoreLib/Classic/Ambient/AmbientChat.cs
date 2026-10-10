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
 * AMBIENT CHAT AND KNOWLEDGE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: how an ambient wizard's lines reach players, and the server
 * data its chat brain answers from.
 *   - Say goes out as MSG_RADIALCHAT to the real players in its zone (as
 *     ChatService sends a player's Say, ignore lists respected); a Text
 *     (whisper) as MSG_DIRECTEDCHAT to the player's session.
 *   - A Say's Message is in the official client's own form (EncodeSay: a
 *     16-bit character count, then UTF-16LE), byte for byte what a real
 *     client sends and ChatService relays. With [Classic] AmbientWizardChat
 *     off, Say and Whisper send nothing. CLASSIC (2026-10-10): neither sends a line the client's chat
 *     dictionary would hide (Fits; AmbientChatPlanner.Sendable).
 *   - WhereIs answers "where is X" from the quest templates: a goal titled
 *     "Talk to Lady Blackhope" whose destination is WizardCity/WC_Streets/
 *     WC_HauntedCave gives "Lady Blackhope" -> "Haunted Cave".
 *   - Facts gives a player's name, zone and current quest title.
 *
 * USAGE EXAMPLE:
 * AmbientChat.Say(wizard, "hi everyone", ignoreCharId: 0);
 * var where = AmbientKnowledge.WhereIs("lady blackhope");
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/10/2026
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Utilities;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Classic.Ambient;

/// <summary>Ambient wizard chat out (see the file header).</summary>
internal static class AmbientChat {

    /// <summary>
    /// CLASSIC (2026-10-02): a Say's Message as the r806919 client writes and reads it. MSG_RADIALCHAT's Message is an
    /// STR, but the client fills it with its own little-endian buffer (GameClient::HandleSendRadialChat, 0x1412416e0):
    /// a 16-bit character count, then the text in UTF-16LE. MSG_RadialChat (0x141708e50 -> 0x1412c8990) reads the
    /// count and then count * 2 bytes. Plain UTF-8 made the official client read a count of 0x656E from "ne" and
    /// spin at 100% CPU (owner's client froze on "need a hand?"; ChatService's "drop the first byte" was a guess at
    /// the low byte of this count).
    /// </summary>
    internal static byte[] EncodeSay(string text) {
        text ??= "";
        var body = Encoding.Unicode.GetBytes(text);
        var bytes = new byte[2 + body.Length];
        bytes[0] = (byte) text.Length;
        bytes[1] = (byte) (text.Length >> 8);
        body.CopyTo(bytes, 2);

        return bytes;
    }

    /// <summary>
    /// The text of a Say: the client's count + UTF-16LE form when the bytes are exactly that, else plain text (our own
    /// headless bot sends UTF-8).
    /// </summary>
    internal static string Text(byte[] raw) {
        if (raw is null || raw.Length == 0) {
            return "";
        }

        if (raw.Length >= 2 && raw.Length == 2 + 2 * (raw[0] | raw[1] << 8)) {
            return Encoding.Unicode.GetString(raw, 2, raw.Length - 2).Trim();
        }

        var start = raw[0] < 0x20 ? 1 : 0;
        return Encoding.UTF8.GetString(raw, start, raw.Length - start).Trim();
    }

    /// <summary>The 4-byte name the chat messages carry (name keys with the gender marker).</summary>
    internal static byte[] NameBytes(Wizard wizard)
        => DataManipulation.SpacedHexStringToBytes(wizard.PlayerNameBehavior.GetWizardNameAsByteHexString());

    /// <summary>
    /// Says <paramref name="text"/> to the real players in the wizard's zone. Nothing at all goes out when
    /// [Classic] AmbientWizardChat is off.
    /// </summary>
    internal static bool Say(AmbientWizard wizard, string text) {
        if (!AmbientWizards.Settings.Chat || string.IsNullOrEmpty(text) || !Fits(wizard, text)) {
            return false;
        }

        var message = new GAME_5_PROTOCOL.MSG_RADIALCHAT {
            Message = EncodeSay(text),
            SourceID = wizard.Wizard.GameObjectID,
            SourceName = NameBytes(wizard.Wizard),
            Filter = 2,
        };
        var blockedBy = BuddyRelationshipCollection.GetCharactersWhoBlocked(wizard.CharId);
        foreach (var player in OnlinePlayerCollection.GetPlayersInZone(wizard.Zone)) {
            if (AmbientWizards.IsAmbientChar(player.CharacterId) || blockedBy.Contains(player.CharacterId)) {
                continue;
            }

            Selection(player.ActorPath)?.Tell(message);
        }

        Logger.Debug("[{Zone}] {Name} (ambient): {Text}", Logger.Args(wizard.Zone, wizard.Name, text));

        return true;
    }

    /// <summary>Whispers <paramref name="text"/> to a player (MSG_DIRECTEDCHAT, as from a friend).</summary>
    internal static bool Whisper(AmbientWizard wizard, ulong toCharId, string text) {
        if (!AmbientWizards.Settings.Chat || string.IsNullOrEmpty(text) || !Fits(wizard, text)) {
            return false; // [Classic] AmbientWizardChat off: no chat at all
        }

        var target = OnlinePlayerCollection.GetOnlinePlayer(toCharId);
        if (target is null || AmbientWizards.IsAmbientChar(toCharId)
            || BuddyRelationshipCollection.GetCharactersWhoBlocked(wizard.CharId).Contains(toCharId)) {
            return false;
        }

        Selection(target.ActorPath)?.Tell(new GAME_5_PROTOCOL.MSG_DIRECTEDCHAT {
            SourceName = NameBytes(wizard.Wizard),
            SourceID = wizard.CharId,
            Message = text,
            Filter = 0,
        });
        Logger.Debug("{Name} (ambient) to {Target}: {Text}", Logger.Args(wizard.Name, toCharId, text));

        return true;
    }

    /// <summary>
    /// CLASSIC (2026-10-10): the last gate for every ambient line: IsClean, and every word in the client's chat dictionary
    /// (r806919 lists, when loaded; numbers only for an open-chat grown-up). A line that fails is not sent at all: the
    /// client would show its words as "..." (a 2009 kid's chat), which no player would type.
    /// </summary>
    internal static bool Fits(AmbientWizard wizard, string text) {
        var persona = ChatPersona.For(wizard.Identity);
        if (AmbientChatPlanner.Sendable(text, persona, ChatWordFilter.Current)) {
            return true;
        }

        Logger.Warning("Ambient wizard {Name} would say \"{Text}\", which the client's chat would hide ({Words}); not sent.",
            Logger.Args(wizard.Name, text, ChatWordFilter.Current is { } filter ? string.Join(",", filter.Refused(text, persona.Numbers)) : "unclean"));
        return false;
    }

    internal static ActorSystem System { get; set; }

    private static ActorSelection Selection(string path)
        => System is null || string.IsNullOrEmpty(path) ? null : System.ActorSelection(path);

}

/// <summary>Server data for ambient chat (see the file header).</summary>
internal static class AmbientKnowledge {

    private static Dictionary<string, string> s_whereIs;
    private static readonly Regex s_subject = new(@"^(?:talk to|speak (?:to|with)|defeat|find|visit|meet|go to|see)\s+(?<who>.+?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Where a character or place is, as a zone players know, or null.</summary>
    internal static string WhereIs(string subject) {
        var index = s_whereIs ??= Build();
        var key = Normalize(subject);
        if (key.Length < 3) {
            return null;
        }

        if (index.TryGetValue(key, out var exact)) {
            return exact;
        }

        var hit = index.Where(kv => kv.Key.Length >= 4 && (kv.Key.Contains(key, StringComparison.Ordinal) || key.Contains(kv.Key, StringComparison.Ordinal)))
            .OrderBy(kv => Math.Abs(kv.Key.Length - key.Length)).ThenBy(kv => kv.Key, StringComparer.Ordinal).FirstOrDefault();

        return hit.Value;
    }

    /// <summary>A zone path as players say it: WizardCity/WC_Streets/WC_HauntedCave -> Haunted Cave.</summary>
    internal static string ZoneName(string zonePath) {
        if (string.IsNullOrEmpty(zonePath)) {
            return null;
        }

        var last = zonePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? zonePath;
        last = Regex.Replace(last, @"^[A-Z]{2,3}_", "");
        last = last switch {
            "Hub" => zonePath.StartsWith("WizardCity", StringComparison.OrdinalIgnoreCase) ? "The Commons" : "the hub",
            "Unicorn" => "Unicorn Way", "Triton" => "Triton Avenue", "Firecat" => "Firecat Alley", "Cyclops" => "Cyclops Lane",
            "Colossus" => "Colossus Boulevard", "Shop_Area" => "the Shopping District", "Ravenwood" => "Ravenwood",
            "OldeTown" => "Olde Town", _ => last,
        };
        last = Regex.Replace(last.Replace('_', ' '), "(?<=[a-z])(?=[A-Z])", " ");

        return last.Trim();
    }

    /// <summary>A player's name, zone (as players say it) and current quest title, when the player is online.</summary>
    internal static (string Name, string Zone, string Quest) Facts(ulong charId) {
        if (!ActiveWizardDirectory.TryGetByCharId(charId, out var wizard)) {
            return (null, null, null);
        }

        string quest = null;
        try {
            var current = wizard.QuestBehavior?.CurrentQuestInstances?.LastOrDefault();
            if (current is not null && QuestTemplateCollection.GetQuestByName(current.QuestName) is { } template) {
                quest = Locale.GetEnglishName(template.m_questTitle ?? "");
            }
        }
        catch (Exception) {
            quest = null;
        }

        return (wizard.PlayerNameBehavior?.GetWizardName(), ZoneName(wizard.Zone), string.IsNullOrWhiteSpace(quest) ? null : quest);
    }

    private static Dictionary<string, string> Build() {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        try {
            foreach (var quest in QuestTemplateCollection.GetAllQuests() ?? []) {
                foreach (var goal in quest?.m_goals ?? []) {
                    if (goal is null || string.IsNullOrEmpty(goal.m_destinationZone)) {
                        continue;
                    }

                    var title = Locale.GetEnglishName(goal.m_goalTitle ?? "");
                    var match = s_subject.Match(title ?? "");
                    if (!match.Success) {
                        continue;
                    }

                    var who = Normalize(Regex.Replace(match.Groups["who"].Value, @"<[^>]*>", ""));
                    if (who.Length >= 3) {
                        index.TryAdd(who, ZoneName(goal.m_destinationZone));
                    }
                }
            }
        }
        catch (Exception ex) {
            Logger.Warning("Ambient wizards: the where-is index is incomplete: {Error}", Logger.Args(ex.Message));
        }

        foreach (var zone in new[] { "WizardCity/WC_Streets/WC_Unicorn", "WizardCity/WC_Streets/WC_Triton", "WizardCity/WC_Streets/WC_Firecat",
                                     "WizardCity/WC_Streets/WC_Cyclops", "WizardCity/WC_Streets/WC_Colossus", "WizardCity/WC_Shop_Area",
                                     "WizardCity/WC_Ravenwood", "WizardCity/WC_Streets/WC_OldeTown", "WizardCity/WC_Streets/WC_HauntedCave" }) {
            var name = ZoneName(zone);
            index.TryAdd(Normalize(name), "Wizard City");
        }

        Logger.Information("Ambient wizards know where {Count} characters and places are.", Logger.Args(index.Count));

        return index;
    }

    private static string Normalize(string text)
        => Regex.Replace((text ?? "").ToLowerInvariant().Replace("the ", ""), @"[^a-z' ]", "").Trim();

}

/// <summary>
/// CLASSIC (2026-10-03): the text of a menu (quick) chat phrase by its chat id, from the client's own menu
/// (Root.wad:QuickChat.xml, QuickChatEntry m_chatID / m_text / m_label) with the words from the QuickChat locale
/// table. Players often answer an ambient wizard's "need a hand?" with the menu's "Yes!", which reaches it as
/// MSG_RADIALQUICKCHAT (or MSG_DIRECTEDQUICKCHAT) with only that id.
/// </summary>
internal static class AmbientQuickChat {

    private static Dictionary<uint, string> s_text;

    /// <summary>The English phrase for a menu chat id, or null.</summary>
    internal static string TextOf(uint chatId) {
        var table = s_text ??= Load();
        return table.TryGetValue(chatId, out var text) ? text : null;
    }

    private static Dictionary<uint, string> Load() {
        var table = new Dictionary<uint, string>();
        try {
            var root = RootArchiveLoader.GetFile<QuickChatEntry>("QuickChat.xml");
            Walk(root, table);
            Logger.Information("Ambient wizards read {Count} menu chat phrases.", Logger.Args(table.Count));
        }
        catch (Exception ex) {
            Logger.Warning("Ambient wizards could not read the menu chat phrases: {Error}", Logger.Args(ex.Message));
        }

        return table;
    }

    private static void Walk(QuickChatEntry entry, Dictionary<uint, string> table) {
        if (entry is null) {
            return;
        }

        if (entry.m_chatID != 0) {
            var key = entry.m_text.ToString() is { Length: > 0 } text ? text : entry.m_label.ToString();
            var english = Locale.GetEnglishName("QuickChat", key);
            if (string.IsNullOrEmpty(english)) {
                english = Locale.GetEnglishName(key);
            }

            if (!string.IsNullOrWhiteSpace(english)) {
                table.TryAdd(entry.m_chatID, english.Trim());
            }
        }

        foreach (var child in entry.m_childEntries ?? []) {
            Walk(child, table);
        }
    }

}

