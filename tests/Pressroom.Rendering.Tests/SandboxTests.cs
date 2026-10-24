using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Pressroom.Contracts;
using Pressroom.Rendering;
using UglyToad.PdfPig;
using Xunit;

namespace Pressroom.Rendering.Tests;

public sealed class SandboxTests(BrowserFixture fixture) : IClassFixture<BrowserFixture>
{
    private static string DataUrl(string mediaType, string content)
        => $"data:{mediaType};base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(content))}";

    private static string PdfText(byte[] bytes)
    {
        using var pdf = PdfDocument.Open(bytes);
        return string.Join(" ", pdf.GetPages().Select(page => page.Text));
    }

    [BrowserFact]
    public async Task DataImagesStylesScriptsAndNestedDocumentsStillRender()
    {
        var script = DataUrl("text/javascript", "window.embeddedScriptRan = true;");
        var style = DataUrl("text/css", "#styled { color: rgb(1, 2, 3); }");
        var frame = DataUrl("text/html", "<p>EMBEDDED_FRAME</p>");
        // A real PNG, so this proves image decoding as well as URL admission.
        const string png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";
        var result = await fixture.RenderAsync($$"""
            <!doctype html><html><head>
            <link rel="stylesheet" href="{{style}}"><script src="{{script}}"></script>
            </head><body><p id="styled">EMBEDDED_RESOURCES</p>
            <img id="embedded" width="40" height="40" src="{{png}}">
            <iframe src="{{frame}}"></iframe>
            <iframe srcdoc="<p>INLINE_FRAME</p>"></iframe>
            <script>
            document.getElementById('styled').append(' INLINE_SCRIPT');
            fetch('data:text/plain,EMBEDDED_FETCH').then(response => response.text()).then(text => {
                document.getElementById('styled').append(' ' + text); window.embeddedFetchFinished = true;
            });
            </script>
            </body></html>
            """, new()
            {
                DisplayHeaderFooter = false,
                Readiness = new()
                {
                    Function = "window.embeddedScriptRan === true && window.embeddedFetchFinished === true && document.querySelector('#embedded').naturalWidth === 1 && getComputedStyle(document.querySelector('#styled')).color === 'rgb(1, 2, 3)'"
                }
            });
        var text = PdfText(result.Pdf);
        Assert.Contains("EMBEDDED_RESOURCES", text);
        Assert.Contains("INLINE_SCRIPT", text);
        Assert.Contains("EMBEDDED_FETCH", text);
        Assert.Contains("EMBEDDED_FRAME", text);
        Assert.Contains("INLINE_FRAME", text);
        Assert.Equal(0, result.BlockedResources);
    }

