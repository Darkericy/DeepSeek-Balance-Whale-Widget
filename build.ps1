param([switch]$Check, [string]$OutputPath)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$cache = Join-Path $PSScriptRoot '.build'
[void][IO.Directory]::CreateDirectory($cache)
function Get-PinnedArchive($fileName, $source, $expected) {
    $path = Join-Path $cache $fileName
    if (-not [IO.File]::Exists($path)) {
        $download = Join-Path $cache ('download-' + [guid]::NewGuid() + '.tmp')
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $source -OutFile $download
            if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expected) { throw "Invalid download checksum for $fileName." }
            try { [IO.File]::Move($download, $path) }
            catch [IO.IOException] { if (-not [IO.File]::Exists($path)) { throw } }
        } finally { if ([IO.File]::Exists($download)) { [IO.File]::Delete($download) } }
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected) { throw "Invalid cached checksum for $fileName." }
    return $path
}
$archive = Get-PinnedArchive 'qianwen-windows-x64.zip' 'https://github.com/QianWen-AI/qianwen-cli/releases/download/v1.12.0/qianwen-windows-x64.zip' 'E567B4D17170038BD51FF830D62D125A037B90FD63C2D6E20B239838B80AE0F3'
$sdkVersion = '1.0.3537.50'
$package = Get-PinnedArchive "webview2-$sdkVersion.nupkg" "https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/$sdkVersion/microsoft.web.webview2.$sdkVersion.nupkg" '5EA526BBD728ADDA0DA4D31219267E96460494A427E4894C4E09D9F320F4B9AA'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$flags = @('/nologo', '/optimize+', '/warn:4', '/langversion:5', '/platform:x64', '/codepage:65001', '/win32icon:ui/shopkeeper.ico', '/win32manifest:src/app.manifest')
foreach ($name in @('System', 'System.Core', 'System.Xaml', 'System.Net.Http', 'System.Web.Extensions', 'System.Security', 'System.Drawing', 'System.Windows.Forms', 'System.IO.Compression', 'System.Xml')) {
    $flags += '/r:' + (Join-Path $framework ($name + '.dll'))
}
foreach ($name in @('WindowsBase', 'PresentationCore', 'PresentationFramework')) {
    $flags += '/r:' + (Join-Path $framework ('WPF/' + $name + '.dll'))
}
foreach ($file in (Get-ChildItem -LiteralPath 'ui' -Filter '*.xaml')) { $flags += '/resource:' + $file.FullName + ',' + $file.Name }
$flags += '/resource:ui/shopkeeper-ledger.png,shopkeeper-ledger.png'
$flags += '/resource:LICENSE,Whale-Widget-LICENSE.txt'
$flags += '/resource:PROVENANCE.md,Art-Credits.md'
$flags += '/resource:' + $archive + ',qianwen.zip'
$flags += '/resource:third_party/qianwen-cli-LICENSE.txt,qianwen-LICENSE.txt'
$sources = @(Get-ChildItem -LiteralPath 'src' -Filter '*.cs' | ForEach-Object FullName)
$sdkFolder = Join-Path $cache ('webview2-' + [guid]::NewGuid())
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($package, $sdkFolder)
    foreach ($fileName in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll')) {
        $library = Join-Path $sdkFolder ('lib/net462/' + $fileName)
        $flags += '/r:' + $library
        $flags += '/resource:' + $library + ',' + $fileName
    }
    $flags += '/resource:' + (Join-Path $sdkFolder 'runtimes/win-x64/native/WebView2Loader.dll') + ',WebView2Loader.dll'
    $flags += '/resource:' + (Join-Path $sdkFolder 'LICENSE.txt') + ',WebView2-LICENSE.txt'
    $flags += '/resource:' + (Join-Path $sdkFolder 'NOTICE.txt') + ',WebView2-NOTICE.txt'
    if ($Check) {
        $checkExe = Join-Path $cache 'Checks.exe'
        $checkSource = Join-Path $PSScriptRoot 'tests\Checks.cs'
        & $compiler @flags /target:exe /main:BalanceWatcher.Checks ('/out:' + $checkExe) @sources $checkSource
        if ($LASTEXITCODE -ne 0) { throw 'Check build failed.' }
        & '.\.build\Checks.exe'
        if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
    } else {
        $output = if ($OutputPath) { [IO.Path]::GetFullPath($OutputPath) } else { Join-Path $PSScriptRoot '余额看板.exe' }
        & $compiler @flags /target:winexe /main:BalanceWatcher.Program ('/out:' + $output) @sources
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
        Write-Output $output
    }
} finally {
    if ([IO.Directory]::Exists($sdkFolder)) { [IO.Directory]::Delete($sdkFolder, $true) }
}
