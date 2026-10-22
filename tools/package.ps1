# Builds Pebble.msix for the Microsoft Store.
# IdentityName / Publisher / PublisherDisplayName come from Partner Center:
#   your app > Product management > Product identity
param(
  [string]$IdentityName = 'Akio1405.PebbleIsland',
  [string]$Publisher = 'CN=56E9ED75-1B25-4210-8D00-B62DC8BFAD83',
  [string]$PublisherDisplayName = 'Akio1405',
  [string]$DisplayName = 'Pebble Island',   # must match the name reserved in Partner Center
  [string]$Version = '1.0.2.0'
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$layout = Join-Path $root 'artifacts\layout'
$outDir = Join-Path $root 'artifacts'
$makeappx = Get-ChildItem (Join-Path $PSScriptRoot 'sdk-buildtools\pkg\bin') -Recurse -Filter makeappx.exe | Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1

if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null

# 1. self-contained build, so users don't need .NET installed
& "$env:ProgramFiles\dotnet\dotnet.exe" publish (Join-Path $root 'Pebble.csproj') -c Release -r win-x64 --self-contained true `
  -p:PublishReadyToRun=true -p:DebugType=none -o $layout -nologo -v q
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# 2. logos
New-Item -ItemType Directory -Force (Join-Path $layout 'Assets') | Out-Null
foreach ($f in 'Square44x44Logo.png', 'Square150x150Logo.png', 'Wide310x150Logo.png', 'StoreLogo.png') {
  Copy-Item (Join-Path $root "Assets\$f") (Join-Path $layout "Assets\$f")
}

# 3. manifest
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10"
         xmlns:uap3="http://schemas.microsoft.com/appx/manifest/uap/windows10/3"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap uap3 desktop rescap">
  <Identity Name="$IdentityName" Publisher="$Publisher" Version="$Version" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>$DisplayName</DisplayName>
    <PublisherDisplayName>$PublisherDisplayName</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Resources>
    <Resource Language="en-us" />
  </Resources>
  <Applications>
    <Application Id="Pebble" Executable="Pebble.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="$DisplayName" Description="A Dynamic Island for Windows"
                          BackgroundColor="transparent"
                          Square150x150Logo="Assets\Square150x150Logo.png"
                          Square44x44Logo="Assets\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png" />
      </uap:VisualElements>
      <Extensions>
        <desktop:Extension Category="windows.startupTask" Executable="Pebble.exe" EntryPoint="Windows.FullTrustApplication">
          <desktop:StartupTask TaskId="PebbleStartup" Enabled="false" DisplayName="$DisplayName" />
        </desktop:Extension>
      </Extensions>
    </Application>
  </Applications>
  <Capabilities>
    <Capability Name="internetClient" />
    <rescap:Capability Name="runFullTrust" />
    <uap3:Capability Name="userNotificationListener" />
    <DeviceCapability Name="location" />
  </Capabilities>
</Package>
"@
Set-Content -Path (Join-Path $layout 'AppxManifest.xml') -Value $manifest -Encoding UTF8

# 4. pack
$msix = Join-Path $outDir "Pebble_$($Version)_x64.msix"
if (Test-Path $msix) { Remove-Item $msix }
& $makeappx.FullName pack /d $layout /p $msix /o
if ($LASTEXITCODE) { throw 'makeappx failed' }
"`nPackage: $msix  ($([int]((Get-Item $msix).Length / 1MB)) MB)"
