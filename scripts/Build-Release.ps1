param([string]$Version='1.4.0',[switch]$IncludeSource)
$ErrorActionPreference='Stop'
$repo=Split-Path -Parent $PSScriptRoot
$dist=Join-Path $repo ('dist\v'+$Version)
if(Test-Path -LiteralPath $dist){throw 'Release directory already exists; preserve and inspect it before rebuilding'}
if($IncludeSource){
    $dirty=& git -C $repo status --porcelain --untracked-files=all
    if($LASTEXITCODE-ne0 -or $dirty){throw 'Commit the reviewed source tree before creating its source archive'}
}
New-Item -ItemType Directory -Path $dist|Out-Null
foreach($kind in @('CLI','GUI')){
    $project=if($kind-eq'CLI'){'PS3HddTool.Migrate'}else{'PS3HddTool.Avalonia'}
    $output=Join-Path $dist $kind
    & dotnet publish (Join-Path $repo $project) -c Release -r win-x64 --self-contained true -o $output -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false
    if($LASTEXITCODE-ne0){throw ('Publish failed: '+$kind)}
    if(Get-ChildItem -LiteralPath $output -Filter 'Avalonia.BuildServices*.dll'){throw 'Build-only dependency must not be redistributed'}
}
$licenses=Join-Path $dist 'third-party-licenses'
& python (Join-Path $PSScriptRoot 'Collect-Licenses.py') --root $repo --output $licenses
if($LASTEXITCODE-ne0){throw 'Dependency license collection failed'}
foreach($kind in @('CLI','GUI')){
    $output=Join-Path $dist $kind
    foreach($file in @('README.md','LICENSE.md','NOTICE.md','DISCLAIMER.md','THIRD-PARTY-NOTICES.md','actual-migration.request.template.json','direct-request.example.json','migration-request.example.json')){
        Copy-Item -LiteralPath (Join-Path $repo $file) -Destination $output
    }
    Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination (Join-Path $output 'docs') -Recurse
    Copy-Item -LiteralPath (Join-Path $repo 'evidence') -Destination (Join-Path $output 'evidence') -Recurse
    Copy-Item -LiteralPath $licenses -Destination (Join-Path $output 'third-party-licenses') -Recurse
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath (Join-Path $dist ('PS3HDDTool-'+$kind+'-v'+$Version+'-win-x64.zip')) -CompressionLevel Optimal
}
if($IncludeSource){
    $archive=Join-Path $dist ('PS3HDDTool-v'+$Version+'-source.zip')
    & git -C $repo archive --format=zip ('--prefix=PS3HDDTool-v'+$Version+'/') -o $archive HEAD
    if($LASTEXITCODE-ne0){throw 'Source archive failed'}
}
$checksums=@(Get-ChildItem -LiteralPath $dist -Filter '*.zip'|Sort-Object Name|ForEach-Object {((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLower()+'  '+$_.Name)})
[IO.File]::WriteAllLines((Join-Path $dist 'SHA256SUMS.txt'),$checksums)
Write-Output $dist
