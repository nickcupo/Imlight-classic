using System;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.CoreLib.Classic;
using Xunit;

namespace Imlight.Classic.Tests;

[Collection(nameof(ClassicRuntimeCollection))]
public sealed class ClassicDialogCameraTests : IDisposable {

    public ClassicDialogCameraTests() {
        ClassicRuntime.ResetForTests();
        ClassicRuntime.Initialize(ClassicDataFixture.RealRules("late-2009"));
    }

    public void Dispose() => ClassicRuntime.ResetForTests();

    [Fact]
    public void AClassicDialogKeepsItsTextButNotItsCameraAndTheTemplateIsUnchanged() {
        var entry = new ActorDialogEntry {
            m_dialog = "WizQst957D_00000014", m_soundFile = "a.ogg", m_cameraName = "Cam_Triton", m_cameraZoneName = "WC_Triton",
            m_cameraOffsetZ = 40f, m_yaw = 1.5f, m_secondaryCameraName = "Cam2", m_dontReleaseCameraAtExit = true,
        };
        var dialog = new ActorDialog { m_dialogTag = "Prep", m_dialogEntries = [entry] };

        var sent = Assert.Single(ClassicDialogCamera.ForClient(dialog).m_dialogEntries);

        Assert.Equal(("WizQst957D_00000014", "a.ogg"), (sent.m_dialog, sent.m_soundFile));
        Assert.Equal(("", "", ""), (sent.m_cameraName, sent.m_cameraZoneName, sent.m_secondaryCameraName));
        Assert.Equal((0f, 0f, false), (sent.m_cameraOffsetZ, sent.m_yaw, sent.m_dontReleaseCameraAtExit));
        Assert.Equal("Cam_Triton", entry.m_cameraName);
        Assert.Same(entry, Assert.Single(dialog.m_dialogEntries));
    }

    [Fact]
    public void ManyWizardsGettingTheSameDialogAtOnceAllGetACameraFreeCopy() {
        var entries = new System.Collections.Generic.List<ActorDialogEntry>();
        for (var i = 0; i < 6; i++) {
            entries.Add(new ActorDialogEntry { m_dialog = "k" + i, m_cameraName = "Cam", m_yaw = 2f });
        }

        var dialog = new ActorDialog { m_dialogTag = "Prep", m_dialogEntries = entries };
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        System.Threading.Tasks.Parallel.For(0, 400, _ => {
            try {
                var sent = ClassicDialogCamera.ForClient(dialog);
                Assert.Equal(6, sent.m_dialogEntries.Count);
                Assert.All(sent.m_dialogEntries, e => Assert.Equal(("", 0f), (e.m_cameraName, e.m_yaw)));
            }
            catch (Exception ex) {
                failures.Add(ex);
            }
        });

        Assert.Empty(failures);
        Assert.All(entries, e => Assert.Equal("Cam", e.m_cameraName));
    }

}
