// CLASSIC: real sigil selection and native duel bytes must use the same camera/seating template.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Imcodec.ObjectProperty;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Sigils;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Game.Zone.Core;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ArenaNativeSigilIdentityTests : IDisposable {
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Dictionary<string, SigilTemplate?> _previous = [];
    private readonly IDictionary<string, SigilTemplate> _templates;

    public ArenaNativeSigilIdentityTests() {
        EquipmentAttachConcurrencyTests.Configure();
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
        _templates = (IDictionary<string, SigilTemplate>)typeof(SigilFactory)
            .GetField("s_combatSigils", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    }

    public void Dispose() {
        foreach (var (name, previous) in _previous) {
            if (previous is null) _templates.Remove(name);
            else _templates[name] = previous;
        }
        ClassicRuntime.ResetForTests();
    }

    [Fact]
    public void BeforeSigilSelectionNativeBehaviorKeepsTheExistingStreetDefault() {
        var component = new CombatDuelComponent(null!);
        var behavior = component.GetClientBehaviorInstance();
        Assert.Null(behavior.m_pDuel);
        AssertNativeSigil(behavior, 1901671683u);
    }

    [Theory]
    [InlineData("CombatSigil8Actor", false, 1901671683u)]
    [InlineData("PvPSigil8Actor2Sides", true, 1924535158u)]
    public void ActualSigilDetailsAndNativeBehaviorAdvertiseTheSelectedTemplate(
        string name, bool pvp, uint expectedNativeId) {
        var component = Select(name, pvp);
        var duel = component.Duel;
        var behavior = component.GetClientBehaviorInstance();
        Assert.Same(duel, behavior.m_pDuel);
        var decoded = AssertNativeSigil(behavior, expectedNativeId);
        Assert.Equal(duel.m_duelID.Full, decoded.m_pDuel.m_duelID.Full);
        Assert.Equal(pvp, decoded.m_pDuel.m_bPVP);
        Assert.Equal(duel.m_firstTeamToAct, decoded.m_pDuel.m_firstTeamToAct);
        Assert.Equal(name, SelectedTemplate(component).m_sigilName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void ArenaCameraIdentityKeepsBothNativeParticipantSidesWhenTheOpponentArrivesFirst(int ownerSlot) {
        var component = Select("PvPSigil8Actor2Sides", true);
        var opponentSlot = ownerSlot == 0 ? 4 : 0;
        InitializeWizard(component, opponentSlot);
        InitializeWizard(component, ownerSlot);
        var before = component.SubCircles.Select(circle =>
            (circle.PvpTeam, circle.SlotIndex, circle.WorldPosition, circle.WorldRotation)).ToArray();
        component.Duel.m_flatParticipantList = [component.SubCircles[opponentSlot].CombatParticipant,
            component.SubCircles[ownerSlot].CombatParticipant];

        var decoded = AssertNativeSigil(component.GetClientBehaviorInstance(), 1924535158u);

        Assert.Equal(before, component.SubCircles.Select(circle =>
            (circle.PvpTeam, circle.SlotIndex, circle.WorldPosition, circle.WorldRotation)).ToArray());
        Assert.Equal(1, decoded.m_pDuel.m_firstTeamToAct);
        Assert.Equal(2, decoded.m_pDuel.m_flatParticipantList.Count);
        foreach (var slot in new[] { 0, 4 }) {
            Assert.Equal(slot == 0 ? CombatTeam.Monster : CombatTeam.Player, component.SubCircles[slot].PvpTeam);
            var participant = Assert.Single(decoded.m_pDuel.m_flatParticipantList.Where(p => p.m_subcircle == slot));
            Assert.True(participant.m_isPlayer);
            Assert.Equal(0u, participant.m_isMonster);
            Assert.Equal(slot == 0 ? 1 : 0, participant.m_teamID);
            Assert.Equal(0, participant.m_originalTeam);
            Assert.Equal(slot + 9, (int)participant.m_dynamicSymbol);
            Assert.Equal(9000UL + (ulong)slot, participant.m_ownerID.Full);
        }
    }

    [Theory]
    [InlineData(MagicSchool.Fire, MagicSchool.Ice, true)]
    [InlineData(MagicSchool.Ice, MagicSchool.Storm, true)]
    [InlineData(MagicSchool.Storm, MagicSchool.Myth, true)]
    [InlineData(MagicSchool.Myth, MagicSchool.Life, true)]
    [InlineData(MagicSchool.Life, MagicSchool.Death, true)]
    [InlineData(MagicSchool.Death, MagicSchool.Balance, true)]
    [InlineData(MagicSchool.Balance, MagicSchool.Fire, true)]
    [InlineData(MagicSchool.Fire, MagicSchool.Ice, false)]
    [InlineData(MagicSchool.Ice, MagicSchool.Storm, false)]
    [InlineData(MagicSchool.Storm, MagicSchool.Myth, false)]
    [InlineData(MagicSchool.Myth, MagicSchool.Life, false)]
    [InlineData(MagicSchool.Life, MagicSchool.Death, false)]
    [InlineData(MagicSchool.Death, MagicSchool.Balance, false)]
    [InlineData(MagicSchool.Balance, MagicSchool.Fire, false)]
    public void NativeCombatSnapshotKeepsEachAuthoritativeSchoolOnBothPlayerSides(
        MagicSchool ownerSchool, MagicSchool opponentSchool, bool loadedFromDatabase) {
        var component = Select("PvPSigil8Actor2Sides", true);
        var opponent = InitializeWizard(component, 0, opponentSchool, loadedFromDatabase);
        var owner = InitializeWizard(component, 4, ownerSchool, loadedFromDatabase);
        component.Duel.m_flatParticipantList = [component.SubCircles[0].CombatParticipant,
            component.SubCircles[4].CombatParticipant];
        var decoded = AssertNativeSigil(component.GetClientBehaviorInstance(), 1924535158u);

        foreach (var (slot, wizard) in new[] { (0, opponent), (4, owner) }) {
            var expected = (uint)wizard.MagicSchoolBehavior.MagicSchool;
            // The real saved-wizard initializer or new/ambient stats constructor derives the identity;
            // the fixture never assigns m_schoolID directly. Native participant fields must agree.
            Assert.Equal(expected, wizard.GameStats.m_schoolID);
            var participant = Assert.Single(decoded.m_pDuel.m_flatParticipantList.Where(p => p.m_subcircle == slot));
            Assert.Equal(expected, (uint)participant.m_primaryMagicSchoolID);
            Assert.Equal(expected, participant.m_pGameStats.m_schoolID);
            Assert.Equal(participant.m_primaryMagicSchoolID, (int)participant.m_pGameStats.m_schoolID);
        }
    }

    private CombatDuelComponent Select(string name, bool pvp) {
        if (!_previous.ContainsKey(name)) _previous[name] = _templates.TryGetValue(name, out var previous) ? previous : null;
        var template = new CombatSigilTemplate {
            m_sigilName = name,
            m_subCircles = Enumerable.Range(0, 8).Select(slot => new SigilSubCircle {
                m_locationType = !pvp && slot < 4 ? "MonsterCircle" : "PlayerCircle",
                m_locationPreference = "Authored seat " + slot,
                m_rotation = slot * 45f, m_radius = 100f,
            }).ToList(),
        };
        _templates[name] = template;
        var entity = (ZoneEntity)RuntimeHelpers.GetUninitializedObject(typeof(ZoneEntity));
        CombatRegressionTests.SetProperty(entity, nameof(ZoneEntity.ActiveGameObject), new CoreObject { m_globalID = 8000 });
        var component = new CombatDuelComponent(entity);
        CombatRegressionTests.Invoke(component, "ReceiveSigilDetails", new ZONE_102_PROTOCOL.MSG_SIGILDETAILS {
            CombatSigilObjectInfo = new CombatSigilObjectInfo { m_sigilType = name, m_zoneTag = "Authored sigil fixture" },
        });
        Assert.Same(template, SelectedTemplate(component));
        typeof(CombatDuelComponent).GetField("_pvp", Private)!.SetValue(component, pvp);
        CombatRegressionTests.SetProperty(component, nameof(CombatDuelComponent.Duel),
            CombatRegressionTests.Invoke(component, "CreateDuelWithDefaults")!);
        component.Duel.m_firstTeamToAct = 1;
        CombatRegressionTests.SetProperty(component, nameof(CombatDuelComponent.SubCircles),
            CombatRegressionTests.Invoke(component, "CreateDuelActorSubCircles", template)!);
        return component;
    }

    private static CombatSigilTemplate SelectedTemplate(CombatDuelComponent component)
        => (CombatSigilTemplate)typeof(CombatDuelComponent).GetField("_sigilTemplate", Private)!.GetValue(component)!;

    private static Wizard InitializeWizard(CombatDuelComponent component, int slot, MagicSchool school = MagicSchool.Ice,
        bool loadedFromDatabase = true) {
        var circle = component.SubCircles[slot];
        var wizard = (Wizard)RuntimeHelpers.GetUninitializedObject(typeof(Wizard));
        wizard.GameStats = loadedFromDatabase
            ? (ServerWizGameStats)RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats))
            : new ServerWizGameStats(school, 50); // same constructor used by CreateAmbient/InitializeWizardGameStats
        wizard.GameStats.m_currentHitpoints = wizard.GameStats.m_baseHitpoints = 100;
        wizard.SpellbookBehavior = new();
        wizard.EquipmentBehavior = new() { SlotList = [] };
        wizard.MagicSchoolBehavior = new() { MagicSchool = school, Level = 50 };
        wizard.Account = (Account)RuntimeHelpers.GetUninitializedObject(typeof(Account));
        wizard.Account.Characters = [wizard];
        if (loadedFromDatabase) CombatRegressionTests.Invoke(wizard, "AfterDatabaseLoadWizardGameStats");
        circle._wizard = wizard;
        CombatRegressionTests.SetProperty(circle, nameof(CombatDuelSubCircle.ParticipantObject),
            new CoreObject { m_templateID = 1, m_globalID = 9000UL + (ulong)slot });
        CombatRegressionTests.Invoke(circle, "InitializePlayerSubCircleState");
        return wizard;
    }

    private static WizardClientDuelBehavior AssertNativeSigil(WizardClientDuelBehavior behavior, uint expectedNativeId) {
        var codec = new ObjectSerializer(Versionable: false, Behaviors: SerializerFlags.None);
        Assert.Equal(expectedNativeId, behavior.m_sigilTemplateID);
        Assert.True(codec.Serialize(behavior, (PropertyFlags)4, out var bytes));
        var raw = (byte[])bytes;
        // Native public duel behavior: real class hash, inherited duel body, then the sigil-template uint.
        Assert.Equal(1508636938u, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4)));
        Assert.Equal(expectedNativeId, BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(raw.Length - 4, 4)));
        Assert.True(codec.Deserialize<WizardClientDuelBehavior>(raw, (PropertyFlags)4, out var decoded));
        return Assert.IsType<WizardClientDuelBehavior>(decoded);
    }
}
