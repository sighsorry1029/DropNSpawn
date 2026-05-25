[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$InputPath,

    [Parameter(Position = 1)]
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:InvariantCulture = [System.Globalization.CultureInfo]::InvariantCulture
$script:Warnings = [System.Collections.Generic.List[string]]::new()
$script:YamlDotNetAssembly = $null

function Resolve-YamlDotNetPath {
    $projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $candidates = @(
        (Join-Path $projectRoot "bin\Release\YamlDotNet.dll"),
        (Join-Path $projectRoot "bin\Debug\YamlDotNet.dll"),
        (Join-Path $env:USERPROFILE ".nuget\packages\yamldotnet\16.3.0\lib\net47\YamlDotNet.dll")
    )

    foreach ($candidate in $candidates) {
        if ([System.IO.File]::Exists($candidate)) {
            return $candidate
        }
    }

    throw "YamlDotNet.dll was not found. Checked: $($candidates -join ', ')"
}

function Ensure-YamlDotNetLoaded {
    if ($null -ne $script:YamlDotNetAssembly) {
        return
    }

    $alreadyLoaded = [AppDomain]::CurrentDomain.GetAssemblies() |
        Where-Object { $_.GetName().Name -eq "YamlDotNet" } |
        Select-Object -First 1
    if ($null -ne $alreadyLoaded) {
        $script:YamlDotNetAssembly = $alreadyLoaded
        return
    }

    $yamlAssemblyPath = Resolve-YamlDotNetPath
    $script:YamlDotNetAssembly = [System.Reflection.Assembly]::LoadFrom($yamlAssemblyPath)
}

function Test-YamlNodeType {
    param(
        [object]$Node,
        [string]$TypeName
    )

    return $null -ne $Node -and $Node.GetType().FullName -eq $TypeName
}

function Get-YamlSequenceEntries {
    param(
        [string]$YamlText
    )

    $yamlStreamType = $script:YamlDotNetAssembly.GetType("YamlDotNet.RepresentationModel.YamlStream", $true)
    $yamlStream = [Activator]::CreateInstance($yamlStreamType)
    $reader = [System.IO.StringReader]::new($YamlText)

    try {
        $yamlStream.Load($reader)
    }
    finally {
        $reader.Dispose()
    }

    if ($yamlStream.Documents.Count -eq 0) {
        return @()
    }

    $rootNode = $yamlStream.Documents[0].RootNode
    if (-not (Test-YamlNodeType $rootNode "YamlDotNet.RepresentationModel.YamlSequenceNode")) {
        throw "Expected the EWS file root to be a YAML list ('- prefab: ...')."
    }

    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($child in $rootNode.Children) {
        $entries.Add($child)
    }

    return $entries.ToArray()
}

function Assert-NoDuplicateEntryKeys {
    param(
        [string]$YamlText
    )

    $lines = $YamlText -split "`r?`n"
    $entryIndex = 0
    $keysByName = @{}
    $prefabLabel = $null

    for ($i = 0; $i -lt $lines.Length; $i++) {
        $lineNumber = $i + 1
        $line = $lines[$i]

        if ($line -match '^\s*-\s+prefab:\s*(.+?)\s*$') {
            $entryIndex++
            $keysByName = @{}
            $prefabLabel = $matches[1].Trim("'", '"', ' ')
            continue
        }

        if ($entryIndex -eq 0) {
            continue
        }

        if ($line -match '^\s{2}([A-Za-z0-9_]+):') {
            $key = $matches[1]
            if ($keysByName.ContainsKey($key)) {
                $firstLine = $keysByName[$key]
                $target = if ([string]::IsNullOrWhiteSpace($prefabLabel)) {
                    "entry $entryIndex"
                }
                else {
                    "entry $entryIndex ($prefabLabel)"
                }

                throw "Duplicate key '$key' in $target at lines $firstLine and $lineNumber. Remove one of them before conversion."
            }

            $keysByName[$key] = $lineNumber
        }
    }
}

function Get-MappingValueNode {
    param(
        [object]$MappingNode,
        [string]$Key
    )

    foreach ($entry in $MappingNode.Children.GetEnumerator()) {
        $keyValue = [string]$entry.Key.Value
        if ($keyValue.Equals($Key, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $entry.Value
        }
    }

    return $null
}

function Test-MappingHasKey {
    param(
        [object]$MappingNode,
        [string]$Key
    )

    return $null -ne (Get-MappingValueNode $MappingNode $Key)
}

function Get-ScalarValue {
    param(
        [object]$Node,
        [string]$Context
    )

    if ($null -eq $Node) {
        return $null
    }

    if (-not (Test-YamlNodeType $Node "YamlDotNet.RepresentationModel.YamlScalarNode")) {
        throw "$Context must be a YAML scalar."
    }

    return [string]$Node.Value
}

function Get-TrimmedScalarValue {
    param(
        [object]$MappingNode,
        [string]$Key
    )

    $node = Get-MappingValueNode $MappingNode $Key
    if ($null -eq $node) {
        return $null
    }

    return (Get-ScalarValue $node $Key).Trim()
}

function Get-BoolValue {
    param(
        [object]$MappingNode,
        [string]$Key
    )

    $raw = Get-TrimmedScalarValue $MappingNode $Key
    return [bool]::Parse($raw)
}

function Get-IntValue {
    param(
        [object]$MappingNode,
        [string]$Key
    )

    $raw = Get-TrimmedScalarValue $MappingNode $Key
    return [int]::Parse($raw, $script:InvariantCulture)
}

function Get-FloatValue {
    param(
        [object]$MappingNode,
        [string]$Key
    )

    $raw = Get-TrimmedScalarValue $MappingNode $Key
    return [double]::Parse($raw, $script:InvariantCulture)
}

function Get-StringListValue {
    param(
        [object]$Node,
        [string]$Context
    )

    if ($null -eq $Node) {
        return @()
    }

    $values = [System.Collections.Generic.List[string]]::new()
    if (Test-YamlNodeType $Node "YamlDotNet.RepresentationModel.YamlSequenceNode") {
        foreach ($child in $Node.Children) {
            $value = (Get-ScalarValue $child $Context).Trim()
            if ($value.Length -gt 0) {
                $values.Add($value)
            }
        }

        return ,([string[]]$values.ToArray())
    }

    if (Test-YamlNodeType $Node "YamlDotNet.RepresentationModel.YamlScalarNode") {
        foreach ($part in (Get-ScalarValue $Node $Context).Split(",")) {
            $value = $part.Trim()
            if ($value.Length -gt 0) {
                $values.Add($value)
            }
        }

        return ,([string[]]$values.ToArray())
    }

    throw "$Context must be either a scalar or a list."
}

function Get-StringMapValue {
    param(
        [object]$Node,
        [string]$Context
    )

    if ($null -eq $Node) {
        return $null
    }

    if (-not (Test-YamlNodeType $Node "YamlDotNet.RepresentationModel.YamlMappingNode")) {
        throw "$Context must be a YAML mapping."
    }

    $map = [ordered]@{}
    foreach ($entry in $Node.Children.GetEnumerator()) {
        $key = (Get-ScalarValue $entry.Key "$Context key").Trim()
        if ($key.Length -eq 0) {
            continue
        }

        $value = Get-ScalarValue $entry.Value "$Context.$key"
        $map[$key] = $value
    }

    if ($map.Count -eq 0) {
        return $null
    }

    return $map
}

function Format-Number {
    param(
        [double]$Value
    )

    return $Value.ToString("0.###############", $script:InvariantCulture)
}

function Format-Bool {
    param(
        [bool]$Value
    )

    if ($Value) {
        return "true"
    }

    return "false"
}

function Format-QuotedYamlString {
    param(
        [AllowNull()]
        [string]$Value
    )

    if ($null -eq $Value) {
        return "''"
    }

    return "'" + $Value.Replace("'", "''") + "'"
}

function Test-NeedsYamlQuotes {
    param(
        [AllowNull()]
        [string]$Value
    )

    if ($null -eq $Value) {
        return $true
    }

    if ($Value.Length -eq 0) {
        return $true
    }

    if ($Value.Trim() -ne $Value) {
        return $true
    }

    if ($Value -match '^-(\s|$)' -or
        $Value -match '^\?(\s|$)' -or
        $Value.StartsWith(":") -or
        $Value.StartsWith("@") -or
        $Value.StartsWith('`')) {
        return $true
    }

    if ($Value -match '^(true|false|null|~)$') {
        return $true
    }

    if ($Value -notmatch '^[A-Za-z0-9 _./~-]+$') {
        return $true
    }

    return $false
}

function Format-YamlString {
    param(
        [AllowNull()]
        [string]$Value
    )

    if (Test-NeedsYamlQuotes $Value) {
        return Format-QuotedYamlString $Value
    }

    return $Value
}

function Format-InlineList {
    param(
        [object[]]$Values
    )

    if ($null -eq $Values -or $Values.Count -eq 0) {
        return "[]"
    }

    $formatted = foreach ($value in $Values) {
        Format-YamlString ([string]$value)
    }

    return "[" + ($formatted -join ", ") + "]"
}

function Format-RangeString {
    param(
        [Nullable[double]]$Min,
        [Nullable[double]]$Max
    )

    $hasMin = $PSBoundParameters.ContainsKey("Min")
    $hasMax = $PSBoundParameters.ContainsKey("Max")
    if (-not $hasMin -and -not $hasMax) {
        return $null
    }

    if ($hasMin -and $hasMax) {
        if ([Math]::Abs($Min - $Max) -lt 0.0000001) {
            return Format-Number $Min
        }

        return "$(Format-Number $Min)~$(Format-Number $Max)"
    }

    if ($hasMin) {
        return "$(Format-Number $Min)~"
    }

    return "~$(Format-Number $Max)"
}

function Add-ValueIfNotNull {
    param(
        [System.Collections.IDictionary]$Map,
        [string]$Key,
        $Value
    )

    if ($null -eq $Value) {
        return
    }

    if ($Value -is [string] -and $Value.Length -eq 0) {
        return
    }

    if ($Value -is [System.Collections.IDictionary] -and $Value.Count -eq 0) {
        return
    }

    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string])) {
        $items = @($Value)
        if ($items.Count -eq 0) {
            return
        }
    }

    $Map[$Key] = $Value
}

