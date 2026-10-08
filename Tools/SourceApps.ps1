# Shared source-app discovery and path validation for developer builds and releases.

function Get-SourceAppStableId {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$AppDirectory
    )

    $root = [IO.Path]::GetFullPath($RootPath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $folder = [IO.Path]::GetFullPath($AppDirectory)
    if (-not $folder.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Source app folder must be inside the source root: $folder"
    }
    $relative = $folder.Substring($root.Length + 1).Replace([IO.Path]::DirectorySeparatorChar, '/').ToLowerInvariant()
    $name = [IO.Path]::GetFileName($folder).ToLowerInvariant()
    $id = [Regex]::Replace([Regex]::Replace($name, '[^a-z0-9_-]+', '-'), '^[-_]+|[-_]+$', '')
    if (-not $id) {
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($relative))).Replace('-', '').ToLowerInvariant() }
        finally { $algorithm.Dispose() }
        $id = 'app-' + $hash.Substring(0, 12)
    }
    if ($id.Length -gt 80 -or $id -in @('folder-compare', 'swift-copy', 'disk-atlas', 'deskcal') -or
        $id -match '^(?i:con|prn|aux|nul|com[1-9]|lpt[1-9])$') {
        throw "Source app folder produces a reserved or unsafe app id '$id': $folder"
    }
    return $id
}

