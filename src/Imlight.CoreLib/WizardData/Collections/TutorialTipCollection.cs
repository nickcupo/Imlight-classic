// CLASSIC (2026-10-17): each wizard's read tutorial tips (Imlight.Classic.Net.TutorialTipLog), one small document per
// character, as the Bazaar stock is stored.
#nullable enable

using System.Collections.Generic;
using System.Linq;
using Imlight.CoreLib.WizardData.Databases;

namespace Imlight.CoreLib.WizardData.Collections;

internal sealed class TutorialTips {

    public string Id { get; set; } = "";

    public List<uint> Read { get; set; } = [];

}

internal static class TutorialTipCollection {

    public const string CollectionName = "TutorialTips";

    public static string DocumentId(ulong charId) => $"classic/tutorial-tips/{charId}";

    public static List<uint> Load(ulong charId) {
        using var session = PlayerDatabase.Instance.Store.OpenSession();

        return session.Load<TutorialTips>(DocumentId(charId))?.Read ?? [];
    }

    public static void Save(ulong charId, IEnumerable<uint> read) {
        using var session = PlayerDatabase.Instance.Store.OpenSession();
        var id = DocumentId(charId);
        var doc = session.Load<TutorialTips>(id);
        if (doc is null) {
            doc = new TutorialTips { Id = id };
            session.Store(doc, id);
            session.Advanced.GetMetadataFor(doc)[Raven.Client.Constants.Documents.Metadata.Collection] = CollectionName;
        }

        doc.Read = [.. read.Distinct().Take(Imlight.Classic.Net.TutorialTipLog.MaxTips)];
        session.SaveChanges();
    }

}
