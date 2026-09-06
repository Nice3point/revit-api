$assemblies = @(
    "AdWindows"
    "PackageContentsParser"
    "RevitAddInUtility"
    "RevitAPI"
    "RevitAPIIFC"
    "RevitAPIMacros"
    "RevitAPIUI"
    "RevitNET"
    "UIFramework"
    "UIFrameworkServices"
)

$defaultPath = "C:\Program Files\Autodesk\Revit Preview Release"
$input = Read-Host "Path to files (Press Enter for $defaultPath)"
$path = if ([string]::IsNullOrWhiteSpace($input)) { $defaultPath } else { $input.Trim('"').Trim("'") }

if (-not (Test-Path $path)) {
    Write-Error "Path not found: $path"
    exit 1
}

Push-Location $path

$files = $assemblies | ForEach-Object { "$_.dll", "$_.xml" } | Where-Object { Test-Path $_ }

if (-not $files) {
    Write-Error "No matching files found in: $path"
    Pop-Location
    exit 1
}

$destination = "$env:USERPROFILE\Desktop\RevitFiles.zip"
Compress-Archive -Path $files -DestinationPath $destination -Force

Pop-Location

Write-Host "Done! Archive saved to: $destination"