function Test-SourceAppSkippedDirectory {
    param([Parameter(Mandatory = $true)][string]$Name)
    $excludedNames = @(
        '.git', '.vs', '.vscode', 'bin', 'obj', 'node_modules', 'artifacts', 'dist',
        'test', 'tests', 'check', 'checks', 'launcher', 'launcher.checks', 'launcher.uichecks',
        'distribution', 'tools'
    )
    if ($Name -in $excludedNames) { return $true }
    return $Name.EndsWith('.test', [StringComparison]::OrdinalIgnoreCase) -or
        $Name.EndsWith('.tests', [StringComparison]::OrdinalIgnoreCase) -or
        $Name.EndsWith('.check', [StringComparison]::OrdinalIgnoreCase) -or
        $Name.EndsWith('.checks', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-SourceAppProjectPath {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$ProjectPath,
        [string]$ExpectedAppId
    )

    if ([IO.Path]::IsPathRooted($ProjectPath)) { throw "Project path must be root-relative: $ProjectPath" }
    $root = [IO.Path]::GetFullPath($RootPath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $project = [IO.Path]::GetFullPath((Join-Path $root $ProjectPath))
    if (-not $project.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Project path escapes the source root: $ProjectPath"
    }
    if ([IO.Path]::GetExtension($project) -ne '.csproj' -or -not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "Project must name an existing .csproj file: $ProjectPath"
    }
    $relative = $project.Substring($root.Length + 1).Replace('/', '\')
    $parts = $relative.Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)
    if ($parts.Count -lt 2 -or
        ($parts[0] -ieq 'Projects' -and $parts.Count -lt 3)) {
        throw "Source projects must be in an immediate app folder or Projects/<app folder>: $ProjectPath"
    }
    if ($parts.Count -gt 7) { throw "Source project is nested too deeply: $ProjectPath" }
    if ($parts[0] -ine 'Projects' -and $parts[0] -in @('Launcher', 'FolderCompareWPF', 'SwiftCopy', 'DiskAtlas', 'DeskCal', 'Distribution', 'Tools')) {
        throw "Project is inside a reserved launcher or tooling directory: $ProjectPath"
    }
    foreach ($part in $parts[0..($parts.Count - 2)]) {
        if (Test-SourceAppSkippedDirectory $part) { throw "Project is inside an excluded build or test directory: $ProjectPath" }
    }
    $current = $root
    foreach ($part in $parts) {
        $current = Join-Path $current $part
        if (-not (Test-Path -LiteralPath $current)) { break }
        $item = Get-Item -Force -LiteralPath $current
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Project path contains a reparse point: $current" }
        if (($item.Attributes -band [IO.FileAttributes]::Hidden) -ne 0) { throw "Project path contains a hidden file or directory: $current" }
    }
    if ($parts[0] -ieq 'Projects') { $appFolder = Join-Path (Join-Path $root 'Projects') $parts[1] }
    else { $appFolder = Join-Path $root $parts[0] }
    if ($ExpectedAppId) {
        $expected = Get-SourceAppStableId $root $appFolder
        if ($ExpectedAppId -cne $expected) { throw "App id '$ExpectedAppId' does not match project folder id '$expected'." }
    }
    return [pscustomobject]@{ ProjectPath = $project; AppFolder = $appFolder; Id = (Get-SourceAppStableId $root $appFolder) }
}

function Get-SourceAppProjectInfo {
    param([Parameter(Mandatory = $true)][string]$ProjectPath)

    [xml]$xml = Get-Content -LiteralPath $ProjectPath -Raw -Encoding UTF8
    $assemblyName = [IO.Path]::GetFileNameWithoutExtension($ProjectPath)
    $assemblyNodes = @($xml.SelectNodes("//*[local-name()='AssemblyName']") | Where-Object { -not [string]::IsNullOrWhiteSpace($_.InnerText) })
    if ($assemblyNodes.Count -gt 0) { $assemblyName = $assemblyNodes[0].InnerText.Trim() }
    $outputTypes = @($xml.SelectNodes("//*[local-name()='OutputType']") | ForEach-Object { $_.InnerText.Trim() } | Where-Object { $_ } | Sort-Object -Unique)
    $outputType = if ($outputTypes.Count -eq 0) { 'Library' } else { $outputTypes -join ', ' }
    $isRunnable = $outputTypes.Count -gt 0 -and @($outputTypes | Where-Object { $_ -notin @('Exe', 'WinExe') }).Count -eq 0
    $invalidFileName = [IO.Path]::GetInvalidFileNameChars()
    if ([string]::IsNullOrWhiteSpace($assemblyName) -or $assemblyName.IndexOfAny($invalidFileName) -ge 0 -or
        $assemblyName.Contains('/') -or $assemblyName.Contains('\') -or $assemblyName.Contains('$(') -or $assemblyName.Contains('@(')) {
        throw "AssemblyName cannot be used as a Windows executable name: $assemblyName"
    }
    return [pscustomobject]@{ AssemblyName = $assemblyName; OutputType = $outputType; IsRunnable = $isRunnable; ExecutableName = "$assemblyName.exe" }
}

function Get-SourceAppDirectories {
    param([Parameter(Mandatory = $true)][string]$RootPath)

    $root = [IO.Path]::GetFullPath($RootPath)
    $roots = [Collections.Generic.List[string]]::new()
    foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -Force | Sort-Object Name) {
        if ($directory.Name -ieq 'Projects' -and
            ($directory.Attributes -band [IO.FileAttributes]::Hidden) -eq 0 -and
            ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            foreach ($projectDirectory in Get-ChildItem -LiteralPath $directory.FullName -Directory -Force | Sort-Object Name) {
                if (-not (Test-SourceAppSkippedDirectory $projectDirectory.Name) -and
                    ($projectDirectory.Attributes -band [IO.FileAttributes]::Hidden) -eq 0 -and
                    ($projectDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) { $roots.Add($projectDirectory.FullName) }
            }
        }
        elseif (-not (Test-SourceAppSkippedDirectory $directory.Name) -and
            $directory.Name -notin @('FolderCompareWPF', 'SwiftCopy', 'DiskAtlas', 'DeskCal') -and
            ($directory.Attributes -band [IO.FileAttributes]::Hidden) -eq 0 -and
            ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) { $roots.Add($directory.FullName) }
    }
    return @($roots.ToArray())
}

function Find-SourceAppProjects {
    param([Parameter(Mandatory = $true)][string]$RootPath)

    $found = [Collections.Generic.List[object]]::new()
    foreach ($appFolder in Get-SourceAppDirectories $RootPath) {
        $manifest = Join-Path $appFolder 'launcher.app.json'
        if (Test-Path -LiteralPath $manifest -PathType Leaf) { continue }
        $pending = [Collections.Generic.Queue[object]]::new()
        $pending.Enqueue([pscustomobject]@{ Path = $appFolder; Depth = 0 })
        $projects = [Collections.Generic.List[string]]::new()
        while ($pending.Count -gt 0) {
            $entry = $pending.Dequeue()
            if ($entry.Depth -gt 4) { continue }
            foreach ($file in Get-ChildItem -LiteralPath $entry.Path -File -Filter '*.csproj' -Force | Sort-Object Name) { $projects.Add($file.FullName) }
            foreach ($child in Get-ChildItem -LiteralPath $entry.Path -Directory -Force | Sort-Object Name) {
                if (Test-SourceAppSkippedDirectory $child.Name) { continue }
                if (($child.Attributes -band [IO.FileAttributes]::Hidden) -ne 0 -or
                    ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
                $pending.Enqueue([pscustomobject]@{ Path = $child.FullName; Depth = $entry.Depth + 1 })
            }
        }
        $runnableProjects = [Collections.Generic.List[object]]::new()
        foreach ($project in $projects) {
            $root = [IO.Path]::GetFullPath($RootPath).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            $relativeProject = $project.Substring($root.Length + 1)
            $validated = Assert-SourceAppProjectPath $RootPath $relativeProject
            $info = Get-SourceAppProjectInfo $validated.ProjectPath
            if ($info.IsRunnable) {
                $runnableProjects.Add([pscustomobject]@{ Validated = $validated; Info = $info; RelativeProjectPath = $relativeProject })
            }
        }
        if ($runnableProjects.Count -gt 1) { throw "Source app folder contains multiple runnable .csproj files and is ambiguous: $appFolder" }
        if ($runnableProjects.Count -eq 1) {
            $selected = $runnableProjects[0]
            $validated = $selected.Validated
            $info = $selected.Info
            $relativeProject = $selected.RelativeProjectPath
            $found.Add([pscustomobject]@{ Id = $validated.Id; AppFolder = $validated.AppFolder; ProjectPath = $validated.ProjectPath; RelativeProjectPath = $relativeProject; Name = [IO.Path]::GetFileName($validated.AppFolder); ExecutableName = $info.ExecutableName; Manifest = $null; IsAutomatic = $true })
        }
    }
    $duplicates = @($found | Group-Object Id | Where-Object Count -gt 1)
    if ($duplicates.Count) { throw "Duplicate source app id: $($duplicates[0].Name)" }
    return @($found.ToArray())
}
