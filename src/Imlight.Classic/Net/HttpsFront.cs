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
 * HTTPS FRONT (self-hosted TLS)
 * ========================================================================
 *
 * PURPOSE:
 * CLASSIC (outside-access): the server's own HTTPS listener, so the launcher
 * sign-in, the signed downloads and the friend page can cross the internet
 * without a third-party tunnel (docs/runbooks/outside-access.md). The
 * certificate is the owner's own (a public CA) or one from the owner's local
 * CA (tools/dist/w101c-tls.py), read from PEM files on the server:
 *  - TlsCertificateSource: the certificate chain and its private key, re-read
 *    when either file changes (renewal needs no restart). Refused, and the
 *    listener answers nobody, when the key file can be read by group/others,
 *    the key does not match, or the certificate is not yet / no longer valid.
 *  - HttpsFront: TLS 1.2/1.3 only, no client certificates, a 10 s handshake,
 *    per-address and total connection caps, and a sweep that closes clients the
 *    off switch no longer admits.
 *  - HttpRequestHead / LoopbackForwarder: one request per connection, strictly
 *    parsed; the friend page is forwarded to its loopback listener with the
 *    visitor's address in the one trusted header (any copy the client sent is
 *    dropped, as are all proxy headers).
 *
 * NOTE:
 * TLS only protects the transport. The signed tools/patch lists keep their own
 * Ed25519 checks on the player's machine whichever way they are fetched.
 *
 * Created by: Nick with Claude Code
 * Version: KALI 1.0
 * Last Updated: 10/28/2026
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Imlight.Classic.Net;

/// <summary>The certificate chain and key from two PEM files, re-read when they change.</summary>
public sealed class TlsCertificateSource {
    private readonly string _certFile;
    private readonly string _keyFile;
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private (DateTime Cert, DateTime Key, long CertLength, long KeyLength) _stamp;
    private SslStreamCertificateContext? _context;
    private X509Certificate2? _leaf;
    private string? _problem = "not loaded";

    public TlsCertificateSource(string certFile, string keyFile, Func<DateTime>? clock = null) {
        _certFile = certFile;
        _keyFile = keyFile;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>The current leaf certificate (for logs: subject, expiry, SHA-256), or null.</summary>
    public X509Certificate2? Leaf { get { lock (_gate) return _leaf; } }

    /// <summary>The context to serve, or null with <paramref name="problem"/> saying why (never key material).</summary>
    public SslStreamCertificateContext? Current(out string? problem) {
        lock (_gate) {
            try {
                var stamp = (File.GetLastWriteTimeUtc(_certFile), File.GetLastWriteTimeUtc(_keyFile),
                    new FileInfo(_certFile).Length, new FileInfo(_keyFile).Length);
                if (stamp != _stamp || _context is null && _problem is "not loaded") {
                    _stamp = stamp;
                    Load();
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                _context = null;
                _leaf = null;
                _problem = "the certificate or key file cannot be read";
            }

            if (_leaf is not null) {
                var now = _clock();
                if (now < _leaf.NotBefore.ToUniversalTime()) { problem = "the certificate is not valid yet"; return null; }
                if (now >= _leaf.NotAfter.ToUniversalTime()) { problem = "the certificate has expired"; return null; }
            }

            problem = _problem;
            return _context;
        }
    }

    private void Load() {
        _context = null;
        _leaf = null;
        if (!File.Exists(_certFile) || !File.Exists(_keyFile)) {
            _problem = "the certificate or key file is missing";
            return;
        }

        if (!OperatingSystem.IsWindows()) {
            var mode = File.GetUnixFileMode(_keyFile);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0) {
                _problem = "the key file can be read by other users (chmod 600 it)";
                return;
            }
        }

        try {
            var chain = new X509Certificate2Collection();
            chain.ImportFromPemFile(_certFile);
            if (chain.Count == 0) { _problem = "the certificate file holds no certificate"; return; }
            using var pem = X509Certificate2.CreateFromPemFile(_certFile, _keyFile);
            if (!pem.HasPrivateKey) { _problem = "the key does not match the certificate"; return; }
            // A PKCS#12 round trip gives a persisted key handle SslStream accepts on every platform.
            var leaf = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
            var intermediates = new X509Certificate2Collection();
            foreach (var extra in chain.Cast<X509Certificate2>().Skip(1)) intermediates.Add(extra);
            _context = SslStreamCertificateContext.Create(leaf, intermediates, offline: true);
            _leaf = leaf;
            _problem = null;
        } catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or ArgumentException or IOException) {
            _problem = "the certificate or key could not be loaded (PEM expected; does the key match?)";
        }
    }
}

