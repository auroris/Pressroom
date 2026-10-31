#requires -Version 7.0
param([string]$Image = 'pressroom:windows-local', [switch]$TestNetworkIsolation)
$ErrorActionPreference = 'Stop'
if (![OperatingSystem]::IsWindows()) { throw 'Run on a Windows Docker host.' }
$docker = (Get-Command docker -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (!$docker) { $docker = 'C:\Program Files\Docker\docker.exe' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$id = [Guid]::NewGuid().ToString('N')
$name = "pressroom-test-$id"
$work = Join-Path $root "artifacts/windows-container-smoke/$id"
$configDirectory = Join-Path $work config
New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null
$secret = [Guid]::NewGuid().ToString('N')
@{ Paths=@{StateRoot='C:\state'}; Auth=@{Mode='ApiKey';ApiKeys=@{test=@{Secret=$secret}}}; Jobs=@{Enabled=$true} } |
    ConvertTo-Json -Depth 8 | Set-Content (Join-Path $configDirectory pressroom.settings.json)
$portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$portProbe.Start(); $port=$portProbe.LocalEndpoint.Port; $portProbe.Stop()
$base="http://127.0.0.1:$port"
$headers=@{'X-Api-Key'=$secret}
$firewallRule="Pressroom-test-$id"
function Wait-Ready {
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do {
        try {
            if ((Invoke-WebRequest "$base/v1/health" -SkipHttpErrorCheck -TimeoutSec 1).StatusCode -eq 200) { return $watch.ElapsedMilliseconds }
        } catch { }
        Start-Sleep -Milliseconds 100
    } while ($watch.Elapsed.TotalSeconds -lt 30)
    throw 'Windows container did not become ready within 30 seconds.'
}
try {
    $startup=[Diagnostics.Stopwatch]::StartNew()
    & (Join-Path $PSScriptRoot Start-WindowsContainer.ps1) -ConfigurationDirectory $configDirectory -Image $Image -Name $name -Port $port -BlockOutbound:$TestNetworkIsolation | Out-Null
    Wait-Ready | Out-Null
    $startupMs=$startup.ElapsedMilliseconds
    $inspection=(& $docker inspect $name | ConvertFrom-Json)[0]
    if (!$inspection.HostConfig.AutoRemove -or $inspection.Config.User -ne 'ContainerUser' -or
        @($inspection.Mounts | Where-Object RW).Count -ne 0) { throw 'Unexpected container identity or writable host mount.' }
    if ((Invoke-WebRequest "$base/v1/info" -Headers @{'X-Api-Key'='invalid'} -SkipHttpErrorCheck).StatusCode -ne 401) { throw 'Invalid key did not return 401.' }
    $body=@{html='<html><meta charset="utf-8"><p>English / français: été, cœur.</p><p style="break-before:page">Second page / deuxième page.</p></html>'} | ConvertTo-Json
    $pdf=Join-Path $work bilingual.pdf
    $response=Invoke-WebRequest "$base/v1/render" -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -OutFile $pdf -PassThru
    if ($response.Headers['X-Render-Pages'] -ne '2' -or [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($pdf),0,4) -ne '%PDF') { throw 'Bilingual PDF check failed.' }
    if (@((Invoke-RestMethod "$base/v1/printers" -Headers $headers).printers).Count -ne 0) { throw 'The container must be PDF-only.' }
    if ($TestNetworkIsolation) {
        # Use the unrestricted NAT gateway for the positive control. The isolated
        # network's own host interface also has an inbound API-only ACL.
        $network=(& $docker network inspect nat | ConvertFrom-Json)[0]
        $gateway=$network.IPAM.Config[0].Gateway
        $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Any,0)
        $listener.Start()
        try {
            $canaryPort=$listener.LocalEndpoint.Port
            New-NetFirewallRule -Name $firewallRule -DisplayName $firewallRule -Direction Inbound -Action Allow -Protocol TCP -LocalPort $canaryPort | Out-Null
            $probe='$ErrorActionPreference="Stop";$c=[Net.Sockets.TcpClient]::new();if(!$c.ConnectAsync("'+$gateway+'",'+$canaryPort+').Wait(3000)-or !$c.Connected){exit 2};$c.Dispose()'
            $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($probe))
            & $docker run --rm --network nat --entrypoint powershell $Image -NoProfile -EncodedCommand $encoded | Out-Null
            if ($LASTEXITCODE -ne 0 -or !$listener.Pending()) { throw 'The unrestricted canary control did not connect.' }
            $accepted=$listener.AcceptTcpClient(); $accepted.Dispose()
            & $docker exec $name powershell -NoProfile -EncodedCommand $encoded *> $null
            if ($LASTEXITCODE -eq 0 -or $listener.Pending()) { throw 'The isolated container reached the host canary.' }
        } finally { $listener.Stop(); Remove-NetFirewallRule -Name $firewallRule -ErrorAction SilentlyContinue }
        foreach ($target in @('http://1.1.1.1','https://example.com')) {
            & $docker exec $name curl.exe --noproxy '*' --connect-timeout 2 --max-time 3 -fsS $target *> $null
            if ($LASTEXITCODE -eq 0) { throw "Unexpected outbound success: $target" }
        }
        # The full Windows base has OS listeners; the optional ACL must close them.
        $address=@($inspection.NetworkSettings.Networks.PSObject.Properties)[0].Value.IPAddress
        if (!$address) { throw 'Missing container address for the inbound policy test.' }
        foreach ($blockedPort in @(135,5985)) {
            $client=[Net.Sockets.TcpClient]::new()
            try {
                try { $null=$client.ConnectAsync($address,$blockedPort).Wait(1000) } catch { }
                if ($client.Connected) { throw "Unexpected reachable Windows service port $blockedPort." }
            } finally { $client.Dispose() }
        }
    }
    $slow=@{html='<p>Shutdown test</p>';options=@{readiness=@{selector='#never';timeoutMs=20000};timeoutMs=30000}} | ConvertTo-Json -Depth 6
    $job=Invoke-RestMethod "$base/v1/jobs" -Method Post -Headers $headers -ContentType application/json -Body $slow
    $active=[Diagnostics.Stopwatch]::StartNew()
    do {
        $job=Invoke-RestMethod "$base/v1/jobs/$($job.id)" -Headers $headers
        if ($job.status -eq 'rendering') { break }
        Start-Sleep -Milliseconds 100
    } while ($active.Elapsed.TotalSeconds -lt 15)
    if ($job.status -ne 'rendering') { throw 'The shutdown job did not begin rendering.' }
    $exitPath=Join-Path $work exit.txt
    $waiter=Start-Process -FilePath $docker -ArgumentList @('wait',$name) -WindowStyle Hidden -PassThru -RedirectStandardOutput $exitPath -RedirectStandardError (Join-Path $work wait-error.txt)
    & $docker logs $name *> (Join-Path $work container.log)
    $shutdown=[Diagnostics.Stopwatch]::StartNew()
    & $docker stop --time 45 $name | Out-Null
    if ($LASTEXITCODE -ne 0 -or !$waiter.WaitForExit(5000) -or (Get-Content $exitPath -Raw).Trim() -ne '0' -or $shutdown.Elapsed.TotalSeconds -gt 45) { throw 'Windows shutdown did not complete cleanly within 45 seconds.' }
    $shutdownMs=$shutdown.ElapsedMilliseconds
    & $docker inspect $name *> $null
    if ($LASTEXITCODE -eq 0) { throw 'Stopped container and writable layer were retained.' }
    & (Join-Path $PSScriptRoot Start-WindowsContainer.ps1) -ConfigurationDirectory $configDirectory -Image $Image -Name $name -Port $port -BlockOutbound:$TestNetworkIsolation | Out-Null
    Wait-Ready | Out-Null
    if ((Invoke-WebRequest "$base/v1/jobs/$($job.id)" -Headers $headers -SkipHttpErrorCheck).StatusCode -ne 404) { throw 'Temporary state survived container replacement.' }
    @{image=$Image;startupMs=$startupMs;shutdownMs=$shutdownMs;pageCount=2;invalidKeyStatus=401;ephemeralState=$true;outboundIsolationTested=[bool]$TestNetworkIsolation} |
        ConvertTo-Json | Set-Content (Join-Path $work result.json)
    Write-Host "Windows container acceptance passed. Evidence: $work"
} finally {
    & $docker logs $name *> (Join-Path $work final-container.log)
    & $docker rm --force $name *> $null
    Remove-NetFirewallRule -Name $firewallRule -ErrorAction SilentlyContinue
}
