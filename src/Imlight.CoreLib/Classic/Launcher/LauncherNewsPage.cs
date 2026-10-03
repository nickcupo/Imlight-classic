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
 * LAUNCHER NEWS PAGE
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC: the page KingsIsle's own launcher shows in its embedded browser
 * (its PatchConfig NewsURL, http://<server>:<MinionHelperPort>/launcher/news):
 * a Ravenwood News scroll in the 2009-2010 style with our news, a world
 * painting and the Wizard101 logo. Everything comes from our server:
 *   - the news from the published news.txt ([Classic] ClientPatchesPath;
 *     items split by a blank line, the first line the title, "icon: x"
 *     ignored here), or a welcome line when none is published;
 *   - the art from the published launcher art (manifest.json "launcher_art",
 *     extracted from the owner's own KingsIsle files by launcher-art.py and
 *     served at /classic/files/<sha256>). A missing piece is drawn in CSS.
 * No outside URL, script or font.
 *
 * Created by: Nick with Claude Code (claude-opus-5-5)
 * Version: KALI 1.0
 * Last Updated: 10/02/2026
 */
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Imlight.CoreLib.Classic.Launcher;

internal static class LauncherNewsPage {

    /// <summary>Where the page loads a published art piece (served as image/png by the launcher port).</summary>
    internal const string ArtPrefix = "/launcher/art/";

    internal const string DefaultNews = "Welcome to Wizard101 Classic!\nThe Spiral as it was in late 2009.";

    /// <summary>A news item: its title and text.</summary>
    internal readonly record struct Item(string Title, string Body);

    /// <summary>The page for the published folder <paramref name="root"/> (null: nothing published).</summary>
    internal static string Render(string? root) {
        var news = DefaultNews;
        var art = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root is not null) {
            try {
                var path = Path.Combine(root, "news.txt");
                if (File.Exists(path)) news = File.ReadAllText(path, Encoding.UTF8);
                art = ArtUrls(File.Exists(Path.Combine(root, "manifest.json")) ? File.ReadAllText(Path.Combine(root, "manifest.json")) : null);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Keep the defaults.
            }
        }
        return Render(Parse(news), art);
    }

    /// <summary>news.txt as items (the launcher window's format).</summary>
    internal static List<Item> Parse(string text) {
        var items = new List<Item>();
        foreach (var block in text.Replace("\r\n", "\n").Split("\n\n")) {
            var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0
                && !l.StartsWith("icon:", StringComparison.OrdinalIgnoreCase)).ToList();
            if (lines.Count == 0) continue;
            items.Add(new Item(lines[0].Trim('#', ' '), string.Join(" ", lines.Skip(1))));
        }
        return items;
    }

    /// <summary>name -> URL of each published launcher art piece ("/classic/files/&lt;sha&gt;").</summary>
    internal static Dictionary<string, string> ArtUrls(string? manifestJson) {
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(manifestJson)) return urls;
        try {
            using var doc = JsonDocument.Parse(manifestJson);
            if (!doc.RootElement.TryGetProperty("launcher_art", out var art) || art.ValueKind != JsonValueKind.Array) return urls;
            foreach (var piece in art.EnumerateArray()) {
                var name = piece.TryGetProperty("name", out var n) ? n.GetString() : null;
                var sha = piece.TryGetProperty("sha256", out var s) ? s.GetString() : null;
                if (name is null || sha is null || sha.Length != 64 || !sha.All(Uri.IsHexDigit)) continue;
                urls[name] = "/classic/files/" + sha.ToLowerInvariant();
            }
        } catch (JsonException) {
            // No art; the page draws plain pieces.
        }
        return urls;
    }

    internal static string Render(IReadOnlyList<Item> items, IReadOnlyDictionary<string, string> art) {
        static string H(string text) => WebUtility.HtmlEncode(text);
        string Url(string name) => art.ContainsKey(name) ? ArtPrefix + name : "";

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"><title>Ravenwood News</title><style>\n")
            .Append("html,body{margin:0;padding:0;height:100%;overflow:hidden;}\n")
            .Append("body{font-family:Georgia,'Times New Roman',serif;color:#2b1a08;background:#1d2a6b;");
        if (Url("world").Length > 0) html.Append($"background-image:url('{Url("world")}');background-size:cover;background-position:center;");
        html.Append("}\n")
            .Append("#scroll{position:absolute;left:18px;top:8px;bottom:58px;width:392px;border:4px solid #7a4a17;")
            .Append("border-radius:14px;background:#e9d29a;box-shadow:0 0 0 2px #e8c76a,0 4px 10px rgba(0,0,0,.6);");
        if (Url("parchment").Length > 0) html.Append($"background-image:url('{Url("parchment")}');");
        html.Append("}\n")
            .Append("#banner{position:absolute;left:50%;top:-6px;margin-left:-150px;width:300px;height:34px;line-height:34px;")
            .Append("text-align:center;font-size:21px;font-weight:bold;letter-spacing:1px;color:#5a2d06;")
            .Append("background:#f3e1ae;border:3px solid #7a4a17;border-radius:8px;font-variant:small-caps;")
            .Append("text-shadow:0 1px 0 #fff3cf;}\n")
            .Append("#list{position:absolute;left:12px;right:6px;top:40px;bottom:10px;overflow-y:auto;padding-right:8px;}\n")
            .Append(".item{padding:9px 4px 10px 4px;border-bottom:2px solid #3a240c;}\n.item:last-child{border-bottom:0;}\n")
            .Append(".item h2{margin:0 0 4px 0;font-size:15px;text-align:center;color:#1b1206;}\n")
            .Append(".item p{margin:0;font-size:14px;line-height:1.3;font-family:Tahoma,Verdana,Arial,sans-serif;}\n")
            .Append("#logo{position:absolute;right:28px;bottom:66px;width:300px;}\n")
            .Append("#notice{position:absolute;left:96px;right:96px;bottom:10px;height:30px;line-height:30px;text-align:center;")
            .Append("font-family:Tahoma,Verdana,Arial,sans-serif;font-size:13px;color:#fff;background:#241a4a;")
            .Append("border:2px solid #c9a94a;border-radius:15px;}\n#notice b{color:#fff;}\n")
            .Append("</style></head><body>\n<div id=\"scroll\"><div id=\"banner\">Ravenwood News</div><div id=\"list\">\n");
        foreach (var item in items) {
            html.Append("<div class=\"item\"><h2>").Append(H(item.Title)).Append("</h2>");
            if (item.Body.Length > 0) html.Append("<p>").Append(H(item.Body)).Append("</p>");
            html.Append("</div>\n");
        }
        html.Append("</div></div>\n");
        if (Url("logo").Length > 0) html.Append($"<img id=\"logo\" src=\"{Url("logo")}\" alt=\"Wizard101\">\n");
        html.Append("<div id=\"notice\"><b>NEVER</b> share your password - you will lose your Wizard forever!</div>\n")
            .Append("</body></html>\n");
        return html.ToString();
    }
}
