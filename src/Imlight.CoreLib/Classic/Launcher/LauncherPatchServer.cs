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
 * LAUNCHER PATCH SERVER
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the patch server KingsIsle's own launcher talks to after it signs
 * in ([Classic] LauncherPatchPort; its PatchConfig.xml names this port). It
 * answers MSG_LATEST_FILE_LIST_V2 with LauncherFileList: a list with nothing
 * to patch, downloaded over HTTP from the launcher port
 * (http://<server>:<MinionHelperPort>/launcher/LatestFileList.bin). The
 * normal patch server (PatchServerPort) is unchanged.
 *
 * USAGE EXAMPLE:
 * LauncherPatchServer.Start(actorSystem);   // Director start-up; off when the port is empty or 0
 *
 * NOTE:
 * The URL names the address the launcher connected to, so a LAN friend's
 * launcher fetches it from the same server.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */
#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using Akka.Actor;
using Imcodec.MessageLayer.Generated;
using Imlight.Common;
using Imlight.CoreLib.Shared.Networking;
using Imlight.CoreLib.Shared.Services;

namespace Imlight.CoreLib.Classic.Launcher;

public sealed class LauncherPatchServer : Server {

    internal const string Name_ = "Imlight.LauncherPatch";

    public LauncherPatchServer(int port) : base(Name_, port, LauncherPatchServiceFactory.Props(), "127.0.0.1") {
        Logger.Information("Launcher patch server listening on port {0}.", Logger.Args(port));
    }

    /// <summary>[Classic] LauncherPatchPort, or 0 when it is off.</summary>
    internal static int ConfiguredPort
        => ParsePort(ConfigurationManager.Settings["Classic.LauncherPatchPort"].AsString());

    internal static int ParsePort(string? value)
        => int.TryParse(value?.Trim(), out var port) && port is > 0 and < 65536 ? port : 0;

    /// <summary>Starts it when the port is configured.</summary>
    public static void Start(ActorSystem system) {
        var port = ConfiguredPort;
        if (port == 0) return;
        system.ActorOf(Akka.Actor.Props.Create(() => new LauncherPatchServer(port)), Name_);
    }

    /// <summary>An endpoint's address as a URL host ("192.168.1.75"; IPv4-mapped IPv6 unwrapped).</summary>
    internal static string AddressText(EndPoint? endPoint) {
        if (endPoint is not IPEndPoint ip) return "127.0.0.1";
        var address = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
    }

    /// <summary>The answer to the launcher's file-list request, for a server reached at <paramref name="host"/>.</summary>
    internal static PATCH_8_PROTOCOL.MSG_LATEST_FILE_LIST_V2 Answer(string host, int httpPort, string? locale, uint fileTime) {
        var prefix = $"http://{host}:{httpPort}/launcher";
        return new PATCH_8_PROTOCOL.MSG_LATEST_FILE_LIST_V2 {
            LatestVersion = 1,
            ListFileName = LauncherFileList.FileName,
            ListFileType = 1, // the client only parses type 1 (see PatchService)
            ListFileTime = fileTime,
            ListFileSize = (uint) LauncherFileList.Bytes.Length,
            ListFileCRC = LauncherFileList.Crc,
            ListFileURL = $"{prefix}/{LauncherFileList.FileName}",
            URLPrefix = prefix,
            URLSuffix = "",
            Locale = locale ?? "",
        };
    }

    internal static readonly uint StartTime = (uint) DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

internal sealed class LauncherPatchServiceFactory : ServiceFactory {
    protected override HashSet<Type> ServiceTypes { get; set; } = [
        typeof(ControlService),
        typeof(LauncherPatchService),
    ];

    public static Props Props() => Akka.Actor.Props.Create(() => new LauncherPatchServiceFactory());
}

internal sealed class LauncherPatchService(SessionActor sessionActor) : MessageService(sessionActor) {

    [MessageHandler(typeof(PATCH_8_PROTOCOL.MSG_LATEST_FILE_LIST_V2))]
    private void ReceiveLatestFileListV2(PATCH_8_PROTOCOL.MSG_LATEST_FILE_LIST_V2 message) {
        var httpPort = Imlight.CoreLib.Classic.MinionHelper.MinionHelperListener.ConfiguredPort;
        Logger.Information("Launcher patch: {0} asked for the file list; nothing to patch", Logger.Args(SessionActor.RemoteIp));
        SendToSocket(LauncherPatchServer.Answer(SessionActor.LocalIp ?? "127.0.0.1", httpPort, message.Locale,
            LauncherPatchServer.StartTime));
    }
}
