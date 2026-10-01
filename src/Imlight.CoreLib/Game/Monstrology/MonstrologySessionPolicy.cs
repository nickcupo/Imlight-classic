using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Imlight.CoreLib.Shared.Packets;
using Imlight.CoreLib.WizardData.Models.Player;

namespace Imlight.CoreLib.Game.Monstrology;

internal sealed class MonstrologySessionPolicy {
    internal const uint Capability = 1u << 1;
    private int _enhanced = 1; // Ordinary stock sessions default enhanced until a Hello declares strict.
    private readonly object _gate = new();
    private static readonly ConditionalWeakTable<Wizard, MonstrologySessionPolicy> s_wizards = new();
    internal bool Allows(bool globallyEnabled) => globallyEnabled && Volatile.Read(ref _enhanced) == 1;
    internal void Negotiate(ushort version, bool strictClassic) {
        lock (_gate) Volatile.Write(ref _enhanced, version == EnhancedClassicProtocol.Version && !strictClassic ? 1 : 0);
    }
    internal uint Advertise(bool globallyEnabled) => Allows(globallyEnabled) ? Capability : 0;
    internal MonstrologyResult WithPermission(bool globallyEnabled, Func<MonstrologyResult> operation) {
        lock (_gate) return Allows(globallyEnabled) ? operation() : MonstrologyResult.Rejected;
    }
    internal static void Bind(Wizard wizard, MonstrologySessionPolicy policy) {
        if (wizard != null) s_wizards.AddOrUpdate(wizard, policy);
    }
    internal static MonstrologySessionPolicy ForWizard(Wizard wizard)
        => wizard != null && s_wizards.TryGetValue(wizard, out var policy) ? policy : null;
    internal static bool AllowsWizard(Wizard wizard, bool globallyEnabled)
        => ForWizard(wizard)?.Allows(globallyEnabled) == true;
}
