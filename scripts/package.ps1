<#
.SYNOPSIS
    Builds the same downloads as GitHub Actions (.github/workflows/build.yml), locally.

.DESCRIPTION
    Publishes self-contained builds for each system and packages them exactly as CI names them, plus every plugin on
    its own and all of them together. Everything lands in publish/dist (publish/ is ignored by git).

    Runs on Windows, macOS or Linux with PowerShell 7.4+ and the .NET 10 SDK; every target can be built from any of
    them. Linux tarballs and the macOS zip are written with Unix permissions, so the programs in them are executable
    when unpacked, even when built on Windows. On a Mac the .app is signed and zipped with codesign and ditto, as CI does.

.PARAMETER Targets
    Which systems to build: any of win-x64, linux-x64, linux-arm64, osx-arm64, osx-x64. Defaults to all of them.

.PARAMETER Version
    The version to stamp. Defaults to a nightly numbered as CI does: just past the latest v* tag, so 1.0.0 gives
    1.0.1-nightly.<RunNumber>.

.PARAMETER RunNumber
    The nightly's number (CI uses its run number). Defaults to 0, so a local build is never taken for a newer one.

.PARAMETER Release
    Name the files after the version (SchedularrTV-v1.2.0-...) as a tagged release does, rather than SchedularrTV-nightly-...

.PARAMETER NoPlugins
    Skip the plugin zips.

.PARAMETER PluginsOnly
    Only build the plugin zips.

.PARAMETER Test
    Run the tests first, as CI does.

.EXAMPLE
    ./scripts/package.ps1
    Everything, as the nightly.

.EXAMPLE
    ./scripts/package.ps1 -Targets win-x64 -NoPlugins
    Just the Windows download.

.EXAMPLE
    ./scripts/package.ps1 -Version 1.2.0 -Release
    Named and stamped as release v1.2.0.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64', 'linux-arm64', 'osx-arm64', 'osx-x64')]
    [string[]] $Targets = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-arm64', 'osx-x64'),
    [string] $Version,
    [int] $RunNumber = 0,
    [switch] $Release,
    [switch] $NoPlugins,
    [switch] $PluginsOnly,
    [switch] $Test
)