function Convert-ExclusiveToggle {
    param(
        [bool]$AllowInside,
        [bool]$AllowOutside,
        [string]$FieldName,
        [int]$EntryIndex
    )

    if ($AllowInside -and $AllowOutside) {
        return $null
    }

    if ($AllowInside -and -not $AllowOutside) {
        return $true
    }

    if (-not $AllowInside -and $AllowOutside) {
        return $false
    }

    $script:Warnings.Add("Entry ${EntryIndex}: '$FieldName' had both inside and outside set to false. DNS cannot express that exact state, so the field was omitted.")
    return $null
}

function Convert-TimeOfDay {
    param(
        [bool]$SpawnAtDay,
        [bool]$SpawnAtNight
    )

    if ($SpawnAtDay -and $SpawnAtNight) {
        return $null
    }

    if ($SpawnAtDay -and -not $SpawnAtNight) {
        return ,([string[]]@("day"))
    }

    if (-not $SpawnAtDay -and $SpawnAtNight) {
        return ,([string[]]@("night"))
    }

    return ,([string[]]@())
}

function Add-StringArrayIfAny {
    param(
        [System.Collections.IDictionary]$Map,
        [string]$Key,
        $Values
    )

    if ($null -eq $Values) {
        return
    }

    $array = [string[]]@($Values)
    if ($array.Length -eq 0) {
        return
    }

    $Map[$Key] = $array
}

