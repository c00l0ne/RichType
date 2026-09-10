$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$kazRoot = $PSScriptRoot
$kazOutputDirectory = Join-Path $kazRoot 'dist'
New-Item -ItemType Directory -Path $kazOutputDirectory -Force | Out-Null
$kazArchive = Join-Path $kazOutputDirectory 'RichType-source.zip'
$kazTemporaryArchive = $kazArchive + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    $kazRootFiles = @('README.md','LICENSE','Directory.Build.props','global.json','.gitignore','.gitattributes','build.cmd','test.cmd','pack-source.ps1')
    $kazFiles = @($kazRootFiles | ForEach-Object { Get-Item -LiteralPath (Join-Path $kazRoot $_) })
    foreach ($kazSubdirectory in @('src','tests','docs')) {
        $kazFiles += @(Get-ChildItem -LiteralPath (Join-Path $kazRoot $kazSubdirectory) -File -Recurse |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Extension -in @('.cs','.csproj','.manifest','.md','.json','.txt') })
    }
    $kazStream = [System.IO.File]::Open($kazTemporaryArchive, [System.IO.FileMode]::CreateNew)
    try {
        $kazZip = New-Object System.IO.Compression.ZipArchive($kazStream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($kazFile in $kazFiles) {
                $kazRelative = $kazFile.FullName.Substring($kazRoot.Length + 1).Replace('\','/')
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($kazZip, $kazFile.FullName, 'RichType/' + $kazRelative) | Out-Null
            }
        }
        finally { $kazZip.Dispose() }
    }
    finally { $kazStream.Dispose() }
    # Replace only a complete archive. File.Replace also works in Windows
    # PowerShell 5.1 and keeps the previous ZIP intact when writing fails.
    if ([System.IO.File]::Exists($kazArchive)) {
        # PowerShell 5.1 can bind a null string as an invalid empty path.
        $kazPreviousArchive = $kazTemporaryArchive + '.previous'
        [System.IO.File]::Replace($kazTemporaryArchive, $kazArchive, $kazPreviousArchive)
        Remove-Item -LiteralPath $kazPreviousArchive -Force
    }
    else { [System.IO.File]::Move($kazTemporaryArchive, $kazArchive) }
}
finally { if (Test-Path -LiteralPath $kazTemporaryArchive) { Remove-Item -LiteralPath $kazTemporaryArchive -Force } }
Write-Output "Source archive: $kazArchive"
