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
 * AMBIENT ZONE: CHAT
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (2026-10-05): the zone's AmbientChatter (who talks, what, when)
 * and the one-time chat setup: the client's chat word lists from
 * Root.wad (ChatWordFilter) and the optional local language model
 * ([Classic] AmbientWizardLlm*, AmbientLlmClient). Kept in its own file so
 * the chat work and the zone's movement and pet work merge apart.
 *
 * USAGE EXAMPLE:
 * AmbientZone.ConfigureChat(setting); // once, from AmbientDirector
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/05/2026
 */

using System;
using System.Threading.Tasks;
using Imlight.Classic.Ambient;
using Imlight.Common;
using Imlight.CoreLib.Shared.Resources;

namespace Imlight.CoreLib.Classic.Ambient;

internal sealed partial class AmbientZone {

    private AmbientChatter _chatter;
    private int _chatSeq;

    /// <summary>The zone's talk; made on first use (on the actor's thread).</summary>
    private AmbientChatter Chatter => _chatter ??= new AmbientChatter(_zone, _wizards, _heard, _rng,
        (wizard, after, action) => Timers.StartSingleTimer($"chat-{wizard.CharId}-{++_chatSeq}", new Later(wizard, action), after),
        (wizard, speaker, facts) => ChatFor(wizard, speaker, facts));

    /// <summary>
    /// Reads the chat switches and, in the background, the client's chat word lists. <paramref name="setting"/> returns an
    /// ini value ("" when missing).
    /// </summary>
    internal static void ConfigureChat(Func<string, string> setting) {
        var llm = AmbientLlmSettings.Parse(setting("Classic.AmbientWizardLlm"), setting("Classic.AmbientWizardLlmUrl"),
            setting("Classic.AmbientWizardLlmTimeoutMs"), setting("Classic.AmbientWizardLlmPerMinute"),
            setting("Classic.AmbientWizardLlmMaxTokens"));
        AmbientChatter.Llm?.Dispose();
        AmbientChatter.Llm = llm.Enabled ? new AmbientLlmClient(llm) : null;
        Logger.Information(llm.Enabled
            ? "Ambient wizards may take lines from the local language model at {Url} (timeout {Timeout} ms, {PerMinute} a minute)."
            : "Ambient wizards use rule-based chat only ([Classic] AmbientWizardLlm: {Why}).",
            Logger.Args(llm.Enabled ? llm.Endpoint?.ToString() : llm.Why, (int) llm.Timeout.TotalMilliseconds, llm.PerMinute));

        Task.Run(async () => {
            for (var i = 0; i < 120 && !RootArchiveLoader.IsLoaded; i++) {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }

            try {
                var filter = ChatWordFilter.Load(name => {
                    try {
                        return RootArchiveLoader.GetFileStream(name).ToArray();
                    }
                    catch (Exception) {
                        return null;
                    }
                });
                if (filter.HasDictionary) {
                    ChatWordFilter.Current = filter;
                }

                Logger.Information("Ambient wizards check their lines against the client's chat dictionary ({Words} words).",
                    Logger.Args(filter.DictionarySize));
            }
            catch (Exception ex) {
                Logger.Warning("Ambient wizards could not read the chat word lists: {Error}", Logger.Args(ex.Message));
            }
        });
    }

}