function Convert-EwsEntry {
    param(
        [object]$Node,
        [int]$EntryIndex
    )

    if (-not (Test-YamlNodeType $Node "YamlDotNet.RepresentationModel.YamlMappingNode")) {
        throw "Entry $EntryIndex must be a YAML mapping."
    }

    $prefab = Get-TrimmedScalarValue $Node "prefab"
    if ([string]::IsNullOrWhiteSpace($prefab)) {
        throw "Entry $EntryIndex is missing 'prefab'."
    }

    $dnsEntry = [ordered]@{}
    $dnsEntry["prefab"] = $prefab
    $dnsEntry["enabled"] = if (Test-MappingHasKey $Node "enabled") { Get-BoolValue $Node "enabled" } else { $true }

    $spawnSystem = [ordered]@{}
    $conditions = [ordered]@{}
    $modifiers = [ordered]@{}

    Add-ValueIfNotNull $spawnSystem "name" (Get-TrimmedScalarValue $Node "name")
    if (Test-MappingHasKey $Node "huntPlayer") { Add-ValueIfNotNull $spawnSystem "huntPlayer" (Get-BoolValue $Node "huntPlayer") }
    if (Test-MappingHasKey $Node "overrideLevelupChance") { Add-ValueIfNotNull $spawnSystem "overrideLevelUpChance" (Get-FloatValue $Node "overrideLevelupChance") }
    if (Test-MappingHasKey $Node "levelUpMinCenterDistance") { Add-ValueIfNotNull $spawnSystem "levelUpMinCenterDistance" (Get-FloatValue $Node "levelUpMinCenterDistance") }
    if (Test-MappingHasKey $Node "groundOffset") { Add-ValueIfNotNull $spawnSystem "groundOffset" (Get-FloatValue $Node "groundOffset") }
    if (Test-MappingHasKey $Node "groundOffsetRandom") { Add-ValueIfNotNull $spawnSystem "groundOffsetRandom" (Get-FloatValue $Node "groundOffsetRandom") }
    if (Test-MappingHasKey $Node "spawnInterval") { Add-ValueIfNotNull $spawnSystem "spawnInterval" (Get-FloatValue $Node "spawnInterval") }
    if (Test-MappingHasKey $Node "spawnChance") { Add-ValueIfNotNull $spawnSystem "spawnChance" (Get-FloatValue $Node "spawnChance") }
    if (Test-MappingHasKey $Node "groupRadius") { Add-ValueIfNotNull $spawnSystem "groupRadius" (Get-FloatValue $Node "groupRadius") }

    $hasMinLevel = Test-MappingHasKey $Node "minLevel"
    $hasMaxLevel = Test-MappingHasKey $Node "maxLevel"
    if ($hasMinLevel -or $hasMaxLevel) {
        if ($hasMinLevel -and -not $hasMaxLevel) {
            $levelText = Format-RangeString -Min (Get-IntValue $Node "minLevel")
            $script:Warnings.Add("Entry ${EntryIndex}: 'minLevel' was set without 'maxLevel'. The converter emitted an open-ended DNS level range.")
        }
        elseif (-not $hasMinLevel -and $hasMaxLevel) {
            $levelText = Format-RangeString -Min 1 -Max (Get-IntValue $Node "maxLevel")
        }
        else {
            $levelText = Format-RangeString -Min (Get-IntValue $Node "minLevel") -Max (Get-IntValue $Node "maxLevel")
        }

        Add-ValueIfNotNull $spawnSystem "level" $levelText
    }

    $hasSpawnRadiusMin = Test-MappingHasKey $Node "spawnRadiusMin"
    $hasSpawnRadiusMax = Test-MappingHasKey $Node "spawnRadiusMax"
    if ($hasSpawnRadiusMin -or $hasSpawnRadiusMax) {
        $spawnRadiusText = if ($hasSpawnRadiusMin -and $hasSpawnRadiusMax) {
            Format-RangeString -Min (Get-FloatValue $Node "spawnRadiusMin") -Max (Get-FloatValue $Node "spawnRadiusMax")
        }
        elseif ($hasSpawnRadiusMin) {
            Format-RangeString -Min (Get-FloatValue $Node "spawnRadiusMin") -Max 100
        }
        else {
            Format-RangeString -Max (Get-FloatValue $Node "spawnRadiusMax")
        }

        Add-ValueIfNotNull $spawnSystem "spawnRadius" $spawnRadiusText
    }

    $hasGroupSizeMin = Test-MappingHasKey $Node "groupSizeMin"
    $hasGroupSizeMax = Test-MappingHasKey $Node "groupSizeMax"
    if ($hasGroupSizeMin -or $hasGroupSizeMax) {
        if ($hasGroupSizeMin -and -not $hasGroupSizeMax) {
            $groupSizeText = Format-RangeString -Min (Get-IntValue $Node "groupSizeMin")
            $script:Warnings.Add("Entry ${EntryIndex}: 'groupSizeMin' was set without 'groupSizeMax'. The converter emitted an open-ended DNS groupSize range.")
        }
        elseif (-not $hasGroupSizeMin -and $hasGroupSizeMax) {
            $groupSizeText = Format-RangeString -Min 1 -Max (Get-IntValue $Node "groupSizeMax")
        }
        else {
            $groupSizeText = Format-RangeString -Min (Get-IntValue $Node "groupSizeMin") -Max (Get-IntValue $Node "groupSizeMax")
        }

        Add-ValueIfNotNull $spawnSystem "groupSize" $groupSizeText
    }

    if (Test-MappingHasKey $Node "maxSpawned") { Add-ValueIfNotNull $conditions "maxSpawned" (Get-IntValue $Node "maxSpawned") }
    if (Test-MappingHasKey $Node "spawnDistance") { Add-ValueIfNotNull $conditions "noSpawnRadius" (Get-FloatValue $Node "spawnDistance") }
    Add-StringArrayIfAny $conditions "biomes" (Get-StringListValue (Get-MappingValueNode $Node "biome") "biome")
    Add-StringArrayIfAny $conditions "biomeAreas" (Get-StringListValue (Get-MappingValueNode $Node "biomeArea") "biomeArea")
    Add-StringArrayIfAny $conditions "requiredEnvironments" (Get-StringListValue (Get-MappingValueNode $Node "requiredEnvironments") "requiredEnvironments")
    Add-ValueIfNotNull $conditions "requiredGlobalKey" (Get-TrimmedScalarValue $Node "requiredGlobalKey")
    if (Test-MappingHasKey $Node "canSpawnCloseToPlayer") { Add-ValueIfNotNull $conditions "canSpawnCloseToPlayer" (Get-BoolValue $Node "canSpawnCloseToPlayer") }
    if (Test-MappingHasKey $Node "insidePlayerBase") { Add-ValueIfNotNull $conditions "insidePlayerBase" (Get-BoolValue $Node "insidePlayerBase") }

    $spawnAtDay = if (Test-MappingHasKey $Node "spawnAtDay") { Get-BoolValue $Node "spawnAtDay" } else { $true }
    $spawnAtNight = if (Test-MappingHasKey $Node "spawnAtNight") { Get-BoolValue $Node "spawnAtNight" } else { $true }
    $timeOfDay = Convert-TimeOfDay -SpawnAtDay $spawnAtDay -SpawnAtNight $spawnAtNight
    if ($null -ne $timeOfDay) {
        $conditions["timeOfDay"] = $timeOfDay
    }

    $hasMinAltitude = Test-MappingHasKey $Node "minAltitude"
    $hasMaxAltitude = Test-MappingHasKey $Node "maxAltitude"
    if ($hasMinAltitude -or $hasMaxAltitude) {
        if ($hasMinAltitude -and $hasMaxAltitude) {
            $altitudeText = Format-RangeString -Min (Get-FloatValue $Node "minAltitude") -Max (Get-FloatValue $Node "maxAltitude")
        }
        elseif ($hasMinAltitude) {
            $altitudeText = Format-RangeString -Min (Get-FloatValue $Node "minAltitude") -Max 1000
        }
        else {
            $maxAltitude = Get-FloatValue $Node "maxAltitude"
            $defaultMinAltitude = if ($maxAltitude -gt 0) { 0 } else { -1000 }
            $altitudeText = Format-RangeString -Min $defaultMinAltitude -Max $maxAltitude
        }

        Add-ValueIfNotNull $conditions "altitude" $altitudeText
    }

    $hasMinTilt = Test-MappingHasKey $Node "minTilt"
    $hasMaxTilt = Test-MappingHasKey $Node "maxTilt"
    if ($hasMinTilt -or $hasMaxTilt) {
        $tiltText = if ($hasMinTilt -and $hasMaxTilt) {
            Format-RangeString -Min (Get-FloatValue $Node "minTilt") -Max (Get-FloatValue $Node "maxTilt")
        }
        elseif ($hasMinTilt) {
            Format-RangeString -Min (Get-FloatValue $Node "minTilt")
        }
        else {
            Format-RangeString -Min 0 -Max (Get-FloatValue $Node "maxTilt")
        }

        Add-ValueIfNotNull $conditions "tilt" $tiltText
    }

    $hasMinOceanDepth = Test-MappingHasKey $Node "minOceanDepth"
    $hasMaxOceanDepth = Test-MappingHasKey $Node "maxOceanDepth"
    if ($hasMinOceanDepth -or $hasMaxOceanDepth) {
        $oceanDepthText = if ($hasMinOceanDepth -and $hasMaxOceanDepth) {
            Format-RangeString -Min (Get-FloatValue $Node "minOceanDepth") -Max (Get-FloatValue $Node "maxOceanDepth")
        }
        elseif ($hasMinOceanDepth) {
            Format-RangeString -Min (Get-FloatValue $Node "minOceanDepth")
        }
        else {
            Format-RangeString -Min 0 -Max (Get-FloatValue $Node "maxOceanDepth")
        }

        Add-ValueIfNotNull $conditions "oceanDepth" $oceanDepthText
    }

    $hasMinDistance = Test-MappingHasKey $Node "minDistance"
    $hasMaxDistance = Test-MappingHasKey $Node "maxDistance"
    if ($hasMinDistance -or $hasMaxDistance) {
        $distanceText = if ($hasMinDistance -and $hasMaxDistance) {
            Format-RangeString -Min (Get-FloatValue $Node "minDistance") -Max (Get-FloatValue $Node "maxDistance")
        }
        elseif ($hasMinDistance) {
            Format-RangeString -Min (Get-FloatValue $Node "minDistance")
        }
        else {
            Format-RangeString -Min 0 -Max (Get-FloatValue $Node "maxDistance")
        }

        Add-ValueIfNotNull $conditions "distanceFromCenter" $distanceText
    }

    if ((Test-MappingHasKey $Node "inForest") -or (Test-MappingHasKey $Node "outsideForest")) {
        $forestInside = if (Test-MappingHasKey $Node "inForest") { Get-BoolValue $Node "inForest" } else { $true }
        $forestOutside = if (Test-MappingHasKey $Node "outsideForest") { Get-BoolValue $Node "outsideForest" } else { $true }
        Add-ValueIfNotNull $conditions "inForest" (Convert-ExclusiveToggle -AllowInside $forestInside -AllowOutside $forestOutside -FieldName "forest" -EntryIndex $EntryIndex)
    }

    if ((Test-MappingHasKey $Node "inLava") -or (Test-MappingHasKey $Node "outsideLava")) {
        $lavaInside = if (Test-MappingHasKey $Node "inLava") { Get-BoolValue $Node "inLava" } else { $false }
        $lavaOutside = if (Test-MappingHasKey $Node "outsideLava") { Get-BoolValue $Node "outsideLava" } else { $true }
        Add-ValueIfNotNull $conditions "inLava" (Convert-ExclusiveToggle -AllowInside $lavaInside -AllowOutside $lavaOutside -FieldName "lava" -EntryIndex $EntryIndex)
    }

    Add-ValueIfNotNull $modifiers "data" (Get-TrimmedScalarValue $Node "data")
    Add-ValueIfNotNull $modifiers "faction" (Get-TrimmedScalarValue $Node "faction")
    Add-ValueIfNotNull $modifiers "fields" (Get-StringMapValue (Get-MappingValueNode $Node "fields") "fields")
    Add-StringArrayIfAny $modifiers "objects" (Get-StringListValue (Get-MappingValueNode $Node "objects") "objects")

    if (-not (Test-MappingHasKey $Node "inForest") -and -not (Test-MappingHasKey $Node "outsideForest")) {
        $conditions.Remove("inForest")
    }

    if (-not (Test-MappingHasKey $Node "inLava") -and -not (Test-MappingHasKey $Node "outsideLava")) {
        $conditions.Remove("inLava")
    }

    if ($spawnSystem.Count -gt 0) {
        $dnsEntry["spawnSystem"] = $spawnSystem
    }

    if ($conditions.Count -gt 0) {
        $dnsEntry["conditions"] = $conditions
    }

    if ($modifiers.Count -gt 0) {
        $dnsEntry["modifiers"] = $modifiers
    }

    return $dnsEntry
}

