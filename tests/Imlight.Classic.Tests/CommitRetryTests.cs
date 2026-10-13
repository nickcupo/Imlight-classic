using System;
using System.Collections.Generic;
using System.IO;
using Imlight.Common;
using System.Reflection;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.WizardData.Collections;
using Imlight.CoreLib.WizardData.Models.Player;
using Raven.Client.Documents.Session;
using Xunit;

namespace Imlight.Classic.Tests;

/// <summary>A save refused for a document changed outside the write lane is done again, once the change is loaded afresh.</summary>
[Collection(nameof(ClassicRuntimeCollection))]
public sealed class CommitRetryTests {

    public CommitRetryTests() {
        var config = Path.Combine(Path.GetTempPath(), $"classic-commit-retry-{Guid.NewGuid():N}.ini");
        File.WriteAllText(config, $"[Logging]\nLogLevel=FATAL\nLogPath={Path.GetTempPath()}classic-commit-retry.log\n[Character]\nMaxInventoryItems=150\n");
        ConfigurationManager.Initialize(config);
    }

    [Fact]
    public void ACharacterChangeRefusedByAConcurrentWriteIsRetriedAndCommitsOnce() {
        var opened = 0;
        var operations = 0;
        var after = 0;
        IDocumentSession Open() {
            opened++;
            var session = DispatchProxy.Create<IDocumentSession, HousingTests.HousingSessionProxy>();
            var proxy = (HousingTests.HousingSessionProxy)(object)session;
            proxy.Saved = new Dictionary<string, object>();
            var saves = 0;
            proxy.Fail = () => ++saves == 1 && opened == 1 ? throw new Raven.Client.Exceptions.ConcurrencyException("changed meanwhile") : false;
            return session;
        }

        var committed = WizardCollection.CommitCharacterMutation(4242, (_, _) => { operations++; return true; }, _ => after++,
            Open, (_, _) => new Wizard { CharId = 4242 });

        Assert.True(committed);
        Assert.Equal((2, 2, 1), (opened, operations, after));
    }

}
