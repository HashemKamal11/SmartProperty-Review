[CmdletBinding()]
param(
    [switch]$Execute,
    [ValidateSet('Migrations', 'Marketplace')]
    [string]$Batch = 'Marketplace',
    [string]$ExpectedCommit,
    [string]$DotnetPath,
    [string]$PackagesPath,
    [switch]$AllowApprovedPreCommitChanges
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')).TrimEnd('\','/')
$image = 'sha256:67f41722b7a8cbdb868a44a4995c846eddfdc2973bccb291ce937dce88ad5675'
$dataDirectory = '/var/lib/postgresql/data'
$utf8 = [Text.UTF8Encoding]::new($false)

function Require {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw [InvalidOperationException]::new($Message)
    }
}

function New-Secret {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }
    return ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
}

function Invoke-Native {
    param(
        [string]$Executable,
        [string[]]$Arguments,
        [int]$TimeoutMilliseconds = 30000
    )

    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $quoted = foreach ($argument in $Arguments) {
        Require (-not $argument.Contains('"')) 'Unsupported native argument quote.'
        '"' + $argument + '"'
    }
    $start.Arguments = $quoted -join ' '
    $start.WorkingDirectory = $root
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        Require ($process.Start()) 'Native process failed to start.'
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()

        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            # Terminate only this runner-created client process, never a
            # container or an unrelated process. pg_isready also has its own
            # two-second server-side timeout.
            try {
                $process.Kill()
                $null = $process.WaitForExit(5000)
            }
            catch {
                # The operation remains failed regardless of termination.
            }
            return [pscustomobject]@{
                Code = $null
                TimedOut = $true
                Out = ''
                Err = ''
            }
        }

        return [pscustomobject]@{
            Code = $process.ExitCode
            TimedOut = $false
            Out = $stdout.GetAwaiter().GetResult()
            Err = $stderr.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Docker-Result {
    param(
        [string[]]$Arguments,
        [int]$TimeoutMilliseconds = 30000
    )
    return Invoke-Native -Executable 'docker' `
        -Arguments (@('--context', 'desktop-linux') + $Arguments) `
        -TimeoutMilliseconds $TimeoutMilliseconds
}

function Docker-Read {
    param(
        [string[]]$Arguments,
        [int]$TimeoutMilliseconds = 30000
    )
    $result = Docker-Result -Arguments $Arguments `
        -TimeoutMilliseconds $TimeoutMilliseconds
    return Confirm-ProbeResult $result
}

function Confirm-ProbeResult {
    param($Result)
    Require (-not $Result.TimedOut -and $Result.Code -eq 0) `
        'Docker operation failed or timed out; raw output withheld.'
    return $Result.Out
}

function Verify-Container {
    param(
        $Metadata,
        [string]$Id,
        [string]$Run,
        [string]$Name,
        [string]$Token,
        [string]$Password,
        [int]$ExpectedPort = 0,
        [switch]$ForCleanup
    )

    # Inspection includes secrets. Never propagate raw metadata or parser
    # exceptions to logs.
    try {
        Require ($Id -cmatch '\A[0-9a-f]{64}\z') 'Invalid full container ID.'
        Require ($Run -cmatch '\A[0-9a-f]{32}\z') 'Invalid run identity.'
        Require ($Name -ceq ('sp-marketplace-pg-' + $Run)) 'Invalid run name.'
        Require (
            $Token -cmatch '\A[0-9a-f]{64}\z' -and
            $Password -cmatch '\A[0-9a-f]{64}\z' -and
            $Token -cne $Password
        ) 'Invalid independent credentials.'

        Require (
            $Metadata.Id -ceq $Id -and
            $Metadata.Image -ceq $image -and
            $Metadata.Name -ceq ('/' + $Name)
        ) 'Container identity mismatch.'
        Require (
            $Metadata.Config.Labels.'smartproperty.validation.run' -ceq $Run
        ) 'Ownership label mismatch.'
        Require ($Metadata.HostConfig.Privileged -eq $false) 'Privileged container rejected.'
        Require ($Metadata.State.Running -is [bool]) 'Unknown running state.'

        if (-not $ForCleanup) {
            Require ($Metadata.State.Running -eq $true) 'Container is not running.'
        }

        $expectedEnvironment = @{
            PGDATA = $dataDirectory
            SP_MARKETPLACE_PG_RUN_TOKEN = $Token
            POSTGRES_PASSWORD = $Password
            POSTGRES_USER = 'smartproperty_test'
            POSTGRES_DB = 'smartproperty_admin'
        }
        foreach ($key in $expectedEnvironment.Keys) {
            $values = @($Metadata.Config.Env | Where-Object {
                $_.StartsWith($key + '=', [StringComparison]::Ordinal)
            })
            Require (
                $values.Count -eq 1 -and
                $values[0] -ceq ($key + '=' + $expectedEnvironment[$key])
            ) 'Container environment mismatch.'
        }

        # Cleanup checks ownership and independent credentials, not readiness.
        if ($ForCleanup) { return $ExpectedPort }

        # Metadata rejects contradictions; independent probes establish storage.
        $dataMounts = 0
        foreach ($mount in @($Metadata.Mounts)) {
            Require ($mount.Type -ceq 'tmpfs') 'Persistent or unknown mount rejected.'
            Require (
                $mount.Destination -ceq $dataDirectory
            ) 'Nested data mount rejected.'
            if ($mount.Destination -ceq $dataDirectory) {
                Require ($mount.RW -eq $true) 'Read-only PGDATA rejected.'
                $dataMounts++
            }
        }
        Require ($dataMounts -le 1) 'Duplicate data mount.'
        Require (
            $null -eq $Metadata.HostConfig.Binds -or
            @($Metadata.HostConfig.Binds).Count -eq 0
        ) 'Bind mounts rejected.'
        $tmpfs = @($Metadata.HostConfig.Tmpfs.PSObject.Properties)
        Require ($tmpfs.Count -eq 1 -and $tmpfs[0].Name -ceq $dataDirectory -and
            $tmpfs[0].Value -ceq 'rw') 'Unexpected tmpfs configuration.'
        $requests = $Metadata.HostConfig.PSObject.Properties['Mounts']
        Require ($null -eq $requests -or $null -eq $requests.Value -or
            @($requests.Value).Count -eq 0) 'Unexpected mount request.'

        # Require exactly one configured publication, on loopback.
        $configuredPorts = @($Metadata.HostConfig.PortBindings.PSObject.Properties)
        Require (
            $configuredPorts.Count -eq 1 -and
            $configuredPorts[0].Name -ceq '5432/tcp'
        ) 'Unexpected configured ports.'
        $configured = @($Metadata.HostConfig.PortBindings.'5432/tcp')
        Require (
            $configured.Count -eq 1 -and
            $configured[0].HostIp -ceq '127.0.0.1'
        ) 'Unsafe configured endpoint.'

        $port = 0
        if ($Metadata.State.Running) {
            $actualPorts = @($Metadata.NetworkSettings.Ports.PSObject.Properties)
            Require (
                $actualPorts.Count -eq 1 -and
                $actualPorts[0].Name -ceq '5432/tcp'
            ) 'Unexpected effective ports.'
            $actual = @($Metadata.NetworkSettings.Ports.'5432/tcp')
            Require (
                $actual.Count -eq 1 -and
                $actual[0].HostIp -ceq '127.0.0.1'
            ) 'Non-loopback effective endpoint.'
            Require (
                [int]::TryParse([string]$actual[0].HostPort, [ref]$port)
            ) 'Invalid effective port.'

            # Docker may retain the original empty dynamic-port request.
            $configuredPort = [string]$configured[0].HostPort
            Require (
                $configuredPort -eq '' -or $configuredPort -ceq [string]$port
            ) 'Configured/effective endpoint mismatch.'
        }
        else {
            # Stopped cleanup is allowed only when the assigned endpoint can
            # still be established exactly. Do not guess an empty dynamic port.
            Require ($ForCleanup -and $ExpectedPort -gt 0) `
                'Stopped cleanup requires a previously verified endpoint.'
            Require (
                [int]::TryParse([string]$configured[0].HostPort, [ref]$port)
            ) 'Stopped container endpoint cannot be established.'
        }
        Require (
            $port -gt 0 -and $port -le 65535 -and
            ($ExpectedPort -eq 0 -or $port -eq $ExpectedPort)
        ) 'Endpoint mismatch.'
        return $port
    }
    catch {
        throw [InvalidOperationException]::new(
            'Container identity, ownership, storage, state or endpoint verification failed.'
        )
    }
}

function Verify-CreationIdentity {
    param($Metadata,[string]$Id,[string]$Run,[string]$Name,[string]$SavedId,
        [string[]]$SavedCommand,[string[]]$ExpectedCommand,[string]$Owners)
    Require ($Id -cmatch '\A[0-9a-f]{64}\z' -and $Metadata.Id -ceq $Id -and
        $Run -cmatch '\A[0-9a-f]{32}\z' -and $Name -ceq ('sp-marketplace-pg-'+$Run) -and
        $Metadata.Name -ceq ('/'+$Name) -and $Metadata.Image -ceq $image -and
        $Metadata.Config.Labels.'smartproperty.validation.run' -ceq $Run -and
        $SavedId -ceq $Id -and $Owners.Trim() -ceq $Id) 'Creation identity mismatch.'
    Require ($SavedCommand.Count -eq $ExpectedCommand.Count -and $ExpectedCommand.Count -gt 0) 'Creation command mismatch.'
    for ($i=0; $i -lt $ExpectedCommand.Count; $i++) {
        Require ($SavedCommand[$i] -ceq $ExpectedCommand[$i]) 'Creation command mismatch.'
    }
    Require ($Metadata.Created -is [string] -and $Metadata.Created -cmatch
        '\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})?Z\z') 'Invalid creation timestamp.'
}

function Verify-OriginalCreation {
    param([string]$Actual,[string]$Saved,[string]$Original)
    Require (-not [string]::IsNullOrWhiteSpace($Original) -and
        $Saved -ceq $Original -and $Actual -ceq $Original) 'Original creation evidence mismatch.'
}

function Read-OwnedMetadata {
    param([int]$TimeoutMilliseconds=10000)
    try {
        $raw = Docker-Read -Arguments @('inspect','--type','container',$script:containerId) -TimeoutMilliseconds $TimeoutMilliseconds
        $parsed = ConvertFrom-Json -InputObject $raw
        $items = @($parsed)
        Require ($items.Count -eq 1) 'Expected one container.'
        $saved = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $area 'create-command.json')))
        Require ($saved -is [Array]) 'Invalid saved creation command.'
        $owners = Docker-Read -Arguments @('ps','-a','--no-trunc','--filter',('label=smartproperty.validation.run='+$run),'--format','{{.ID}}') -TimeoutMilliseconds $TimeoutMilliseconds
        Verify-CreationIdentity -Metadata $items[0] -Id $script:containerId -Run $run -Name $name `
            -SavedId ([IO.File]::ReadAllText((Join-Path $area 'created-container-id.txt'))) `
            -SavedCommand @($saved) -ExpectedCommand (@('docker','--context','desktop-linux')+$create) -Owners $owners
        return $items[0]
    }
    catch { throw [InvalidOperationException]::new('Creation ownership inspection failed.') }
}

function Capture-CreationEvidence {
    $metadata = Read-OwnedMetadata
    $path = Join-Path $area 'created-timestamp.txt'
    $bytes = $utf8.GetBytes($metadata.Created)
    $stream = [IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    Require ([IO.File]::ReadAllText($path) -ceq $metadata.Created) 'Creation evidence read-back failed.'
    $script:created = $metadata.Created
}

function Initialize-OwnedContainer {
    param([scriptblock]$Capture,[scriptblock]$Verify)
    & $Capture
    return & $Verify
}

function Decode-MountPath {
    param([string]$Value)
    Require ($Value -cnotmatch '\\(?!040|011|012|134)') 'Malformed mount path escape.'
    return [regex]::Replace($Value,'\\(040|011|012|134)',{
        param($match)
        [string][char][Convert]::ToInt32($match.Groups[1].Value,8)
    })
}

function Read-MountEvidence {
    param([string]$Text)
    Require (-not [string]::IsNullOrWhiteSpace($Text)) 'Missing mountinfo.'
    $target = $null
    $ids = @{}
    foreach ($line in $Text.TrimEnd([char]10).Split([char]10)) {
        $fields = $line.Split([char]32)
        $separator = [Array]::IndexOf($fields,'-')
        Require ($fields.Count -ge 10 -and $separator -ge 6 -and $fields.Count -eq ($separator+4) -and
            @($fields | Where-Object { $_ -eq '' }).Count -eq 0) 'Malformed mountinfo.'
        Require ($fields[0] -cmatch '\A[1-9][0-9]*\z' -and $fields[1] -cmatch '\A[0-9]+\z' -and
            $fields[2] -cmatch '\A[0-9]+:[0-9]+\z' -and -not $ids.ContainsKey($fields[0])) 'Invalid mount identity.'
        $ids[$fields[0]] = $true
        $mountRoot = Decode-MountPath $fields[3]
        $path = Decode-MountPath $fields[4]
        Require ($mountRoot.StartsWith('/') -and $path.StartsWith('/')) 'Nonabsolute mount path.'
        Require (-not $path.StartsWith($dataDirectory+'/',[StringComparison]::Ordinal)) 'Nested data mount.'
        if ($path -cne $dataDirectory) { continue }
        $options = $fields[5].Split(',')
        $superOptions = $fields[$separator+3].Split(',')
        Require ($null -eq $target -and $mountRoot -ceq '/' -and $fields[$separator+1] -ceq 'tmpfs' -and
            @($options | Where-Object { $_ -ceq 'rw' }).Count -eq 1 -and $options -cnotcontains 'ro' -and
            @($superOptions | Where-Object { $_ -ceq 'rw' }).Count -eq 1 -and $superOptions -cnotcontains 'ro') 'Unsafe data mount.'
        $target = $line
    }
    Require ($null -ne $target) 'Missing effective data mount.'
    return $target
}

function Verify-Storage {
    param([string]$Canonical,[string]$FileSystem,[string]$InitMounts,[string]$SelfMounts)
    Require ($Canonical.TrimEnd([char]13,[char]10) -ceq $dataDirectory -and
        $FileSystem.TrimEnd([char]13,[char]10) -ceq 'tmpfs') 'Unexpected effective path or filesystem.'
    $first = Read-MountEvidence $InitMounts
    $second = Read-MountEvidence $SelfMounts
    Require ($first -ceq $second) 'Conflicting mount namespaces.'
}

function Verify-Stable {
    param($Before,$After)
    Require ($Before.Id -ceq $After.Id -and $Before.Created -ceq $After.Created -and
        -not [string]::IsNullOrWhiteSpace($Before.State.StartedAt) -and
        $Before.State.StartedAt -ceq $After.State.StartedAt -and
        $Before.State.Pid -gt 0 -and $Before.State.Pid -eq $After.State.Pid) 'Container changed during probes.'
}

function Inspect-Owned {
    param(
        [switch]$ForCleanup,
        [int]$TimeoutMilliseconds = 10000
    )
    try {
        $before = Read-OwnedMetadata -TimeoutMilliseconds $TimeoutMilliseconds
        Verify-OriginalCreation -Actual $before.Created -Original $script:created `
            -Saved ([IO.File]::ReadAllText((Join-Path $area 'created-timestamp.txt')))
        $verifiedPort = Verify-Container -Metadata $before `
            -Id $script:containerId -Run $run -Name $name `
            -Token $token -Password $password `
            -ExpectedPort $script:port -ForCleanup:$ForCleanup
        if ($ForCleanup) { return $verifiedPort }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $probe = {
            param([string[]]$Arguments)
            $left = $TimeoutMilliseconds - $watch.ElapsedMilliseconds
            Require ($left -gt 0) 'Storage verification timed out.'
            Docker-Read -Arguments $Arguments -TimeoutMilliseconds ([int]$left)
        }
        $canonical = & $probe -Arguments @('exec',$script:containerId,'readlink','-e',$dataDirectory)
        $init = & $probe -Arguments @('exec',$script:containerId,'cat','/proc/1/mountinfo')
        $self = & $probe -Arguments @('exec',$script:containerId,'cat','/proc/self/mountinfo')
        $filesystem = & $probe -Arguments @('exec',$script:containerId,'stat','-f','-c','%T',$dataDirectory)
        Verify-Storage $canonical $filesystem $init $self
        $afterRaw = & $probe -Arguments @('inspect','--type','container',$script:containerId)
        $afterParsed = ConvertFrom-Json -InputObject $afterRaw
        $afterItems = @($afterParsed)
        Require ($afterItems.Count -eq 1) 'Expected one post-probe container.'
        $null = Verify-Container $afterItems[0] $script:containerId $run $name $token $password $verifiedPort
        Verify-Stable $before $afterItems[0]
        return $verifiedPort
    }
    catch {
        throw [InvalidOperationException]::new('Owned-container inspection failed.')
    }
}

function Wait-PostgreSqlReady {
    param(
        [scriptblock]$Probe,
        [scriptblock]$ElapsedMilliseconds,
        [scriptblock]$Pause,
        [int]$TimeoutMilliseconds = 90000
    )
    while ($true) {
        $remaining = $TimeoutMilliseconds - [int64](& $ElapsedMilliseconds)
        Require ($remaining -gt 0) 'PostgreSQL readiness timed out.'
        $code = & $Probe $remaining
        $elapsed = [int64](& $ElapsedMilliseconds)
        Require ($elapsed -lt $TimeoutMilliseconds) 'PostgreSQL readiness timed out.'
        if ($code -eq 0) {
            return
        }
        # pg_isready: 1=rejecting connections; 2=no response.
        # Other results indicate an invocation/configuration failure.
        Require ($code -eq 1 -or $code -eq 2) 'PostgreSQL readiness probe failed.'
        & $Pause ([Math]::Min(500, $TimeoutMilliseconds - $elapsed))
    }
}

function Self-Test {
    $id = 'b' * 64
    $runId = 'a' * 32
    $containerName = 'sp-marketplace-pg-' + $runId
    $testToken = 'c' * 64
    $testPassword = 'd' * 64
    $sample = @{
        Id = $id
        Image = $image
        Name = '/' + $containerName
        Created = '2026-10-27T12:00:00Z'
        State = @{ Running = $true; StartedAt='2026-10-27T12:00:01Z'; Pid=123 }
        Config = @{
            Labels = @{ 'smartproperty.validation.run' = $runId }
            Env = @(
                ('PGDATA=' + $dataDirectory)
                ('SP_MARKETPLACE_PG_RUN_TOKEN=' + $testToken)
                ('POSTGRES_PASSWORD=' + $testPassword)
                'POSTGRES_USER=smartproperty_test'
                'POSTGRES_DB=smartproperty_admin'
            )
        }
        Mounts = @(@{ Type='tmpfs'; Destination=$dataDirectory; RW=$true })
        HostConfig = @{
            Privileged = $false
            Binds = $null
            Tmpfs = @{ '/var/lib/postgresql/data'='rw' }
            PortBindings = @{
                '5432/tcp' = @(@{ HostIp='127.0.0.1'; HostPort='54321' })
            }
        }
        NetworkSettings = @{
            Ports = @{
                '5432/tcp' = @(@{ HostIp='127.0.0.1'; HostPort='54321' })
            }
        }
    }
    $json = ConvertTo-Json -InputObject $sample -Depth 12
    $valid = ConvertFrom-Json -InputObject $json
    Require (
        (Verify-Container $valid $id $runId $containerName $testToken $testPassword 54321) -eq 54321
    ) 'Positive verification test failed.'
    $passed = 1

    $cases = @(
        'duplicate-mount','readonly','volume','bind','nested',
        'pgdata','missing-pgdata','duplicate-pgdata',
        'image','owner','token','password','public','port','id'
    )
    foreach ($case in $cases) {
        $bad = ConvertFrom-Json -InputObject $json
        switch ($case) {
            'duplicate-mount' { $bad.Mounts += $bad.Mounts[0] }
            'readonly' { $bad.Mounts[0].RW = $false }
            'volume' { $bad.Mounts[0].Type = 'volume' }
            'bind' { $bad.Mounts[0].Type = 'bind' }
            'nested' {
                $bad.Mounts += [pscustomobject]@{
                    Type='tmpfs'; Destination=($dataDirectory+'/nested'); RW=$true
                }
            }
            'pgdata' { $bad.Config.Env[0] = 'PGDATA=/other' }
            'missing-pgdata' { $bad.Config.Env = @($bad.Config.Env | Select-Object -Skip 1) }
            'duplicate-pgdata' { $bad.Config.Env += ('PGDATA='+$dataDirectory) }
            'image' { $bad.Image = 'sha256:' + ('e'*64) }
            'owner' { $bad.Config.Labels.'smartproperty.validation.run' = 'e'*32 }
            'token' { $bad.Config.Env[1] = 'SP_MARKETPLACE_PG_RUN_TOKEN=' + ('e'*64) }
            'password' { $bad.Config.Env[2] = 'POSTGRES_PASSWORD=' + ('e'*64) }
            'public' { $bad.NetworkSettings.Ports.'5432/tcp'[0].HostIp = '0.0.0.0' }
            'port' { $bad.NetworkSettings.Ports.'5432/tcp'[0].HostPort = '54322' }
            'id' { $bad.Id = 'e'*64 }
        }
        $rejected = $false
        try {
            $null = Verify-Container $bad $id $runId $containerName $testToken $testPassword 54321
        }
        catch [InvalidOperationException] { $rejected = $true }
        Require $rejected ('Negative verification test failed: ' + $case)
        $passed++
    }

    $stopped = ConvertFrom-Json -InputObject $json
    $stopped.State.Running = $false
    Require (
        (Verify-Container $stopped $id $runId $containerName $testToken $testPassword 54321 -ForCleanup) -eq 54321
    ) 'Verified stopped-container cleanup was rejected.'
    $passed++

    $rejected = $false
    try {
        $null = Verify-Container $stopped $id $runId $containerName $testToken $testPassword 54321
    }
    catch [InvalidOperationException] { $rejected = $true }
    Require $rejected 'Stopped container was accepted for execution.'
    $passed++

    $stopped.HostConfig.PortBindings.'5432/tcp'[0].HostPort = ''
    $stopped.Mounts = @()
    $stopped.NetworkSettings.Ports = $null
    $null = Verify-Container $stopped $id $runId $containerName $testToken $testPassword 54321 -ForCleanup
    $passed++

    $clock = @{ Milliseconds=0; Calls=0 }
    Wait-PostgreSqlReady `
        -Probe { param($remaining) $clock.Calls++; return 0 } `
        -ElapsedMilliseconds { $clock.Milliseconds } `
        -Pause { param($delay) $clock.Milliseconds += $delay } `
        -TimeoutMilliseconds 1000
    Require ($clock.Calls -eq 1) 'Ready probe test failed.'
    $passed++

    $clock = @{ Milliseconds=0; Calls=0 }
    $rejected = $false
    try {
        Wait-PostgreSqlReady `
            -Probe { param($remaining) $clock.Calls++; return 2 } `
            -ElapsedMilliseconds { $clock.Milliseconds } `
            -Pause { param($delay) $clock.Milliseconds += $delay } `
            -TimeoutMilliseconds 1000
    }
    catch [InvalidOperationException] {
        Require ($_.Exception.Message -eq 'PostgreSQL readiness timed out.') `
            'Unexpected readiness exception.'
        $rejected = $true
    }
    Require ($rejected -and $clock.Calls -eq 2) 'Bounded readiness timeout test failed.'
    $passed++
    $mountText = "20 1 0:21 / /var/lib/postgresql/data rw,nosuid - tmpfs tmpfs rw,size=65536k`n"
    Verify-Storage $dataDirectory 'tmpfs' $mountText $mountText
    $passed++
    $empty = ConvertFrom-Json -InputObject $json
    $empty.Mounts = @()
    $null = Verify-Container $empty $id $runId $containerName $testToken $testPassword 54321
    Verify-Storage $dataDirectory 'tmpfs' $mountText $mountText
    $passed++
    $escaped = '10 1 0:10 / /path\040with\134escape rw - tmpfs tmpfs rw' + "`n" + $mountText
    Verify-Storage $dataDirectory 'tmpfs' $escaped $escaped
    $passed++
    foreach ($case in @('missing','truncated','escape','overlay','ext4','readonly','readonly-super','nested','stacked','conflicting','symlink','stat')) {
        $first = $mountText; $second = $mountText; $canonical = $dataDirectory; $fs = 'tmpfs'
        switch ($case) {
            'missing' { $first = '' }
            'truncated' { $first = '20 1 0:21 /' }
            'escape' { $first = $first.Replace('/var','/bad\777/var') }
            'overlay' { $first = $first.Replace('- tmpfs','- overlay') }
            'ext4' { $first = $first.Replace('- tmpfs','- ext4') }
            'readonly' { $first = $first.Replace('rw,nosuid','ro,nosuid') }
            'readonly-super' { $first = $first.Replace('rw,size','ro,size') }
            'nested' { $first += "21 20 0:22 / /var/lib/postgresql/data/child rw - tmpfs tmpfs rw`n" }
            'stacked' { $first += $first.Replace('20 1','21 20') }
            'conflicting' { $second = $second.Replace('0:21','0:22') }
            'symlink' { $canonical = '/persistent' }
            'stat' { $fs = 'overlayfs' }
        }
        $rejected = $false
        try { Verify-Storage $canonical $fs $first $second }
        catch [InvalidOperationException] { $rejected = $true }
        Require $rejected ('Unsafe storage accepted: '+$case)
        $passed++
    }
    foreach ($case in @('id','created','started','pid')) {
        $changed = ConvertFrom-Json -InputObject $json
        switch ($case) {
            'id' { $changed.Id = 'e'*64 }
            'created' { $changed.Created = 'changed' }
            'started' { $changed.State.StartedAt = 'changed' }
            'pid' { $changed.State.Pid = 456 }
        }
        $rejected = $false
        try { Verify-Stable $valid $changed }
        catch [InvalidOperationException] { $rejected = $true }
        Require $rejected 'Changed container accepted.'
        $passed++
    }
    foreach ($result in @(@{TimedOut=$true;Code=$null;Out=''},@{TimedOut=$false;Code=1;Out=''})) {
        $rejected = $false
        try { $null = Confirm-ProbeResult $result }
        catch [InvalidOperationException] { $rejected = $true }
        Require $rejected 'Failed verification probe accepted.'
        $passed++
    }
    $command = @('docker','--context','desktop-linux','run','--pull=never','--name',$containerName,$image)
    Verify-CreationIdentity $valid $id $runId $containerName $id $command $command $id
    $passed++
    foreach ($case in @('saved-id','command','owners','timestamp','image')) {
        $changed = ConvertFrom-Json -InputObject $json
        $savedId = $id; $owners = $id; $savedCommand = @($command)
        switch ($case) {
            'saved-id' { $savedId = 'e'*64 }
            'command' { $savedCommand[0] = 'other' }
            'owners' { $owners = $id + "`n" + ('e'*64) }
            'timestamp' { $changed.Created = '' }
            'image' { $changed.Image = 'other' }
        }
        $rejected = $false
        try { Verify-CreationIdentity $changed $id $runId $containerName $savedId $savedCommand $command $owners }
        catch [InvalidOperationException] { $rejected = $true }
        Require $rejected ('Invalid creation evidence accepted: '+$case)
        $passed++
    }
    foreach ($case in @('missing','saved','actual')) {
        $original=$valid.Created; $savedCreated=$original; $actualCreated=$original
        switch ($case) {
            'missing' { $original='' }
            'saved' { $savedCreated='changed' }
            'actual' { $actualCreated='changed' }
        }
        $rejected=$false
        try { Verify-OriginalCreation $actualCreated $savedCreated $original }
        catch [InvalidOperationException] { $rejected=$true }
        Require $rejected 'Missing or changed original evidence accepted.'
        $passed++
    }
    $sequence = [Collections.Generic.List[string]]::new()
    $rejected=$false
    try {
        $null = Initialize-OwnedContainer -Capture { $sequence.Add('captured') } -Verify {
            Require ($sequence.Count -eq 1 -and $sequence[0] -eq 'captured') 'Wrong evidence order.'
            $sequence.Add('storage')
            throw [InvalidOperationException]::new('Synthetic storage failure.')
        }
    }
    catch [InvalidOperationException] { $rejected=$true }
    Require ($rejected -and ($sequence -join ',') -ceq 'captured,storage') 'Evidence was not preserved before failure.'
    $passed++
    $sequence.Clear()
    $rejected=$false
    try {
        $null = Initialize-OwnedContainer -Capture { throw [InvalidOperationException]::new('Capture failure.') } -Verify { $sequence.Add('forbidden') }
    }
    catch [InvalidOperationException] { $rejected=$true }
    Require ($rejected -and $sequence.Count -eq 0) 'Verification proceeded without evidence.'
    $passed++
    $rejected=$false
    try { $null=Verify-Container $stopped $id $runId $containerName ('e'*64) $testPassword 54321 -ForCleanup }
    catch [InvalidOperationException] { $rejected=$true }
    Require $rejected 'Cleanup accepted incorrect independent secret.'
    $passed++
    Verify-OriginalCreation $valid.Created $valid.Created $valid.Created
    $null=Verify-Container $stopped $id $runId $containerName $testToken $testPassword 0 -ForCleanup
    $passed++
    Require ($passed -eq 55) 'Unexpected synthetic test count.'
    Write-Output "SELF_TEST=$passed/55; no external processes or databases used."
}

# Default invocation is entirely synthetic. Runtime requires separate approval.
if (-not $Execute) {
    Self-Test
    return
}

function Read-Git {
    param([string[]]$Arguments)
    $result = Invoke-Native -Executable 'git' -Arguments (@('--no-optional-locks') + $Arguments)
    Require (-not $result.TimedOut -and $result.Code -eq 0) 'Git verification failed.'
    return $result.Out.TrimEnd([char]13,[char]10)
}

function Git-Paths {
    param([string[]]$Arguments)
    $text = Read-Git -Arguments $Arguments
    if (-not [string]::IsNullOrEmpty($text)) {
        foreach ($path in ($text -split '\r?\n')) {
            Require ($path -cmatch '\A[A-Za-z0-9_./-]+\z') 'Unsupported repository path.'
            $absolute = [IO.Path]::GetFullPath((Join-Path $root $path))
            Require ($absolute.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) 'Path escapes checkout.'
            $path
        }
    }
}

$approvedPreCommitHashes = @{
    'tests/SmartProperty.Persistence.IntegrationTests/Infrastructure/PostgreSqlFixture.cs' = 'CE972C774CCA70DCC6CE6DA4C799AF5827A97789FD4EFEC3C50B35AC21C9F408'
    'tests/SmartProperty.Persistence.IntegrationTests/Marketplace/MarketplaceConstraintTests.cs' = '1BB0A449F3EA56E194A9ED8CFDE9E1E4C08EC1825B92AE61DD67902C4393C674'
    'tests/SmartProperty.Persistence.IntegrationTests/Persistence/MigrationTests.cs' = 'EB5EEBB4FBBF08D247FDB85B6ABC213672D04C590C081FAC5DBC99AAC67FD4CC'
}

$packagePaths = @(
    'tests/SmartProperty.Persistence.IntegrationTests/Validation/Invoke-DisposablePostgreSql.ps1'
    'tests/SmartProperty.Persistence.IntegrationTests/Validation/README.md'
)

function Verify-Checkout {
    Require ($ExpectedCommit -cmatch '\A[0-9a-f]{40}\z') 'An explicit full expected commit is required.'
    Require ((Read-Git -Arguments @('rev-parse','--show-toplevel')) -ceq $root.Replace('\','/')) 'Wrong checkout.'
    Require ((Read-Git -Arguments @('rev-parse','HEAD')) -ceq $ExpectedCommit) 'Unexpected HEAD.'
    Require ((Read-Git -Arguments @('diff','--cached','--name-only')) -ceq '') 'Index must be empty.'
    Require ((Read-Git -Arguments @('ls-files','--unmerged')) -ceq '') 'Conflicted index.'
    $changed = @(Git-Paths -Arguments @('diff','--name-only',$ExpectedCommit,'--'))
    if ($AllowApprovedPreCommitChanges) {
        Require ($ExpectedCommit -ceq '575c120c9c3ea9619ef552f735ab94b238a9b7c0') 'Pre-commit exception requires the approved base.'
        Require ($changed.Count -eq 3) 'Pre-commit exception requires exactly three modifications.'
        foreach ($path in $changed) {
            Require ($approvedPreCommitHashes.ContainsKey($path)) 'Unapproved tracked modification.'
            Require ((Get-FileHash -LiteralPath (Join-Path $root $path) -Algorithm SHA256).Hash -ceq
                $approvedPreCommitHashes[$path]) 'Approved pre-commit source hash mismatch.'
        }
    }
    else {
        Require ($changed.Count -eq 0) 'Tracked checkout differs from expected commit.'
        foreach ($path in $packagePaths) {
            $null = Read-Git -Arguments @('ls-files','--error-unmatch','--',$path)
        }
    }
    # Hash the working files against committed blobs as well as using diff.
    # This prevents assume-unchanged/skip-worktree flags from hiding edits.
    $tree = Read-Git -Arguments @('ls-tree','-r',$ExpectedCommit)
    $treePaths = @()
    foreach ($entry in ($tree -split '\r?\n')) {
        Require ($entry -cmatch '\A100644 blob ([0-9a-f]{40})\t([A-Za-z0-9_./-]+)\z') 'Unsupported committed entry.'
        $blob = $Matches[1]
        $path = $Matches[2]
        $treePaths += $path
        if ($AllowApprovedPreCommitChanges -and $approvedPreCommitHashes.ContainsKey($path)) { continue }
        $actualBlob = Read-Git -Arguments @('hash-object',('--path='+$path),'--',$path)
        Require ($actualBlob -ceq $blob) 'Working file differs from committed content.'
    }
    $indexedPaths = @(Git-Paths -Arguments @('ls-files'))
    Require (@(Compare-Object $treePaths $indexedPaths).Count -eq 0) 'Index inventory differs from expected commit.'
    foreach ($path in @(Git-Paths -Arguments @('ls-files','--others','--exclude-standard'))) {
        # Preserve earlier local evidence without consuming it as a dependency.
        Require ($path.StartsWith('.postgres-validation/',[StringComparison]::Ordinal) -or
            ($AllowApprovedPreCommitChanges -and $packagePaths -ccontains $path)) 'Unexpected untracked file.'
    }
    $null = Read-Git -Arguments @('diff','--check')
}

Verify-Checkout
foreach ($path in @($DotnetPath,$PackagesPath)) {
    Require (-not [string]::IsNullOrWhiteSpace($path) -and [IO.Path]::IsPathRooted($path) -and
        -not $path.StartsWith('\\') -and -not $path.StartsWith('//')) 'Explicit local SDK/package paths required.'
    Require (Test-Path -LiteralPath $path) 'Local prerequisite is missing.'
    Require (((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Reparse-point prerequisite rejected.'
}
Require (Test-Path -LiteralPath $DotnetPath -PathType Leaf) 'Dotnet executable missing.'
Require (Test-Path -LiteralPath $PackagesPath -PathType Container) 'Package cache missing.'
$DotnetPath = [IO.Path]::GetFullPath($DotnetPath)
$PackagesPath = [IO.Path]::GetFullPath($PackagesPath).TrimEnd('\','/')
$projectDirectory = Join-Path $root 'tests/SmartProperty.Persistence.IntegrationTests'
$assets = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText((Join-Path $projectDirectory 'obj/project.assets.json')))
$folders = @($assets.packageFolders.PSObject.Properties.Name)
Require ($folders.Count -eq 1 -and [IO.Path]::GetFullPath($folders[0]).TrimEnd('\','/') -eq $PackagesPath) 'Assets use a different package cache.'
Require (Test-Path -LiteralPath (Join-Path $projectDirectory 'bin/Release/net10.0/SmartProperty.Persistence.IntegrationTests.dll')) 'Build Release offline before runtime.'
$sourcePaths = @(@(Git-Paths -Arguments @('ls-files')) + $packagePaths | Sort-Object -Unique)
$sourceHashes = @{}
foreach ($path in $sourcePaths) {
    Require (((Get-Item -LiteralPath (Join-Path $root $path)).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Reparse-point source rejected.'
    $sourceHashes[$path] = (Get-FileHash -LiteralPath (Join-Path $root $path) -Algorithm SHA256).Hash
}
$evidenceRoot = Join-Path $root 'artifacts/postgresql-validation'
$null = Read-Git -Arguments @('check-ignore','--','artifacts/postgresql-validation/probe.txt')

$endpoint = (Docker-Read -Arguments @(
    'context','inspect','desktop-linux','--format','{{.Endpoints.docker.Host}}'
)).Trim()
Require ($endpoint -ceq 'npipe:////./pipe/dockerDesktopLinuxEngine') `
    'Docker context endpoint changed.'
$cached = (Docker-Read -Arguments @(
    'image','inspect',$image,'--format','{{.Id}}'
)).Trim()
Require ($cached -ceq $image) 'Exact approved image is not cached.'
$previous = Docker-Read -Arguments @('ps','-a','--filter','label=smartproperty.validation.run','--format','{{.ID}}')
Require ([string]::IsNullOrWhiteSpace($previous)) 'An earlier validation container remains; separate recovery required.'

$run = [Guid]::NewGuid().ToString('N')
$name = 'sp-marketplace-pg-' + $run
foreach ($filter in @(
    ('name=' + $name),
    ('label=smartproperty.validation.run=' + $run)
)) {
    $existing = Docker-Read -Arguments @('ps','-a','--filter',$filter,'--format','{{.ID}}')
    Require ([string]::IsNullOrWhiteSpace($existing)) `
        'Container name or ownership identity already exists; adoption forbidden.'
}

$token = New-Secret
$password = New-Secret
Require ($token -cne $password) 'Independent credential generation failed.'
$area = Join-Path $evidenceRoot ('runtime-' + $run)
Require (-not (Test-Path -LiteralPath $area)) 'Run directory already exists.'
[IO.Directory]::CreateDirectory($area) | Out-Null
[IO.File]::WriteAllText((Join-Path $area 'source-before.json'),($sourceHashes | ConvertTo-Json),$utf8)
[IO.File]::WriteAllText((Join-Path $area 'expected-commit.txt'),$ExpectedCommit,$utf8)

$script:containerId = $null
$script:created = $null
$script:port = 0
$creationAttempted = $false
$failure = $null
$cleanup = $false
$settings = @{
    POSTGRES_PASSWORD=$password
    SP_MARKETPLACE_PG_RUN_TOKEN=$token
    SP_MARKETPLACE_PG_PASSWORD=$password
    SP_MARKETPLACE_PG_MODE='external'
    SP_MARKETPLACE_PG_MANIFEST=(Join-Path $area 'manifest.json')
    DOTNET_CLI_HOME=(Join-Path $area 'cli')
    TEMP=(Join-Path $area 'temp')
    TMP=(Join-Path $area 'temp')
    NUGET_PACKAGES=$PackagesPath
    NUGET_HTTP_CACHE_PATH=(Join-Path $area 'http')
    NUGET_SCRATCH=(Join-Path $area 'scratch')
    DOTNET_CLI_TELEMETRY_OPTOUT='1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE='true'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
}
$saved = @{}
foreach ($key in $settings.Keys) {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}

try {
    foreach ($directory in @('cli','temp','http','scratch')) {
        [IO.Directory]::CreateDirectory((Join-Path $area $directory)) | Out-Null
    }
    foreach ($key in $settings.Keys) {
        [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Process')
    }

    $create = @(
        'run','--detach','--pull=never','--name',$name,
        '--label',('smartproperty.validation.run=' + $run),
        '--publish','127.0.0.1::5432',
        '--tmpfs','/var/lib/postgresql/data:rw',
        '--env','PGDATA=/var/lib/postgresql/data',
        '--env','POSTGRES_USER=smartproperty_test',
        '--env','POSTGRES_DB=smartproperty_admin',
        '--env','POSTGRES_PASSWORD',
        '--env','SP_MARKETPLACE_PG_RUN_TOKEN',
        $image
    )
    [IO.File]::WriteAllText(
        (Join-Path $area 'create-command.json'),
        (ConvertTo-Json -InputObject (@('docker','--context','desktop-linux') + $create)),
        $utf8
    )
    $creationAttempted = $true
    $script:containerId = (Docker-Read -Arguments $create).Trim()
    Require ($script:containerId -cmatch '\A[0-9a-f]{64}\z') `
        'Creation did not return a full ID; no adoption or guessed cleanup.'
    [IO.File]::WriteAllText(
        (Join-Path $area 'created-container-id.txt'), $script:containerId, $utf8
    )

    $script:port = Initialize-OwnedContainer -Capture { Capture-CreationEvidence } -Verify { Inspect-Owned }
    $manifest = @{
        RunId=$run
        ContainerId=$script:containerId
        Image=$image
        Name=$name
        Port=$script:port
    }
    [IO.File]::WriteAllText(
        $settings.SP_MARKETPLACE_PG_MANIFEST,
        (ConvertTo-Json -InputObject $manifest),
        $utf8
    )

    # Every probe first re-verifies the exact freshly created container.
    # pg_isready uses TCP loopback inside that container, never a host or
    # shared database endpoint. No password appears in its arguments.
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Wait-PostgreSqlReady `
        -ElapsedMilliseconds { $watch.ElapsedMilliseconds } `
        -Pause { param($delay) Start-Sleep -Milliseconds $delay } `
        -Probe {
            param($remaining)
            $null = Inspect-Owned -TimeoutMilliseconds ([int][Math]::Min(10000, $remaining))
            $left = 90000 - $watch.ElapsedMilliseconds
            Require ($left -gt 0) 'PostgreSQL readiness timed out.'
            $probe = Docker-Result -Arguments @(
                'exec', $script:containerId,
                'pg_isready', '-h', '127.0.0.1',
                '-p', '5432', '-U', 'smartproperty_test',
                '-d', 'smartproperty_admin', '-t', '2'
            ) -TimeoutMilliseconds ([int][Math]::Min(5000, $left))
            [IO.File]::AppendAllText(
                (Join-Path $area 'readiness.log'),
                ('ElapsedMs=' + $watch.ElapsedMilliseconds +
                 '; ExitCode=' + $probe.Code +
                 '; TimedOut=' + $probe.TimedOut + [Environment]::NewLine),
                $utf8
            )
            Require (-not $probe.TimedOut) 'Readiness client timed out.'
            return $probe.Code
        }
    $watch.Stop()
    $null = Inspect-Owned

    $classes = if ($Batch -eq 'Migrations') {
        @('SmartProperty.Persistence.IntegrationTests.Persistence.MigrationTests')
    }
    else {
        @(
            'SmartProperty.Persistence.IntegrationTests.Marketplace.MarketplaceConstraintTests'
            'SmartProperty.Persistence.IntegrationTests.Marketplace.ListingSubmissionPersistenceTests'
            'SmartProperty.Persistence.IntegrationTests.Marketplace.PublicListingReadRepositoryTests'
        )
    }
    $filter = (@($classes | ForEach-Object { 'FullyQualifiedName~' + $_ }) -join '|')
    $testArguments = @(
        'test',
        'tests/SmartProperty.Persistence.IntegrationTests/SmartProperty.Persistence.IntegrationTests.csproj',
        '--configuration','Release','--no-build','--no-restore',
        '--filter',$filter
    )
    [IO.File]::WriteAllText(
        (Join-Path $area 'test-command.json'),
        (ConvertTo-Json -InputObject $testArguments),
        $utf8
    )
    $result = Invoke-Native -Executable $DotnetPath `
        -Arguments $testArguments -TimeoutMilliseconds 900000

    # Hold raw streams in memory and redact before writing. Do not create
    # raw TRX or Docker-inspection logs that might contain credentials.
    [IO.File]::WriteAllText(
        (Join-Path $area 'test.stdout.log'),
        $result.Out.Replace($token,'[REDACTED]').Replace($password,'[REDACTED]'),
        $utf8
    )
    [IO.File]::WriteAllText(
        (Join-Path $area 'test.stderr.log'),
        $result.Err.Replace($token,'[REDACTED]').Replace($password,'[REDACTED]'),
        $utf8
    )
    $exitEvidence = if ($result.TimedOut) {
        'TIMEOUT; native exit code unavailable'
    }
    else {
        [string]$result.Code
    }
    [IO.File]::WriteAllText(
        (Join-Path $area 'test.exit-code.txt'), $exitEvidence, $utf8
    )
    Require (-not $result.TimedOut -and $result.Code -eq 0) `
        'Database test batch failed or timed out; no retry.'
}
catch {
    # Never persist arbitrary exception messages from Docker or database tools.
    $failure = 'Runtime validation failed; see sanitized evidence. No retry.'
}
finally {
    if ($null -ne $script:containerId -and
        $script:containerId -cmatch '\A[0-9a-f]{64}\z') {
        try {
            $null = Inspect-Owned -ForCleanup
            $removed = (Docker-Read -Arguments @(
                'rm','--force',$script:containerId
            )).Trim()
            Require ($removed -ceq $script:containerId) 'Unexpected removal result.'
            $remaining = Docker-Read -Arguments @(
                'ps','-a','--no-trunc','--filter',
                ('id=' + $script:containerId),'--format','{{.ID}}'
            )
            Require ([string]::IsNullOrWhiteSpace($remaining)) `
                'Exact owned container still exists.'
            $remainingLabel = Docker-Read -Arguments @('ps','-a','--no-trunc','--filter',
                ('label=smartproperty.validation.run='+$run),'--format','{{.ID}}')
            Require ([string]::IsNullOrWhiteSpace($remainingLabel)) 'Run label still exists.'
            $cleanup = $true
        }
        catch {
            [IO.File]::WriteAllText(
                (Join-Path $area 'cleanup-failure.txt'),
                'Ownership/creation evidence could not be reverified, or exact removal failed. No further cleanup attempted. Manual review required.',
                $utf8
            )
        }
    }
    elseif ($creationAttempted) {
        [IO.File]::WriteAllText(
            (Join-Path $area 'cleanup-failure.txt'),
            'Creation was attempted but no verified returned full ID is available. No container was adopted or guessed for cleanup. Manual review required.',
            $utf8
        )
    }
    foreach ($key in $settings.Keys) {
        [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process')
    }
    try {
        Verify-Checkout
        foreach ($path in $sourceHashes.Keys) {
            Require ((Get-FileHash -LiteralPath (Join-Path $root $path) -Algorithm SHA256).Hash -ceq
                $sourceHashes[$path]) 'Source integrity changed during runtime.'
        }
        [IO.File]::WriteAllText((Join-Path $area 'source-integrity.txt'),'PASS',$utf8)
    }
    catch {
        $failure = 'Post-run source/Git integrity verification failed.'
        [IO.File]::WriteAllText((Join-Path $area 'source-integrity.txt'),'FAIL',$utf8)
    }
    [IO.File]::WriteAllText(
        (Join-Path $area 'result.txt'),
        ('CleanupVerified=' + $cleanup + '; Failure=' + $failure),
        $utf8
    )
    $settings.Clear()
    $token = $null
    $password = $null
}

Require ($cleanup -and $null -eq $failure) `
    'Runtime validation or cleanup failed; review preserved evidence.'
Write-Output ('RUNTIME=PASS; CLEANUP=PASS; evidence=' + $area)