function Append-YamlScalarLine {
    param(
        [System.Text.StringBuilder]$Builder,
        [int]$Indent,
        [string]$Key,
        $Value
    )

    $prefix = (" " * $Indent) + $Key + ": "
    if ($Value -is [bool]) {
        [void]$Builder.AppendLine($prefix + (Format-Bool $Value))
        return
    }

    if ($Value -is [int] -or $Value -is [long]) {
        [void]$Builder.AppendLine($prefix + $Value.ToString($script:InvariantCulture))
        return
    }

    if ($Value -is [double] -or $Value -is [float] -or $Value -is [decimal]) {
        [void]$Builder.AppendLine($prefix + (Format-Number ([double]$Value)))
        return
    }

    [void]$Builder.AppendLine($prefix + (Format-YamlString ([string]$Value)))
}

function Append-YamlInlineListLine {
    param(
        [System.Text.StringBuilder]$Builder,
        [int]$Indent,
        [string]$Key,
        [object[]]$Values
    )

    [void]$Builder.AppendLine((" " * $Indent) + $Key + ": " + (Format-InlineList $Values))
}

function Append-YamlStringMapBlock {
    param(
        [System.Text.StringBuilder]$Builder,
        [int]$Indent,
        [string]$Key,
        [System.Collections.IDictionary]$Map
    )

    [void]$Builder.AppendLine((" " * $Indent) + $Key + ":")
    foreach ($entry in $Map.GetEnumerator()) {
        [void]$Builder.AppendLine((" " * ($Indent + 2)) + (Format-YamlString ([string]$entry.Key)) + ": " + (Format-YamlString ([string]$entry.Value)))
    }
}