# Windows PowerShell 5.1 (what "powershell" and a plain .\script.ps1 run on Windows) can't write tarballs, so the script
# runs itself again in PowerShell 7 with the same arguments. Everything in this file must still parse in 5.1 for that.
if ($PSVersionTable.PSVersion -lt [version]'7.4') {
    $pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
    if (-not $pwsh) {
        Write-Host 'This script needs PowerShell 7.4 or newer, which installs beside Windows PowerShell:' -ForegroundColor Yellow
        Write-Host '    winget install --id Microsoft.PowerShell --source winget'
        Write-Host 'Then open a new terminal and run it again (it starts PowerShell 7 by itself).'
        exit 1
    }

    $arguments = foreach ($parameter in $PSBoundParameters.GetEnumerator()) {
        if ($parameter.Value -is [System.Management.Automation.SwitchParameter]) {
            if ($parameter.Value.IsPresent) { "-$($parameter.Key)" }
        }
        else {
            $values = @($parameter.Value | ForEach-Object { "'" + ([string]$_).Replace("'", "''") + "'" })
            "-$($parameter.Key) $($values -join ',')"
        }
    }
    $command = "& '$($PSCommandPath.Replace("'", "''"))' $($arguments -join ' '); exit `$LASTEXITCODE"
    & $pwsh.Source -NoProfile -ExecutionPolicy Bypass -Command $command
    exit $LASTEXITCODE
}

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.ZipFile, System.Formats.Tar

# Unix permissions (PowerShell has no octal literals): rwxr-xr-x, rw-r--r--, and the file-type bits zip keeps with them.
$ModeExecutable = 493   # 0755
$ModePlain = 420        # 0644
$TypeDirectory = 16384  # 0040000
$TypeFile = 32768       # 0100000

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$publish = Join-Path $root 'publish'
$dist = Join-Path $publish 'dist'
$env:DOTNET_NOLOGO = 'true'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = 'true'

function Invoke-Native {
    param([string] $Command, [string[]] $Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

# --- Version, as CI works it out --------------------------------------------------------------------------------

if (-not $Version) {
    $latest = [string](git -C $root tag --list 'v[0-9]*' --sort=-v:refname 2>$null | Select-Object -First 1)
    $latest = ($latest -replace '^v', '') -replace '-.*$', ''
    if (-not $latest) { $latest = '0.0.0' }
    $parts = $latest.Split('.') + @('0', '0', '0')
    $Version = "$([int]$parts[0]).$([int]$parts[1]).$([int]$parts[2] + 1)-nightly.$RunNumber"
}
$Version = $Version -replace '^v', ''
$prefix = if ($Release) { "SchedularrTV-v$Version" } else { 'SchedularrTV-nightly' }

Write-Host "Version $Version, files named $prefix-..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# --- Archives with Unix permissions -----------------------------------------------------------------------------

# Programs and native libraries (ELF or Mach-O) are made executable; everything else is read-write.
function Test-NativeBinary([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $bytes = [byte[]]::new(4)
        if ($stream.Read($bytes, 0, 4) -lt 4) { return $false }
        $magic = [BitConverter]::ToString($bytes)
        return $magic -in @('7F-45-4C-46', 'CF-FA-ED-FE', 'CE-FA-ED-FE', 'CA-FE-BA-BE', 'FE-ED-FA-CF', 'FE-ED-FA-CE')
    }
    finally { $stream.Dispose() }
}

function Get-UnixMode([string] $Path) {
    if (Test-Path -LiteralPath $Path -PathType Container) { return $ModeExecutable }
    if ($IsLinux -or $IsMacOS) {
        # Already executable here (a script, say): keep it so.
        $mode = [int](Get-Item -LiteralPath $Path).UnixFileMode
        if ($mode -band 73) { return $ModeExecutable }
    }
    if (Test-NativeBinary $Path) { return $ModeExecutable }
    return $ModePlain
}

# A zip entry's external attributes: the Unix type and mode in the high 16 bits (plus the MS-DOS folder flag).
function Get-ZipAttributes([int] $Mode, [switch] $Directory) {
    $value = if ($Directory) { [uint64]($TypeDirectory + $Mode) * 65536 + 16 } else { [uint64]($TypeFile + $Mode) * 65536 }
    return [BitConverter]::ToInt32([BitConverter]::GetBytes([uint32]$value), 0)
}

# Every file and folder under $Source, as (full path, archive name) with forward slashes, under $Base when given.
function Get-ArchiveItems([string] $Source, [string] $Base) {
    $sourceFull = (Resolve-Path $Source).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
    Get-ChildItem -LiteralPath $sourceFull -Recurse -Force | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($sourceFull, $_.FullName).Replace('\', '/')
        $name = if ($Base) { "$Base/$relative" } else { $relative }
        [pscustomobject]@{ Path = $_.FullName; Name = $name; IsDirectory = $_.PSIsContainer }
    }
}

function New-TarGz([string] $Source, [string] $Base, [string] $Destination) {
    Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
    $file = [IO.File]::Create($Destination)
    $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Optimal)
    $tar = [System.Formats.Tar.TarWriter]::new($gzip, [System.Formats.Tar.TarEntryFormat]::Pax, $false)
    try {
        $top = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::Directory, "$Base/")
        $top.Mode = [IO.UnixFileMode]$ModeExecutable
        $tar.WriteEntry($top)
        foreach ($item in Get-ArchiveItems $Source $Base) {
            if ($item.IsDirectory) {
                $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::Directory, "$($item.Name)/")
                $entry.Mode = [IO.UnixFileMode]$ModeExecutable
                $tar.WriteEntry($entry)
                continue
            }
            $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $item.Name)
            $entry.Mode = [IO.UnixFileMode](Get-UnixMode $item.Path)
            $entry.ModificationTime = [IO.File]::GetLastWriteTimeUtc($item.Path)
            $data = [IO.File]::OpenRead($item.Path)
            try {
                $entry.DataStream = $data
                $tar.WriteEntry($entry)
            }
            finally { $data.Dispose() }
        }
    }
    finally {
        $tar.Dispose(); $gzip.Dispose(); $file.Dispose()
    }
}

# A zip whose entries carry Unix permissions (marked as made on Unix), as zip -r or ditto on a Mac would write.
# .NET only marks entries as Unix when it runs on Unix, so on Windows the "made by" bytes are set afterwards.
function New-UnixZip([string] $Source, [string] $Base, [string] $Destination) {
    Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue
    $zip = [IO.Compression.ZipFile]::Open($Destination, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $items = @(Get-ArchiveItems $Source $Base)
        if ($Base) { $items = @([pscustomobject]@{ Path = (Resolve-Path $Source).Path; Name = $Base; IsDirectory = $true }) + $items }
        foreach ($item in $items) {
            if ($item.IsDirectory) {
                $entry = $zip.CreateEntry("$($item.Name)/")
                $entry.ExternalAttributes = Get-ZipAttributes $ModeExecutable -Directory
                continue
            }
            $entry = $zip.CreateEntry($item.Name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset][IO.File]::GetLastWriteTime($item.Path)
            $entry.ExternalAttributes = Get-ZipAttributes (Get-UnixMode $item.Path)
            $in = [IO.File]::OpenRead($item.Path)
            $out = $entry.Open()
            try { $in.CopyTo($out) }
            finally { $out.Dispose(); $in.Dispose() }
        }
    }
    finally { $zip.Dispose() }

    if (-not ($IsLinux -or $IsMacOS)) { Set-ZipMadeByUnix $Destination }
}

# Sets the host system in every central directory entry's "version made by" to Unix (3), so unzip and Archive Utility
# apply the permissions in the external attributes.
function Set-ZipMadeByUnix([string] $Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $end = -1
    for ($i = $bytes.Length - 22; $i -ge [Math]::Max(0, $bytes.Length - 65557); $i--) {
        if ([BitConverter]::ToUInt32($bytes, $i) -eq 0x06054b50) { $end = $i; break }
    }
    if ($end -lt 0) { throw "$Path has no end of central directory record." }

    $count = [BitConverter]::ToUInt16($bytes, $end + 10)
    $offset = [BitConverter]::ToUInt32($bytes, $end + 16)
    for ($n = 0; $n -lt $count; $n++) {
        if ([BitConverter]::ToUInt32($bytes, $offset) -ne 0x02014b50) { throw "$Path's central directory isn't where it says." }
        $bytes[$offset + 5] = 3
        $nameLength = [BitConverter]::ToUInt16($bytes, $offset + 28)
        $extraLength = [BitConverter]::ToUInt16($bytes, $offset + 30)
        $commentLength = [BitConverter]::ToUInt16($bytes, $offset + 32)
        $offset += 46 + $nameLength + $extraLength + $commentLength
    }
    [IO.File]::WriteAllBytes($Path, $bytes)
}

# --- Tests ------------------------------------------------------------------------------------------------------

if ($Test) {
    Write-Host 'Running the tests...' -ForegroundColor Cyan
    Invoke-Native dotnet @('test', (Join-Path $root 'tests/SchedularrTV.Core.Tests/SchedularrTV.Core.Tests.csproj'), '-c', 'Release')
}

# --- The app, per system ----------------------------------------------------------------------------------------

$built = [Collections.Generic.List[string]]::new()

if (-not $PluginsOnly) {
    foreach ($rid in $Targets) {
        Write-Host "`nPublishing $rid..." -ForegroundColor Cyan
        $stage = Join-Path $publish $rid
        $out = Join-Path $stage 'SchedularrTV'
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue

        # Raspberry Pi and other ARM boards: precompiled to start quickly, with the workstation GC's smaller footprint.
        $extra = @()
        if ($rid -eq 'linux-arm64') { $extra = @('-p:PublishReadyToRun=true', '-p:ServerGarbageCollection=false') }

        # The tray goes at the top of the folder and the server in "server/" beside it, where the tray looks.
        Invoke-Native dotnet (@('publish', (Join-Path $root 'src/SchedularrTV.Tray/SchedularrTV.Tray.csproj'), '-c', 'Release', '-r', $rid,
            '--self-contained', "-p:Version=$Version", '-o', $out) + $extra)
        Invoke-Native dotnet (@('publish', (Join-Path $root 'src/SchedularrTV.Service/SchedularrTV.Service.csproj'), '-c', 'Release', '-r', $rid,
            '--self-contained', "-p:Version=$Version", '-o', (Join-Path $out 'server')) + $extra)
        Get-ChildItem -LiteralPath $out, (Join-Path $out 'server') -Filter *.pdb -File | Remove-Item -Force

        switch -Wildcard ($rid) {
            'win-*' {
                $package = Join-Path $dist "$prefix-$rid.zip"
                Remove-Item -LiteralPath $package -Force -ErrorAction SilentlyContinue
                [IO.Compression.ZipFile]::CreateFromDirectory($out, $package, [IO.Compression.CompressionLevel]::Optimal, $true)
            }
            'linux-*' {
                $package = Join-Path $dist "$prefix-$rid.tar.gz"
                New-TarGz $out 'SchedularrTV' $package
            }
            'osx-*' {
                # A menu-bar-only .app (LSUIElement), so it shows no Dock icon.
                $app = Join-Path $stage 'SchedularrTV.app'
                $macOS = Join-Path $app 'Contents/MacOS'
                New-Item -ItemType Directory -Force -Path $macOS | Out-Null
                Copy-Item -Path (Join-Path $out '*') -Destination $macOS -Recurse -Force
                $short = $Version -replace '-.*$', ''
                @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>SchedularrTV</string>
  <key>CFBundleDisplayName</key><string>SchedularrTV</string>
  <key>CFBundleIdentifier</key><string>com.schedularrtv.app</string>
  <key>CFBundleExecutable</key><string>SchedularrTV</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>$Version</string>
  <key>CFBundleShortVersionString</key><string>$short</string>
  <key>LSUIElement</key><true/>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
"@ | Set-Content -LiteralPath (Join-Path $app 'Contents/Info.plist') -Encoding utf8NoBOM -NoNewline

                $package = Join-Path $dist "$prefix-$rid.zip"
                Remove-Item -LiteralPath $package -Force -ErrorAction SilentlyContinue
                if ($IsMacOS) {
                    # As CI: ad-hoc signatures so Apple silicon runs the two programs, zipped by ditto.
                    & codesign --force --sign - (Join-Path $macOS 'SchedularrTV')
                    & codesign --force --sign - (Join-Path $macOS 'server/SchedularrTV.Service')
                    Push-Location $stage
                    try { Invoke-Native ditto @('-c', '-k', '--sequesterRsrc', '--keepParent', 'SchedularrTV.app', $package) }
                    finally { Pop-Location }
                }
                else {
                    # codesign only exists on a Mac; the .NET SDK ad-hoc signs the programs it publishes itself.
                    New-UnixZip $app 'SchedularrTV.app' $package
                }
            }
        }

        $built.Add($package)
    }
}