/// <summary>The server's HTTPS listener: TLS, connection caps and the off-switch sweep; requests go to a callback.</summary>
public sealed class HttpsFront : IDisposable {
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener;
    private readonly TlsCertificateSource _certificates;
    private readonly Func<IPAddress?, bool> _admit;
    private readonly Func<Stream, IPEndPoint, Task> _serve;
    private readonly Action<string>? _log;
    private readonly int _maxConnections;
    private readonly int _maxPerAddress;
    private readonly ConcurrentDictionary<TcpClient, IPAddress> _open = new();
    private readonly ConcurrentDictionary<string, int> _perAddress = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private Timer? _sweep;
    private long _refused;
    private long _failedHandshakes;
    private string? _lastProblem;

    public HttpsFront(IPAddress address, int port, TlsCertificateSource certificates, Func<IPAddress?, bool> admit,
                      Func<Stream, IPEndPoint, Task> serve, Action<string>? log = null, int maxConnections = 128,
                      int maxPerAddress = 16) {
        _listener = new TcpListener(address, port);
        _certificates = certificates;
        _admit = admit;
        _serve = serve;
        _log = log;
        _maxConnections = maxConnections;
        _maxPerAddress = maxPerAddress;
    }

    /// <summary>The bound port (useful when started on port 0).</summary>
    public int Port => ((IPEndPoint) _listener.LocalEndpoint).Port;

    public int OpenConnections => _open.Count;

    public void Start() {
        _listener.Start();
        _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Closes every open connection the off switch no longer admits.</summary>
    public int Sweep() {
        var closed = 0;
        foreach (var (client, peer) in _open) {
            bool keep;
            try {
                keep = _admit(peer);
            } catch (Exception) {
                keep = false;
            }

            if (keep) continue;
            try {
                client.Client.Close(0);
            } catch (Exception) {
                // already gone
            }

            closed++;
        }

        if (closed > 0) _log?.Invoke($"HTTPS: closed {closed} outside connection(s) (outside access is off)");
        return closed;
    }

    private async Task AcceptLoop() {
        while (!_stop.IsCancellationRequested) {
            TcpClient client;
            try {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            } catch (OperationCanceledException) {
                return;
            } catch (ObjectDisposedException) {
                return;
            } catch (Exception ex) {
                _log?.Invoke("HTTPS accept failed: " + ex.GetType().Name);
                await Task.Delay(500);
                continue;
            }

            var peer = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            var key = PublicAccess.NormalizeAddress(peer?.ToString()) ?? "?";
            string? refusal;
            try {
                refusal = !_admit(peer) ? "outside access is off"
                    : ConnectionLimits.Refuse(key, _perAddress.GetValueOrDefault(key), _open.Count, _maxPerAddress, _maxConnections,
                        limitLoopback: true);
            } catch (Exception) {
                refusal = "admission check failed";
            }

            if (refusal is not null || peer is null) {
                if (Interlocked.Increment(ref _refused) % 100 == 1) _log?.Invoke($"HTTPS refused {key}: {refusal}");
                client.Dispose();
                continue;
            }

            _open[client] = peer;
            _perAddress.AddOrUpdate(key, 1, (_, n) => n + 1);
            _ = Task.Run(async () => {
                try {
                    await Handle(client, peer);
                } finally {
                    _open.TryRemove(client, out _);
                    if (_perAddress.AddOrUpdate(key, 0, (_, n) => n - 1) <= 0) {
                        _perAddress.TryRemove(new KeyValuePair<string, int>(key, 0));
                    }

                    client.Dispose();
                }
            });
        }
    }

    private async Task Handle(TcpClient client, IPAddress peer) {
        var context = _certificates.Current(out var problem);
        if (context is null) {
            if (problem != _lastProblem) {
                _lastProblem = problem;
                _log?.Invoke("HTTPS is not serving: " + problem);
            }

            return;
        }

        _lastProblem = null;
        client.NoDelay = true;
        await using var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        try {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            handshake.CancelAfter(HandshakeTimeout);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                ServerCertificateContext = context,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, handshake.Token);
        } catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException
                                         or ObjectDisposedException or SocketException) {
            if (Interlocked.Increment(ref _failedHandshakes) % 100 == 1) {
                _log?.Invoke($"HTTPS handshake from {PublicAccess.NormalizeAddress(peer.ToString())} failed ({ex.GetType().Name}); "
                             + $"{_failedHandshakes} so far");
            }

            return;
        }