function Append-YamlStringListBlock {
    param(
        [System.Text.StringBuilder]$Builder,
        [int]$Indent,
        [string]$Key,
        [object[]]$Values
    )

    [void]$Builder.AppendLine((" " * $Indent) + $Key + ":")
    foreach ($value in $Values) {
        [void]$Builder.AppendLine((" " * ($Indent + 2)) + "- " + (Format-YamlString ([string]$value)))
    }
}

function Append-DnsBlock {
    param(
        [System.Text.StringBuilder]$Builder,
        [int]$Indent,
        [string]$BlockName,
        [System.Collections.IDictionary]$Block
    )

    [void]$Builder.AppendLine((" " * $Indent) + $BlockName + ":")
    foreach ($entry in $Block.GetEnumerator()) {
        $value = $entry.Value
        if ($value -is [System.Collections.IDictionary]) {
            Append-YamlStringMapBlock -Builder $Builder -Indent ($Indent + 2) -Key $entry.Key -Map $value
            continue
        }

        if ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string])) {
            $items = @($value)
            if ($entry.Key -eq "objects") {
                Append-YamlStringListBlock -Builder $Builder -Indent ($Indent + 2) -Key $entry.Key -Values $items
            }
            else {
                Append-YamlInlineListLine -Builder $Builder -Indent ($Indent + 2) -Key $entry.Key -Values $items
            }

            continue
        }

        Append-YamlScalarLine -Builder $Builder -Indent ($Indent + 2) -Key $entry.Key -Value $value
    }
}

