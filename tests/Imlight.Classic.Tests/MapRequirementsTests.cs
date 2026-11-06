// CLASSIC: the spellbook map's MSG_MAPREQUIREMENTS (r806919 sender 0x1409c02f0, applier 0x1409c06f0; ios-parity):
// non-versionable, no flags, mask 4 on the MapInfoRequirementsList, each doodle's RequirementList (raw binary in
// m_serializedRequirements) and the MapInfoRequirementsPassedList answer.

using System.Linq;
using Imcodec.IO;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

public sealed class MapRequirementsTests {

    private static readonly ObjectSerializer Serializer = new(Versionable: false, Behaviors: SerializerFlags.None);

    private static byte[] Requirements(params Requirement[] requirements) {
        Assert.True(Serializer.Serialize(new RequirementList { m_operator = Operator.ROP_AND, m_requirements = [.. requirements] },
            MapRequirements.Mask, out var bytes));
        return bytes;
    }

    private static ReqHasEntry Entry(string name) => new() { m_entryName = name, m_operator = Operator.ROP_AND };

    // As r806919 writes it: the list's hash, the count, then per doodle its hash, m_id and the requirement bytes as they are.
    private static byte[] Request(params (uint Id, byte[] Requirements)[] doodles) {
        var writer = new BitWriter();
        writer.WriteUInt32(2015100750);
        writer.WriteUInt32((uint) doodles.Length);
        foreach (var (id, requirements) in doodles) {
            writer.WriteUInt32(206696061);
            writer.WriteUInt32(id);
            writer.WriteString(new ByteString(requirements));
        }

        return writer.GetData();
    }

    [Fact]
    public void TheRequestReadsLikeImcodecsOwnLayout() {
        // Imcodec's own serialization of the list (ASCII payloads survive its string type) is the layout the reader expects.
        var list = new MapInfoRequirementsList { m_mapInfoRequirementsList = [
            new MapInfoRequirementsRequest { m_id = 7, m_serializedRequirements = "abc" },
            new MapInfoRequirementsRequest { m_id = 41, m_serializedRequirements = "" } ] };
        Assert.True(Serializer.Serialize(list, MapRequirements.Mask, out var data));
        Assert.True(MapRequirements.TryReadRequests(data, out var requests));
        Assert.Equal([7u, 41u], requests.Select(r => r.Id));
        Assert.Equal("abc"u8.ToArray(), requests[0].Requirements);
        Assert.Empty(requests[1].Requirements);
    }

    [Fact]
    public void TheRequirementBytesArriveExactAndDecode() {
        var bytes = Requirements(Entry("WC-RAV-Bernie"));
        Assert.Contains(bytes, b => b >= 0x80); // the hashes: what a UTF-8 string would have mangled
        Assert.True(MapRequirements.TryReadRequests(Request((3, bytes)), out var requests));
        Assert.Equal(bytes, Assert.Single(requests).Requirements);
        var decoded = MapRequirements.DecodeRequirements(requests[0].Requirements);
        Assert.Equal("WC-RAV-Bernie", (string) Assert.IsType<ReqHasEntry>(Assert.Single(decoded!.m_requirements)).m_entryName);
    }

    [Fact]
    public void OnlyDoodlesWhoseRequirementsPassAreAnswered() {
        var data = Request((1, Requirements(Entry("met"))), (2, Requirements(Entry("unmet"))), (3, [1, 2, 3]),
            (4, Requirements(new ReqHasQuest { m_questName = "Q" })));
        Assert.True(MapRequirements.TryReadRequests(data, out var requests));
        var verdicts = MapRequirements.Judge(requests,
            requirement => requirement is ReqHasEntry,
            list => list.m_requirements.OfType<ReqHasEntry>().All(e => (string) e.m_entryName == "met"));
        Assert.Equal([(1u, MapRequirements.Verdict.Passed), (2u, MapRequirements.Verdict.Failed),
            (3u, MapRequirements.Verdict.Unevaluable), (4u, MapRequirements.Verdict.Unevaluable)], verdicts);
    }

    [Fact]
    public void TheAnswerIsAPassedListUnderMaskFour() {
        var data = MapRequirements.WriteAnswer([1, 9]);
        Assert.NotNull(data);
        Assert.True(Serializer.Deserialize<PropertyClass>(data!, MapRequirements.Mask, out var obj));
        Assert.Equal([1u, 9u], Assert.IsType<MapInfoRequirementsPassedList>(obj).m_idList);
        Assert.True(Serializer.Deserialize<PropertyClass>(MapRequirements.WriteAnswer([])!, MapRequirements.Mask, out var none));
        Assert.Empty(Assert.IsType<MapInfoRequirementsPassedList>(none).m_idList);
    }

    [Fact]
    public void ARequestThatDoesNotParseIsRefused() {
        Assert.False(MapRequirements.TryReadRequests([], out _));
        Assert.False(MapRequirements.TryReadRequests([1, 2, 3, 4, 0, 0, 0, 0], out _));
        Assert.False(MapRequirements.TryReadRequests(Request((1, Requirements(Entry("x"))))[..10], out _));
    }
}