        try {
            await _serve(tls, new IPEndPoint(peer, ((IPEndPoint) client.Client.RemoteEndPoint!).Port));
        } catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) {
            // the client went away
        }
    }

    public void Dispose() {
        _stop.Cancel();
        _sweep?.Dispose();
        try {
            _listener.Stop();
        } catch (Exception) {
            // not started
        }

        foreach (var client in _open.Keys) {
            try {
                client.Client.Close(0);
            } catch (Exception) {
                // already gone
            }
        }
    }
}

/// <summary>One parsed HTTP/1.x request head (request line and headers), strictly.</summary>
public sealed class HttpRequestHead {
    public const int MaxHeadBytes = 8 * 1024;
    public const int MaxHeaders = 64;

    public string Method { get; private init; } = "";
    public string Target { get; private init; } = "";
    public string Path { get; private init; } = "";
    public IReadOnlyList<(string Name, string Value)> Headers { get; private init; } = [];

    public string? Header(string name) {
        foreach (var (n, v) in Headers) {
            if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return v;
        }

        return null;
    }

    /// <summary>
    /// The body length the request declares: 0 without Content-Length, -1 when it is chunked, repeated, conflicting or
    /// not a plain number.
    /// </summary>
    public long ContentLength {
        get {
            if (Header("Transfer-Encoding") is not null) return -1;
            var lengths = Headers.Where(h => h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).ToList();
            if (lengths.Count == 0) return 0;
            if (lengths.Count > 1) return -1;
            var text = lengths[0].Value;
            return text.Length is > 0 and <= 12 && text.All(char.IsAsciiDigit) ? long.Parse(text) : -1;
        }
    }

    /// <summary>Parses the bytes before the blank line (exclusive). Null for anything malformed.</summary>
    public static HttpRequestHead? Parse(ReadOnlySpan<byte> head) {
        if (head.Length is 0 or > MaxHeadBytes) return null;
        foreach (var b in head) {
            if (b is 0 or >= 0x80) return null; // headers here are ASCII; no NUL, no obs-text
        }

        var lines = Encoding.ASCII.GetString(head).Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length != 3 || request[2] is not ("HTTP/1.1" or "HTTP/1.0")) return null;
        var method = request[0];
        var target = request[1];
        if (method.Length is 0 or > 8 || !method.All(char.IsAsciiLetterUpper)) return null;
        if (!target.StartsWith('/') || target.StartsWith("//", StringComparison.Ordinal) || target.Length > 2048) return null;
        var path = target.Split('?')[0];
        if (path.Contains("/../", StringComparison.Ordinal) || path.EndsWith("/..", StringComparison.Ordinal)
            || path.Contains('\\') || path.Contains('%')) return null;
        var headers = new List<(string, string)>();
        foreach (var line in lines.Skip(1)) {
            if (line.Length == 0) return null;
            if (line[0] is ' ' or '\t') return null; // no folded headers
            var colon = line.IndexOf(':');
            if (colon <= 0) return null;
            var name = line[..colon];
            if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
            var value = line[(colon + 1)..].Trim(' ', '\t');
            if (value.Any(c => c is '\r' or '\n' || c < 0x20 && c != '\t')) return null;
            headers.Add((name, value));
            if (headers.Count > MaxHeaders) return null;
        }