function Format-DnsYaml {
    param(
        [object[]]$Entries,
        [string]$SourcePath
    )

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.AppendLine("# Generated by tools/ews_to_dns.ps1 from $(Split-Path -Leaf $SourcePath)")

    foreach ($entry in $Entries) {
        [void]$builder.AppendLine("- prefab: " + (Format-YamlString ([string]$entry["prefab"])))
        [void]$builder.AppendLine("  enabled: " + (Format-Bool ([bool]$entry["enabled"])))

        if ($entry.Contains("spawnSystem")) {
            Append-DnsBlock -Builder $builder -Indent 2 -BlockName "spawnSystem" -Block $entry["spawnSystem"]
        }

        if ($entry.Contains("conditions")) {
            Append-DnsBlock -Builder $builder -Indent 2 -BlockName "conditions" -Block $entry["conditions"]
        }

        if ($entry.Contains("modifiers")) {
            Append-DnsBlock -Builder $builder -Indent 2 -BlockName "modifiers" -Block $entry["modifiers"]
        }

        [void]$builder.AppendLine()
    }

    return $builder.ToString()
}

Ensure-YamlDotNetLoaded

$resolvedInputPath = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $InputPath).Path)
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $inputDirectory = Split-Path -Parent $resolvedInputPath
    $inputBaseName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedInputPath)
    $resolvedOutputPath = Join-Path $inputDirectory ($inputBaseName + ".dns.yml")
}
else {
    if ([System.IO.Path]::IsPathRooted($OutputPath)) {
        $resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
    }
    else {
        $resolvedOutputPath = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputPath))
    }
}

$yamlText = Get-Content -LiteralPath $resolvedInputPath -Raw
Assert-NoDuplicateEntryKeys -YamlText $yamlText
$yamlEntries = Get-YamlSequenceEntries -YamlText $yamlText
$convertedEntries = [System.Collections.Generic.List[object]]::new()

$entryIndex = 0
foreach ($yamlEntry in $yamlEntries) {
    $entryIndex++
    $convertedEntries.Add((Convert-EwsEntry -Node $yamlEntry -EntryIndex $entryIndex))
}

$outputText = Format-DnsYaml -Entries @($convertedEntries) -SourcePath $resolvedInputPath
$outputDirectory = Split-Path -Parent $resolvedOutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
}

[System.IO.File]::WriteAllText($resolvedOutputPath, $outputText, [System.Text.UTF8Encoding]::new($false))

Write-Host "Wrote $($convertedEntries.Count) DNS spawnsystem entr$(if ($convertedEntries.Count -eq 1) { 'y' } else { 'ies' }) to $resolvedOutputPath"
foreach ($warning in $script:Warnings) {
    Write-Warning $warning
}