# --- Plugins ----------------------------------------------------------------------------------------------------

# Framework-dependent, so one build serves every system: each zipped on its own, plus all of them together.
if (-not $NoPlugins) {
    Write-Host "`nPublishing the plugins..." -ForegroundColor Cyan
    $pluginsOut = Join-Path $publish 'plugins-out'
    Remove-Item -LiteralPath $pluginsOut -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $pluginsOut | Out-Null

    foreach ($folder in Get-ChildItem -LiteralPath (Join-Path $root 'plugins') -Directory | Sort-Object Name) {
        $project = Get-ChildItem -LiteralPath $folder.FullName -Filter *.csproj -File | Select-Object -First 1
        $manifest = Join-Path $folder.FullName 'plugin.json'
        if (-not $project -or -not (Test-Path -LiteralPath $manifest)) { continue }

        $id = (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json).id
        $out = Join-Path $pluginsOut $id
        Write-Host "  $id"
        Invoke-Native dotnet @('publish', $project.FullName, '-c', 'Release', "-p:Version=$Version", '-o', $out)
        Get-ChildItem -LiteralPath $out -Filter *.pdb -File | Remove-Item -Force

        # Stamp the release's version, so the Plugins page shows which release a plugin came from.
        $published = Join-Path $out 'plugin.json'
        $json = Get-Content -LiteralPath $published -Raw | ConvertFrom-Json -AsHashtable
        $json['version'] = $Version
        $json | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath $published -Encoding utf8NoBOM

        $package = Join-Path $dist "$prefix-plugin-$id.zip"
        New-UnixZip $out $id $package
        $built.Add($package)
    }

    $all = Join-Path $dist "$prefix-all-plugins.zip"
    New-UnixZip $pluginsOut $null $all
    $built.Add($all)
}

Write-Host "`nDone. In $dist`:" -ForegroundColor Green
foreach ($file in $built) {
    $size = (Get-Item -LiteralPath $file).Length / 1MB
    Write-Host ("  {0,-60} {1,7:N1} MB" -f (Split-Path $file -Leaf), $size)
}
