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
 * CLASSIC DIALOG CAMERA
 * ========================================================================
 *
 * PURPOSE:
 * The modern quest dialogs direct the camera (a named camera elsewhere in
 * the zone, offsets, shakes, fades, a second camera), so accepting or turning
 * in a quest flies the camera across the zone. 2009 dialogs did not
 * (playtest 2026-09-28 #5). Under a classic profile a dialog goes to the
 * client without its camera directions; text, portraits, sounds and
 * animations are unchanged.
 *
 * USAGE EXAMPLE:
 * serializer.Serialize(ClassicDialogCamera.ForClient(dialog), 16, out var data);
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 09/29/2026
 */

#nullable enable

using System.Linq;
using Imcodec.ObjectProperty.TypeCache;
using Imlight.Classic;

namespace Imlight.CoreLib.Classic;

/// <summary>
/// Dialogs without camera directions for classic profiles.
/// </summary>
public static class ClassicDialogCamera {

    /// <summary>
    /// <paramref name="dialog"/> as the client should get it: a copy without camera directions under a classic
    /// profile, else the dialog itself. The shared template is never changed.
    /// </summary>
    public static ActorDialog ForClient(ActorDialog dialog) {
        if (!ClassicRuntime.IsActive || dialog?.m_dialogEntries is null) {
            return dialog!;
        }

        return dialog with { m_dialogEntries = [.. dialog.m_dialogEntries.Select(WithoutCamera)] };
    }

    internal static ActorDialogEntry WithoutCamera(ActorDialogEntry entry) => entry is null ? entry! : entry with {
        m_cameraName = "",
        m_cameraZoneName = "",
        m_interpolationDuration = 0f,
        m_cameraOffsetX = 0f,
        m_cameraOffsetY = 0f,
        m_cameraOffsetZ = 0f,
        m_pitch = 0f,
        m_yaw = 0f,
        m_roll = 0f,
        m_cameraShakeType = "",
        m_cameraShakeDuration = 0f,
        m_cameraShakeAmplitude = 0f,
        m_cameraHidePlayers = 0,
        m_secondaryCameraName = "",
        m_secondaryInterpolationDuration = 0f,
        m_secondaryCameraInitalDelay = 0f,
        m_fadeOutCamera = false,
        m_cameraFadeType = "",
        m_cameraFadeTime = 0f,
        m_dontReleaseCameraAtExit = false,
    };

}
