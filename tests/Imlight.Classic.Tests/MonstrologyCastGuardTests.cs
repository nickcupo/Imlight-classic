using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Akka.Actor;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Common;
using Imlight.Classic;
using Imlight.CoreLib.Game.Combat;
using Imlight.CoreLib.Game.Monstrology;
using Imlight.CoreLib.Game.Zone.Components;
using Imlight.CoreLib.Shared.Behaviors;
using Imlight.CoreLib.Shared.Resources;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;
using Xunit;
using static Imlight.CoreLib.Shared.Packets.COMBAT_106_PROTOCOL;
namespace Imlight.Classic.Tests;

[Collection("ClassicRuntimeCollection")]
public sealed class MonstrologyCastGuardTests : IDisposable {
    private const uint SummonId = uint.MaxValue-971, KillId = uint.MaxValue-972, ExtractId = uint.MaxValue-973, DamageId = uint.MaxValue-974, MythId = uint.MaxValue-975;
    private readonly IDictionary<ulong,CoreTemplate> _cache;
    private readonly CombatDuelComponent _duel;
    private readonly CombatDuelSubCircle _owner, _target;
    private readonly MonstrologySessionPolicy _policy = new();
    private static SpellTemplate CreatureCard(bool kill) => new() { m_name="monstrology-guard-fixture", m_Treasure=true,
        m_adjectives=[kill?"MonsterMagicKill":"MonsterMagicSummon"],
        m_effects=[new SpellEffect {m_effectType=kill?kSpellEffects.kKillCreature:kSpellEffects.kSummonCreature,m_effectParam=35085,m_effectTarget=kEffectTarget.kSelf}] };
    public MonstrologyCastGuardTests() {
        ClassicRuntime.ResetForTests(); ClassicRuntime.Initialize(ClassicRules.Stock);
        Settings(true);
        _cache=(IDictionary<ulong,CoreTemplate>)typeof(CoreObjectFactory).GetField("s_templateCache",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
        _cache.Add(SummonId,CreatureCard(false)); _cache.Add(KillId,CreatureCard(true));
        _cache.Add(ExtractId,new SpellTemplate {m_name="extract-guard",m_adjectives=["Collect_Undead"],m_spellRank=new SpellRank(),
            m_effects=[new SpellEffect {m_effectType=kSpellEffects.kModifyCardDamage,m_effectParam=50,m_effectTarget=kEffectTarget.kSpell},
                new SpellEffect {m_effectType=kSpellEffects.kCollectEssence,m_effectParam=100,m_effectTarget=kEffectTarget.kSpell}]});
        _cache.Add(DamageId,new SpellTemplate {m_name="ordinary-hit",m_effects=[new SpellEffect {m_effectType=kSpellEffects.kDamage,m_effectParam=100,m_effectTarget=kEffectTarget.kEnemySingle}]});
        _cache.Add(MythId,new SpellTemplate {m_name="ordinary-myth-creature-summon",m_Treasure=false,m_sMagicSchoolName="Myth",
            m_effects=[new SpellEffect {m_effectType=kSpellEffects.kSummonCreature,m_effectParam=35085,m_effectTarget=kEffectTarget.kSelf}]});
        _duel=new CombatDuelComponent(null!);
        Property(_duel,"SubCircles",Enumerable.Range(0,8).Select(i=>new CombatDuelSubCircle(_duel,0,0,default,i)).ToArray());
        Property(_duel,"Duel",new Duel {m_duelID=42,m_duelModifier=new DuelModifier {m_battlefieldEffects=[]}});
        Property(_duel,"CombatResolver",new Imlight.CoreLib.Game.Combat.CombatResolver(_duel.Duel,_duel.SubCircles)); _duel.CombatResolver.Reset();
        _owner=Occupy(0,true); _target=Occupy(1,false);
        _owner._wizard=new Wizard {CharId=42,GameStats=_owner.ParticipantGameStats,SpellbookBehavior=new ServerWizSpellbookBehavior()};
        _owner._wizard.SpellbookBehavior.AddTreasureCard(SummonId);
        MonstrologySessionPolicy.Bind(_owner._wizard,_policy);
    }
    public void Dispose() { foreach(var id in new[]{SummonId,KillId,ExtractId,DamageId,MythId}) _cache.Remove(id); Settings(false); ClassicRuntime.ResetForTests(); }
    private CombatDuelSubCircle Occupy(int slot,bool player) {
        var circle=_duel.SubCircles[slot];
        Property(circle,"ParticipantObject",new CoreObject {m_templateID=player?1UL:2UL,m_globalID=(ulong)slot+10});
        Property(circle,"ParticipantActor",ActorRefs.Nobody);
        var stats=(ServerWizGameStats)RuntimeHelpers.GetUninitializedObject(typeof(ServerWizGameStats)); stats.m_currentHitpoints=100;
        Property(circle,"ParticipantGameStats",stats);
        Property(circle,"CombatParticipant",new CombatParticipant {m_subcircle=slot,m_isPlayer=player,m_hangingEffects=[],m_pipCount=new PipCount {m_genericPips=7}});
        circle.AddedToDuel=true; circle._combatDeck=new CombatDeck([],[],7); return circle;
    }
    private static Spell Card(uint id,uint enchantment=0) => new() {m_templateID=id,m_enchantment=enchantment,m_pipCost=new SpellRank {m_spellRank=2}};
    private static void Settings(bool enabled) {
        var p=Path.GetTempFileName();
        try {File.WriteAllText(p,$"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}monstrology-cast-tests.log\n[Classic]\nMonstrology={enabled}\n");ConfigurationManager.Initialize(p);}finally {File.Delete(p);}
    }
    private static void Property(object obj,string name,object value) => obj.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(obj,value);
    private static object? Invoke(object obj,string method,params object[] args) => obj.GetType().GetMethod(method,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(obj,args);

    [Fact] public void ClassifierRequiresSpecificIdentityAndVerifiedEffectsNotMythSchool() {
        Assert.True(MonstrologyCastGuard.IsTemplate(CreatureCard(false)));
        Assert.True(MonstrologyCastGuard.IsTemplate(CreatureCard(true)));
        var noTag=CreatureCard(false) with {m_adjectives=[]};
        Assert.False(MonstrologyCastGuard.IsTemplate(noTag,"Spells/MythMinion.xml"));
        Assert.True(MonstrologyCastGuard.IsTemplate(noTag,"Spells/MonsterMagicTC/SummonLostSoulTC.xml"));
        Assert.False(MonstrologyCastGuard.IsTemplate(CreatureCard(false) with {m_effects=[new SpellEffect {m_effectType=kSpellEffects.kDamage}]}));
        Assert.False(MonstrologyCastGuard.IsTemplate((SpellTemplate)_cache[MythId],"Spells/MythMinion.xml"));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void StrictOrGlobalOffAttackAcceptanceDoesNotConsumeOwnedCard(bool strict) {
        var card=Card(SummonId); _owner._combatDeck.AddCardToHand(card);
        if(strict) _policy.Negotiate(1,true); else Settings(false);
        Invoke(_duel,"HandleAttackMove",_owner,0,1u);
        Assert.Null(_duel.CombatResolver.GetQueuedAction(_owner)!.Spell);
        Assert.Same(card,Assert.Single(_owner._combatDeck.LastGivenHand));
        Assert.Single(_owner._wizard.SpellbookBehavior.TreasureCardTemplateIds);
        Assert.Equal(100,_target.ParticipantGameStats.m_currentHitpoints);
    }
    [Theory] [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void StrictOrGlobalOffAfterQueueTurnsCardIntoPassWithoutEffectsOrCosts(bool strict,bool prepared) {
        var card=prepared ? Card(DamageId,ExtractId) : Card(KillId); _owner._combatDeck.AddCardToHand(card);
        _duel.CombatResolver.AddCombatMove(CombatMoveType.Attack,_owner,_target,card);
        Assert.Same(card,_duel.CombatResolver.GetQueuedAction(_owner)!.Spell);
        if(strict) _policy.Negotiate(1,true); else Settings(false);
        var actions=new CombatActionListObj {m_actionList=[]};
        Invoke(_duel.CombatResolver,"ProcessQueuedActions",actions);
        Assert.Null(Assert.Single(actions.m_actionList).m_spell);
        Assert.Equal(100,_target.ParticipantGameStats.m_currentHitpoints);
        Assert.Equal(7,_owner.CombatParticipant.m_pipCount.m_genericPips);
        Assert.Equal(0,_owner._usedPipsForExperienceGain);
        Assert.Same(card,Assert.Single(_owner._combatDeck.LastGivenHand));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void StrictOrGlobalOffBlocksPreparedExtractionDamageAndEnchantmentBeforeTryEnchant(bool strict) {
        var source=Card(ExtractId); var target=Card(DamageId);
        _owner._combatDeck.AddCardToHand(source); _owner._combatDeck.AddCardToHand(target);
        if(strict) _policy.Negotiate(1,true); else Settings(false);
        var ran=false;
        var accepted=(bool)Invoke(_duel,"RunMonstrologyEnchantment",_owner,0,1u,(System.Action)(()=>{ran=true;_owner._combatDeck.TryEnchant(0,1,out _);}))!;
        Assert.False(accepted); Assert.False(ran); Assert.Equal(2,_owner._combatDeck.LastGivenHand.Count); Assert.Equal(0u,target.m_enchantment);
        var prepared=Card(DamageId,ExtractId);
        Assert.False(_duel.RunMonstrologyCast(_owner,prepared,(SpellTemplate)_cache[DamageId],()=>ran=true)); Assert.False(ran);
    }
    [Fact] public void OrdinaryMythSummonQueuesAndExecutesWithStrictAndGlobalOff() {
        _policy.Negotiate(1,true); Settings(false);
        var card=Card(MythId);
        _duel.CombatResolver.AddCombatMove(CombatMoveType.Attack,_owner,_owner,card);
        Assert.Same(card,_duel.CombatResolver.GetQueuedAction(_owner)!.Spell);
        var ran=false; Assert.True(_duel.RunMonstrologyCast(_owner,card,(SpellTemplate)_cache[MythId],()=>ran=true)); Assert.True(ran);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void ControlledMinionMonstrologyOrderUsesCapturedOwnerSessionAndOrdinaryCardSurvives(bool strict) {
        var minion=Occupy(2,false); Property(minion,"IsSummonedMinion",true); minion.CaptureMinionOwner(_owner.SlotIndex);
        var card=Card(SummonId); minion._combatDeck.AddCardToHand(card);
        Assert.True(_duel.AllowsMonstrologyCast(minion,card));
        var order=new OwnedMinionOrder((byte)CombatMoveType.Attack,0,(uint)minion.SlotIndex);
        Assert.Equal(OwnedMinionStatus.Accepted,_duel.ValidateOwnedMinionOrder(minion,order,out _,out _));
        if(strict) _policy.Negotiate(1,true); else Settings(false);
        Assert.False(_duel.AllowsMonstrologyCast(minion,card));
        Assert.Equal(OwnedMinionStatus.Disabled,_duel.ValidateOwnedMinionOrder(minion,order,out _,out _));
        minion._combatDeck.LastGivenHand[0]=Card(MythId);
        Assert.Equal(OwnedMinionStatus.Accepted,_duel.ValidateOwnedMinionOrder(minion,order,out _,out _));
        Assert.True(_duel.AllowsMonstrologyCast(minion,Card(MythId)));
    }
}