        return new HttpRequestHead { Method = method, Target = target, Path = path, Headers = headers };
    }
}

/// <summary>Forwards one request from the HTTPS front to a loopback HTTP listener and streams the answer back.</summary>
public static class LoopbackForwarder {
    public const int MaxBodyBytes = 64 * 1024;
    private static readonly TimeSpan s_idle = TimeSpan.FromSeconds(60);

    /// <summary>Headers never passed on: hop-by-hop, the host, and every proxy/visitor header the client could forge.</summary>
    private static readonly string[] s_dropped = [
        "Host", "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authorization", "Upgrade", "TE", "Trailer",
        "Transfer-Encoding", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Port",
    ];

    /// <summary>The head sent upstream: the same request line and end-to-end headers, plus Host, Connection: close and
    /// the visitor header.</summary>
    public static string UpstreamHead(HttpRequestHead head, int port, string visitorHeader, string visitor) {
        var builder = new StringBuilder();
        builder.Append(head.Method).Append(' ').Append(head.Target).Append(" HTTP/1.1\r\n");
        builder.Append("Host: 127.0.0.1:").Append(port).Append("\r\n");
        foreach (var (name, value) in head.Headers) {
            if (s_dropped.Any(d => d.Equals(name, StringComparison.OrdinalIgnoreCase))
                || PublicAccess.ProxyHeaders.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase))
                || name.Equals(visitorHeader, StringComparison.OrdinalIgnoreCase)) continue;
            builder.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        builder.Append(visitorHeader).Append(": ").Append(visitor).Append("\r\n");
        builder.Append("Connection: close\r\n\r\n");
        return builder.ToString();
    }

    /// <summary>
    /// <paramref name="buffer"/>[0..<paramref name="filled"/>) holds what was read so far: the head (ending at
    /// <paramref name="bodyStart"/>) and maybe part of the body. False when the request was answered with an error here.
    /// </summary>
    public static async Task<bool> ForwardAsync(Stream client, HttpRequestHead head, byte[] buffer, int filled, int bodyStart,
                                                int upstreamPort, string visitorHeader, string visitor,
                                                CancellationToken cancel = default) {
        var length = head.ContentLength;
        if (length < 0) {
            await Respond(client, "411 Length Required");
            return false;
        }

        if (length > MaxBodyBytes) {
            await Respond(client, "413 Content Too Large");
            return false;
        }

        using var upstream = new TcpClient();
        try {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            connect.CancelAfter(TimeSpan.FromSeconds(5));
            await upstream.ConnectAsync(IPAddress.Loopback, upstreamPort, connect.Token);
        } catch (Exception ex) when (ex is SocketException or OperationCanceledException) {
            await Respond(client, "502 Bad Gateway");
            return false;
        }

        var output = upstream.GetStream();
        await output.WriteAsync(Encoding.ASCII.GetBytes(UpstreamHead(head, upstreamPort, visitorHeader, visitor)), cancel);
        var already = (int) Math.Min(length, Math.Max(0, filled - bodyStart));
        if (already > 0) await output.WriteAsync(buffer.AsMemory(bodyStart, already), cancel);
        var remaining = length - already;
        var chunk = new byte[16 * 1024];
        while (remaining > 0) {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            idle.CancelAfter(s_idle);
            var read = await client.ReadAsync(chunk.AsMemory(0, (int) Math.Min(chunk.Length, remaining)), idle.Token);
            if (read == 0) return false;
            await output.WriteAsync(chunk.AsMemory(0, read), cancel);
            remaining -= read;
        }

        while (true) {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            idle.CancelAfter(s_idle);
            var read = await output.ReadAsync(chunk, idle.Token);
            if (read == 0) break;
            await client.WriteAsync(chunk.AsMemory(0, read), idle.Token);
        }

        return true;
    }

    public static async Task Respond(Stream client, string status) {
        var head = $"HTTP/1.1 {status}\r\nContent-Type: text/plain\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await client.WriteAsync(Encoding.ASCII.GetBytes(head));
    }
}
