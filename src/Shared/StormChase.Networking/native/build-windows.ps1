param([string]$CachePath = (Join-Path $env:USERPROFILE '.cache/mmoi-gns'), [ValidateRange(1,32)][int]$Parallel = 8)
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Path $CachePath -Force | Out-Null
foreach ($name in @('llvm','cmake','ninja','gns','protobuf','abseil')) {
    $dependency = $manifest.$name
    $archive = Join-Path $CachePath $dependency.archive
    if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest $dependency.url -OutFile $archive }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $dependency.sha256) { throw "Hash mismatch: $archive" }
    $folder = Join-Path $CachePath $dependency.folder
    if ($name -eq 'ninja') { if (!(Test-Path -LiteralPath (Join-Path $CachePath 'ninja.exe'))) { Expand-Archive -LiteralPath $archive -DestinationPath $CachePath -Force } }
    elseif (!(Test-Path -LiteralPath $folder)) { Expand-Archive -LiteralPath $archive -DestinationPath $CachePath -Force }
}
$previousPath = $env:PATH
try {
    $env:PATH = (Join-Path $CachePath "$($manifest.llvm.folder)/bin") + ';' + $CachePath + ';' + $env:PATH
    $cmake = Join-Path $CachePath "$($manifest.cmake.folder)/bin/cmake.exe"
    $protobufBuild = Join-Path $CachePath 'protobuf36-build'
    $protobufInstall = Join-Path $CachePath 'protobuf36-install'
    $protobufSource = Join-Path $CachePath $manifest.protobuf.folder
    $abseilSource = Join-Path $CachePath $manifest.abseil.folder
    & $cmake -S $protobufSource -B $protobufBuild -G Ninja -DCMAKE_BUILD_TYPE=Release '-DCMAKE_C_COMPILER=x86_64-w64-mingw32-clang' '-DCMAKE_CXX_COMPILER=x86_64-w64-mingw32-clang++' "-DCMAKE_INSTALL_PREFIX=$protobufInstall" "-DFETCHCONTENT_SOURCE_DIR_ABSL=$abseilSource" -Dprotobuf_BUILD_TESTS=OFF -Dprotobuf_BUILD_SHARED_LIBS=OFF -Dprotobuf_MSVC_STATIC_RUNTIME=OFF -Dprotobuf_WITH_ZLIB=OFF '-DCMAKE_CXX_FLAGS=-Wno-deprecated-attributes'
    if ($LASTEXITCODE -ne 0) { throw 'Protobuf configuration failed.' }
    & $cmake --build $protobufBuild --parallel $Parallel
    if ($LASTEXITCODE -ne 0) { throw 'Protobuf build failed.' }
    & $cmake --install $protobufBuild
    if ($LASTEXITCODE -ne 0) { throw 'Protobuf install failed.' }
    $gnsBuild = Join-Path $CachePath 'gns36-build'
    $gnsSource = Join-Path $CachePath $manifest.gns.folder
    & $cmake -S $PSScriptRoot -B $gnsBuild -G Ninja -DCMAKE_BUILD_TYPE=Release '-DCMAKE_C_COMPILER=x86_64-w64-mingw32-clang' '-DCMAKE_CXX_COMPILER=x86_64-w64-mingw32-clang++' "-DCMAKE_PREFIX_PATH=$protobufInstall" "-DGNS_SOURCE_DIR=$gnsSource" '-DCMAKE_CXX_FLAGS=-Wno-deprecated-attributes'
    if ($LASTEXITCODE -ne 0) { throw 'GNS configuration failed.' }
    & $cmake --build $gnsBuild --parallel $Parallel
    if ($LASTEXITCODE -ne 0) { throw 'GNS build failed.' }
    $runtime = Join-Path (Split-Path -Parent $PSScriptRoot) 'runtimes/win-x64/native'
    New-Item -ItemType Directory -Path $runtime -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $gnsBuild 'stormchase_gns.dll') -Destination $runtime -Force
    & (Join-Path $CachePath "$($manifest.llvm.folder)/bin/llvm-objdump.exe") -p (Join-Path $runtime 'stormchase_gns.dll') | Select-String 'DLL Name'
    Get-FileHash -LiteralPath (Join-Path $runtime 'stormchase_gns.dll') -Algorithm SHA256
}
finally { $env:PATH = $previousPath }
