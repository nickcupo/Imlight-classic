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
*/

using Imlight.CoreLib.WizardData.Databases;
using Imlight.CoreLib.WizardData.Models.Misc;
using Raven.Client.Documents;

namespace Imlight.CoreLib.WizardData.Collections;

public static class ChatLogCollection {

    public const string CollectionName = "ChatLog";
    private static readonly IDocumentStore s_store;

    static ChatLogCollection() {
        s_store = PlayerDatabase.Instance.Store;
    }

    /// <summary>
    /// Adds a chat log to the collection.
    /// </summary>
    /// <param name="chatLog">The chat log to add.</param>
    public static void AddChatLog(ChatLog chatLog) {
        using var session = s_store.OpenSession();

        session.Store(chatLog);
        var metadata = session.Advanced.GetMetadataFor(chatLog);
        metadata[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;

        session.SaveChanges();
    }

    // CLASSIC: chat logs written off the speaker's thread, in order, by one background writer (a Say used to wait for
    // the database write before reaching anyone). Logs still queued when the server stops are lost.
    private static readonly System.Threading.Channels.Channel<ChatLog> s_pending =
        System.Threading.Channels.Channel.CreateUnbounded<ChatLog>(new() { SingleReader = true });
    private static readonly System.Threading.Tasks.Task s_writer = System.Threading.Tasks.Task.Run(WriteQueuedAsync);

    /// <summary>
    /// CLASSIC: queues a chat log for the background writer; returns at once.
    /// </summary>
    /// <param name="chatLog">The chat log to add.</param>
    public static void QueueChatLog(ChatLog chatLog) {
        _ = s_writer;
        s_pending.Writer.TryWrite(chatLog);
    }

    private static async System.Threading.Tasks.Task WriteQueuedAsync() {
        var batch = new System.Collections.Generic.List<ChatLog>();
        while (await s_pending.Reader.WaitToReadAsync()) {
            batch.Clear();
            while (batch.Count < 64 && s_pending.Reader.TryRead(out var log)) {
                batch.Add(log);
            }

            try {
                using var session = s_store.OpenAsyncSession();
                foreach (var log in batch) {
                    await session.StoreAsync(log);
                    session.Advanced.GetMetadataFor(log)[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;
                }

                await session.SaveChangesAsync();
            }
            catch (System.Exception ex) {
                Imlight.Common.Logger.Error("Chat log write of {Count} line(s) failed: {Error}",
                    Imlight.Common.Logger.Args(batch.Count, ex.Message));
            }
        }
    }

}
