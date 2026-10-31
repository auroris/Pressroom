#requires -Version 7.0
param([string]$Image = 'pressroom:local')
$ErrorActionPreference = 'Stop'
if (![OperatingSystem]::IsLinux()) { throw 'Run this acceptance check on a Linux Docker host.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$id = [Guid]::NewGuid().ToString('N')
$name = "pressroom-smoke-$id"
$volume = "$name-state"
$work = Join-Path $root "artifacts/container-smoke/$id"
New-Item -ItemType Directory -Path $work -Force | Out-Null
$config = Join-Path $work settings.json
$secret = [Guid]::NewGuid().ToString('N')
@{
    Paths = @{ StateRoot = '/var/lib/pressroom' }
    Auth = @{ Mode = 'ApiKey'; ApiKeys = @{ smoke = @{ Secret = $secret } } }
    Jobs = @{ Enabled = $true }
} | ConvertTo-Json -Depth 6 | Set-Content $config
# This disposable test credential must be readable by the fixed container UID.
& chmod 644 $config
if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the test settings.' }
$profile = Join-Path $root deploy/pressroom-seccomp.json
$created = $false
try {
    & docker volume create $volume | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not create test state volume.' }
    $created = $true
    $startup = [Diagnostics.Stopwatch]::StartNew()
    & docker run --detach --name $name --read-only --cap-drop ALL --security-opt no-new-privileges `
        --security-opt "seccomp=$profile" --tmpfs /tmp:rw,nosuid,noexec,size=256m --shm-size=1g --stop-timeout 45 `
        --publish 127.0.0.1::8080 --mount "type=volume,source=$volume,target=/var/lib/pressroom" `
        --mount "type=bind,source=$config,target=/etc/pressroom/pressroom.settings.json,readonly" $Image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the runtime image.' }
    $binding = & docker port $name 8080/tcp
    if ($LASTEXITCODE -ne 0 -or $binding -notmatch '^127\.0\.0\.1:(\d+)$') { throw 'Missing loopback port binding.' }
    $base = "http://127.0.0.1:$($Matches[1])"
    $ready = $false
    while ($startup.Elapsed.TotalSeconds -lt 10) {
        try {
            $health = Invoke-WebRequest "$base/v1/health" -SkipHttpErrorCheck -TimeoutSec 1
            if ($health.StatusCode -eq 200) { $ready = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 100
    }
    if (!$ready -or $startup.Elapsed.TotalSeconds -gt 10) { throw 'Health did not become ready within 10 seconds.' }
    $startupMs = [int]$startup.Elapsed.TotalMilliseconds
    $headers = @{ 'X-Api-Key' = $secret }
    $badKey = Invoke-WebRequest "$base/v1/info" -Headers @{ 'X-Api-Key' = 'invalid-test-key' } -SkipHttpErrorCheck
    if ($badKey.StatusCode -ne 401) { throw 'Invalid key did not return 401.' }
    $body = @{ html = '<!doctype html><html><head><meta charset="utf-8"></head><body><p>English and français: été, cœur.</p><p style="break-before:page">Second page / deuxième page.</p></body></html>' } | ConvertTo-Json
    $pdf = Join-Path $work bilingual.pdf
    $response = Invoke-WebRequest "$base/v1/render" -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' `
        -Body ([Text.Encoding]::UTF8.GetBytes($body)) -OutFile $pdf -PassThru
    if ($response.StatusCode -ne 200 -or $response.Headers['X-Render-Pages'] -ne '2' -or
        [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($pdf), 0, 4) -ne '%PDF') { throw 'Two-page PDF check failed.' }
    $printers = Invoke-RestMethod "$base/v1/printers" -Headers $headers
    if (@($printers.printers).Count -ne 0) { throw 'Linux unexpectedly returned a printer.' }
    # Keep a real context active while delivering SIGTERM to the dotnet entrypoint.
    $slowBody = @{ html = '<p>Shutdown test</p>'; options = @{ readiness = @{ selector = '#never-created'; timeoutMs = 20000 }; timeoutMs = 30000 } } | ConvertTo-Json -Depth 6
    $job = Invoke-RestMethod "$base/v1/jobs" -Method Post -Headers $headers -ContentType 'application/json' -Body $slowBody
    $activeDeadline = [Diagnostics.Stopwatch]::StartNew()
    do {
        $job = Invoke-RestMethod "$base/v1/jobs/$($job.id)" -Headers $headers
        $info = Invoke-RestMethod "$base/v1/info" -Headers $headers
        if ($job.status -eq 'rendering' -and $info.active -gt 0) { break }
        Start-Sleep -Milliseconds 100
    } while ($activeDeadline.Elapsed.TotalSeconds -lt 10)
    if ($job.status -ne 'rendering' -or $info.active -lt 1) { throw 'The shutdown test did not start rendering.' }
    $stopping = [Diagnostics.Stopwatch]::StartNew()
    & docker stop --time 45 $name | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Container stop failed.' }
    $exitCode = & docker inspect $name --format '{{.State.ExitCode}}'
    if ($LASTEXITCODE -ne 0 -or $exitCode -ne '0' -or $stopping.Elapsed.TotalSeconds -gt 45) { throw 'Container did not stop gracefully within 45 seconds.' }
    @{ image = $Image; startupMs = $startupMs; shutdownMs = [int]$stopping.Elapsed.TotalMilliseconds; pageCount = 2; invalidKeyStatus = 401 } |
        ConvertTo-Json | Set-Content (Join-Path $work result.json)
    Write-Host "Container HTTP and shutdown acceptance passed. Evidence: $work"
} finally {
    & docker logs $name *> (Join-Path $work container.log)
    & docker rm --force $name | Out-Null
    if ($created) { & docker volume rm $volume | Out-Null }
}