    [BrowserFact]
    public async Task NetworkAttemptsFromMainDocumentAndNestedDocumentsNeverReachTcpListener()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var socket = origin.Replace("http:", "ws:", StringComparison.Ordinal);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var child = $$"""
            <p>NESTED_ATTEMPTS</p><img src="{{origin}}/nested-image">
            <script>
            try { fetch('{{origin}}/nested-fetch').catch(() => {}); } catch {}
            try { navigator.sendBeacon('{{origin}}/nested-beacon', 'probe'); } catch {}
            try { const ws = new WebSocket('{{socket}}/nested-socket'); ws.onerror = () => {}; } catch {}
            try { const popup = window.open('{{origin}}/nested-popup'); } catch {}
            </script>
            """;
        var rtcChild = $$"""
            <script>
            try {
                const rtc = new RTCPeerConnection({ iceServers: [{ urls: 'turn:127.0.0.1:{{port}}?transport=tcp', username: 'probe', credential: 'probe' }] });
                window.turnProbe = rtc; rtc.createDataChannel('probe');
                rtc.createOffer().then(offer => rtc.setLocalDescription(offer)).catch(() => {});
            } catch {}
            </script>
            """;
        var result = await fixture.RenderAsync($$"""
            <!doctype html><html><head>
            <link rel="preconnect" href="{{origin}}"><link rel="dns-prefetch" href="//127.0.0.1">
            <link rel="prefetch" href="{{origin}}/prefetch"><link rel="stylesheet" href="{{origin}}/stylesheet">
            <style>@import url('{{origin}}/import'); body { background-image: url('{{origin}}/background'); }
            @font-face { font-family:Probe; src:url('{{origin}}/font'); } #font { font-family:Probe; }</style>
            <script src="{{origin}}/script"></script>
            </head><body><p id="font">NETWORK_ATTEMPTS_FINISHED</p>
            <img src="{{origin}}/image"><iframe name="target" src="{{origin}}/frame"></iframe>
            <iframe src="{{DataUrl("text/html", child)}}"></iframe>
            <iframe src="{{DataUrl("text/html", rtcChild)}}"></iframe>
            <iframe srcdoc="{{WebUtility.HtmlEncode(child)}}"></iframe>
            <object data="{{origin}}/object"></object><embed src="{{origin}}/embed">
            <video poster="{{origin}}/poster" src="{{origin}}/video" preload="auto"></video>
            <form id="probeForm" method="post" target="target" action="{{origin}}/form"><input name="probe" value="value"></form>
            <script>
            try { fetch('{{origin}}/fetch').catch(() => {}); } catch {}
            try { const xhr = new XMLHttpRequest(); xhr.open('POST', '{{origin}}/xhr'); xhr.send('probe'); } catch {}
            try { navigator.sendBeacon('{{origin}}/beacon', 'probe'); } catch {}
            try { const ws = new WebSocket('{{socket}}/socket'); ws.onerror = () => {}; } catch {}
            try { const source = new EventSource('{{origin}}/events'); source.onerror = () => source.close(); } catch {}
            try { document.querySelector('#probeForm').submit(); } catch {}
            try { window.open('{{origin}}/popup'); } catch {}
            try { const popup = window.open(); if (popup) popup.document.write('<img src="{{origin}}/blank-popup">'); } catch {}
            try { new Worker('{{origin}}/worker'); } catch {}
            try { new SharedWorker('{{origin}}/shared-worker'); } catch {}
            try { navigator.serviceWorker?.register('{{origin}}/service-worker').catch(() => {}); } catch {}
            try {
                const rtc = new RTCPeerConnection({ iceServers: [{ urls: 'turn:127.0.0.1:{{port}}?transport=tcp', username: 'probe', credential: 'probe' }] });
                window.turnProbe = rtc; rtc.createDataChannel('probe');
                rtc.createOffer().then(offer => rtc.setLocalDescription(offer)).catch(() => {});
            } catch {}
            window.attemptsFinished = true;
            </script></body></html>
            """, new()
            {
                DisplayHeaderFooter = false,
                Readiness = new() { Function = "window.attemptsFinished === true", DelayMs = 1_000 }
            });
        Assert.False(listener.Pending(), "A document resource or connection reached the loopback TCP listener.");
        Assert.Contains("NETWORK_ATTEMPTS_FINISHED", PdfText(result.Pdf));
    }

    [BrowserFact]
    public async Task WebRtcAndWebTransportCannotSendUdpPackets()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        var child = $$"""
            <script>
            try {
                const rtc = new RTCPeerConnection({ iceServers: [{ urls: 'stun:127.0.0.1:{{port}}' }] });
                window.stunProbe = rtc; rtc.createDataChannel('probe');
                rtc.createOffer().then(offer => rtc.setLocalDescription(offer)).catch(() => {});
            } catch {}
            </script>
            """;
        var result = await fixture.RenderAsync($$"""
            <p>UDP_ATTEMPTS_FINISHED</p>
            <iframe src="{{DataUrl("text/html", child)}}"></iframe>
            <script>
            window.udpAttemptFinished = false;
            (async () => {
                try {
                    const connection = new RTCPeerConnection({ iceServers: [{ urls: 'stun:127.0.0.1:{{port}}' }] });
                    window.probeConnection = connection;
                    connection.createDataChannel('probe');
                    await connection.setLocalDescription(await connection.createOffer());
                } catch {}
                try {
                    const transport = new WebTransport('https://127.0.0.1:{{port}}/transport');
                    transport.ready.catch(() => {}); transport.closed.catch(() => {});
                } catch {}
                window.udpAttemptFinished = true;
            })();
            </script>
            """, new()
            {
                DisplayHeaderFooter = false,
                Readiness = new() { Function = "window.udpAttemptFinished === true", DelayMs = 1_500 }
            });
        Assert.Equal(0, listener.Available);
        Assert.Contains("UDP_ATTEMPTS_FINISHED", PdfText(result.Pdf));
    }

    [BrowserFact]
    public async Task BlobUrlsAreBlockedEvenWhenCreatedInsideTheDocument()
    {
        var result = await fixture.RenderAsync("""
            <p id="result">BLOB_PROBES_PENDING</p><script>
            window.blobProbesFinished = false;
            const probes = [];
            function resource(tag, type, content) {
                return new Promise(resolve => {
                    const node = document.createElement(tag);
                    node.onload = () => resolve('loaded');
                    node.onerror = () => resolve('blocked');
                    node.src = URL.createObjectURL(new Blob([content], { type }));
                    document.body.append(node);
                });
            }
            probes.push(resource('img', 'image/svg+xml', '<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><rect width="10" height="10"/></svg>'));
            probes.push(resource('script', 'text/javascript', 'window.blobScriptExecuted = true;'));
            Promise.all(probes).then(results => {
                document.querySelector('#result').textContent = results.every(value => value === 'blocked') && !window.blobScriptExecuted
                    ? 'BLOB_RESOURCES_BLOCKED' : 'BLOB_RESOURCES_LOADED';
                window.blobProbesFinished = true;
            });
            </script>
            """, new()
            {
                DisplayHeaderFooter = false,
                Readiness = new() { Function = "window.blobProbesFinished === true" }
            });
        Assert.Contains("BLOB_RESOURCES_BLOCKED", PdfText(result.Pdf));
        Assert.DoesNotContain("BLOB_RESOURCES_LOADED", PdfText(result.Pdf));
    }

    [BrowserFact]
    public async Task LocalFilesCannotBeReadByFetchFramesObjectsOrImages()
    {
        var marker = "LOCAL_FILE_SECRET_" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "pressroom-file-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var htmlPath = Path.Combine(directory, "secret.html");
        var svgPath = Path.Combine(directory, "secret.svg");
        try
        {
            await File.WriteAllTextAsync(htmlPath, $"<p>{marker}</p>");
            await File.WriteAllTextAsync(svgPath, $"<svg xmlns='http://www.w3.org/2000/svg' width='200' height='30'><text y='20'>{marker}</text></svg>");
            var file = new Uri(htmlPath).AbsoluteUri;
            var svg = new Uri(svgPath).AbsoluteUri;
            var child = $"<iframe src='{file}'></iframe><object data='{file}'></object>";
            var result = await fixture.RenderAsync($$"""
                <p id="status">FILE_PROBES_PENDING</p>
                <iframe src="{{file}}"></iframe><object data="{{file}}"></object><embed src="{{file}}">
                <iframe src="{{DataUrl("text/html", child)}}"></iframe>
                <iframe srcdoc="{{WebUtility.HtmlEncode(child)}}"></iframe>
                <script>
                window.fileProbesFinished = false;
                const imageProbe = new Promise(resolve => {
                    const image = new Image(); image.onload = () => resolve('loaded'); image.onerror = () => resolve('blocked');
                    image.src = {{JsonSerializer.Serialize(svg)}}; document.body.append(image);
                });
                const fetchProbe = fetch({{JsonSerializer.Serialize(file)}}).then(response => response.text()).catch(() => 'blocked');
                Promise.all([imageProbe, fetchProbe]).then(results => {
                    document.querySelector('#status').textContent = results.every(result => result === 'blocked')
                        ? 'LOCAL_FILES_BLOCKED' : 'LOCAL_FILES_LOADED ' + results.join(' ');
                    window.fileProbesFinished = true;
                });
                </script>
                """, new()
                {
                    DisplayHeaderFooter = false,
                    Readiness = new() { Function = "window.fileProbesFinished === true", DelayMs = 200 }
                });
            var text = PdfText(result.Pdf);
            Assert.Contains("LOCAL_FILES_BLOCKED", text);
            Assert.DoesNotContain("LOCAL_FILES_LOADED", text);
            Assert.DoesNotContain(marker, text);
        }
        finally
        {
            File.Delete(htmlPath);
            File.Delete(svgPath);
            Directory.Delete(directory);
        }
    }

    [BrowserFact]
    public async Task PrintTemplatesCannotFetchExternalResources()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var template = $$"""
            <style>@import url('{{origin}}/print-style'); .probe { background-image:url('{{origin}}/print-background'); }</style>
            <div class="probe" style="font-size:10px">PRINT_TEMPLATE
            <img src="{{origin}}/print-image"></div>
            """;
        RenderOutput? result = null;
        try
        {
            result = await fixture.RenderAsync("<p>PRINT_BODY</p>", new()
            {
                DisplayHeaderFooter = true,
                HeaderTemplate = template,
                FooterTemplate = template,
                Margins = new() { Top = "1in", Bottom = "1in", Left = "1in", Right = "1in" }
            });
        }
        catch (RenderDocumentException)
        {
            // Chromium can reject printing templates with blocked external assets.
            // Rejection is safe only if the attempted assets never reach the listener.
        }
        Assert.False(listener.Pending(), "A print header or footer reached the loopback TCP listener.");
        if (result is not null)
        {
            var text = PdfText(result.Pdf);
            Assert.Contains("PRINT_BODY", text);
            Assert.Contains("PRINT_TEMPLATE", text);
        }
        Assert.Equal(1, (await fixture.RenderAsync("<p>Template failure recovered</p>")).PageCount);
    }

    [BrowserFact]
    public async Task SrcdocWebRtcIsDeniedWithoutTrafficAndRendererRemainsUsable()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var tcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        var child = $$"""
            <script>
            try {
                const rtc = new RTCPeerConnection({ iceServers: [
                    { urls:'stun:127.0.0.1:{{udpPort}}' },
                    { urls:'turn:127.0.0.1:{{tcpPort}}?transport=tcp', username:'probe', credential:'probe' }
                ] });
                window.rtcProbe = rtc; rtc.createDataChannel('probe');
                rtc.createOffer().then(offer => rtc.setLocalDescription(offer)).catch(() => {});
            } catch {}
            </script>
            """;
        // The pinned Chromium terminates this srcdoc renderer when denying RTC.
        // Verify the failure remains contained and cannot grant network access.
        await Assert.ThrowsAsync<RenderDocumentException>(() => fixture.RenderAsync(
            $"<p>SRCDOC_PROBE</p><iframe srcdoc=\"{WebUtility.HtmlEncode(child)}\"></iframe>",
            new() { Readiness = new() { DelayMs = 1_500 } }));
        Assert.False(tcp.Pending(), "A srcdoc WebRTC TURN attempt reached the TCP listener.");
        Assert.Equal(0, udp.Available);
        Assert.Equal(1, (await fixture.RenderAsync("<p>RTC failure recovered</p>")).PageCount);
    }

    [BrowserFact]
    public async Task MainDocumentCannotReplaceItsPolicyWithABlobNavigation()
    {
        await Assert.ThrowsAsync<RenderDocumentException>(() => fixture.RenderAsync("""
            <p>ORIGINAL_DOCUMENT</p><script>
            location.href = URL.createObjectURL(new Blob(['<p>REPLACEMENT_DOCUMENT</p>'], { type:'text/html' }));
            </script>
            """, new() { Readiness = new() { DelayMs = 200 } }));
        Assert.Equal(1, (await fixture.RenderAsync("<p>Navigation rejection recovered</p>")).PageCount);
    }

    [BrowserFact]
    public async Task OpaqueDocumentOriginCannotUsePersistentBrowserStorageOrFileSystem()
    {
        var result = await fixture.RenderAsync("""
            <p id="status">STORAGE_PROBES_PENDING</p><script>
            window.storageProbesFinished = false;
            async function blocked(operation) {
                try { await operation(); return false; } catch { return true; }
            }
            Promise.all([
                blocked(() => localStorage.setItem('probe', 'value')),
                blocked(() => sessionStorage.setItem('probe', 'value')),
                blocked(() => new Promise((resolve, reject) => {
                    const request = indexedDB.open('probe'); request.onsuccess = () => { request.result.close(); resolve(); };
                    request.onerror = () => reject(request.error);
                })),
                blocked(() => caches.open('probe')),
                blocked(() => navigator.storage.getDirectory())
            ]).then(results => {
                document.querySelector('#status').textContent = results.every(Boolean)
                    ? 'PERSISTENT_STORAGE_BLOCKED' : 'PERSISTENT_STORAGE_ALLOWED';
                window.storageProbesFinished = true;
            });
            </script>
            """, new()
            {
                DisplayHeaderFooter = false,
                Readiness = new() { Function = "window.storageProbesFinished === true" }
            });
        Assert.Contains("PERSISTENT_STORAGE_BLOCKED", PdfText(result.Pdf));
        Assert.DoesNotContain("PERSISTENT_STORAGE_ALLOWED", PdfText(result.Pdf));
    }

    [BrowserFact]
    public async Task MainDocumentCannotNavigateToLocalFile()
    {
        var marker = "NAVIGATION_SECRET_" + Guid.NewGuid().ToString("N");
        var file = Path.Combine(Path.GetTempPath(), "pressroom-navigation-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            await File.WriteAllTextAsync(file, $"<h1>{marker}</h1>");
            var html = $$"""
                <p>ORIGINAL_DOCUMENT</p>
                <script>try { location.href = {{JsonSerializer.Serialize(new Uri(file).AbsoluteUri)}}; } catch {}</script>
                """;
            try
            {
                var result = await fixture.RenderAsync(html, new() { DisplayHeaderFooter = false, Readiness = new() { DelayMs = 200 } });
                Assert.DoesNotContain(marker, PdfText(result.Pdf));
            }
            catch (RenderDocumentException error)
            {
                // A rejected navigation may invalidate the print operation; it must
                // not return the file contents in a PDF or an error diagnostic.
                Assert.DoesNotContain(marker, error.ToString());
            }
        }
        finally
        {
            File.Delete(file);
        }
    }
}
