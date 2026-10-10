# Compiles the FSR 3.1 upscaler passes for cs_5_0 into Shaders/Compiled, copies the FSR and XeSS DLLs next to them
# Usage: .\Build-Shaders.ps1 -Sdk <FidelityFX-SDK 2.x checkout> -Xess <XeSS SDK 3.x checkout>
# Output: <pass>.cso and <pass>.txt with "<kind> <name> <slot>" bindings
param(
	[Parameter(Mandatory = $true)][string]$Sdk,
	[Parameter(Mandatory = $true)][string]$Xess,
	[string]$Fxc = (Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\fxc.exe" | Sort-Object FullName | Select-Object -Last 1).FullName
)

$ErrorActionPreference = 'Stop'
$kit = Join-Path $Sdk 'Kits\FidelityFX'
$out = Join-Path $PSScriptRoot 'Compiled'
$work = Join-Path ([IO.Path]::GetTempPath()) 'UpscaleBuddyShaders'

# Keeps the SDK layout, the headers use relative includes
# fxc rejects partially initialised inout structs in the accumulate pass (X3508)
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item (Join-Path $work 'upscalers') -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $kit 'upscalers\fsr3') (Join-Path $work 'upscalers\fsr3') -Recurse
Copy-Item (Join-Path $kit 'api') (Join-Path $work 'api') -Recurse
$fsr3 = Join-Path $work 'upscalers\fsr3'
$accumulate = Join-Path $fsr3 'include\gpu\fsr3upscaler\ffx_fsr3upscaler_accumulate.h'
(Get-Content $accumulate -Raw) `
	-replace 'AccumulationPassCommonParams params;', 'AccumulationPassCommonParams params = (AccumulationPassCommonParams)0;' `
	-replace 'AccumulationPassData data;', 'AccumulationPassData data = (AccumulationPassData)0;' |
	Set-Content $accumulate -NoNewline

# The luma pyramid binds its last UAV at u8, which needs 64 UAV slots (feature level 11.1); u2 is free in that pass
$lumaPyramid = Join-Path $fsr3 'internal\shaders\ffx_fsr3upscaler_luma_pyramid_pass.hlsl'
(Get-Content $lumaPyramid -Raw) -replace '(FSR3UPSCALER_BIND_UAV_FARTHEST_DEPTH_MIP1\s+)8', '${1}2' | Set-Content $lumaPyramid -NoNewline

$defines = @(
	'FFX_GPU=1', 'FFX_HLSL=1', 'FFX_HALF=0', 'FFX_SPD_NO_WAVE_OPERATIONS=1',
	'FFX_FSR3UPSCALER_OPTION_UPSAMPLE_SAMPLERS_USE_DATA_HALF=0',
	'FFX_FSR3UPSCALER_OPTION_ACCUMULATE_SAMPLERS_USE_DATA_HALF=0',
	'FFX_FSR3UPSCALER_OPTION_REPROJECT_SAMPLERS_USE_DATA_HALF=0',
	'FFX_FSR3UPSCALER_OPTION_POSTPROCESSLOCKSTATUS_SAMPLERS_USE_DATA_HALF=0',
	'FFX_FSR3UPSCALER_OPTION_UPSAMPLE_USE_LANCZOS_TYPE=2',
	'FFX_FSR3UPSCALER_OPTION_REPROJECT_USE_LANCZOS_TYPE=1',
	'FFX_FSR3UPSCALER_OPTION_HDR_COLOR_INPUT=0',
	'FFX_FSR3UPSCALER_OPTION_LOW_RESOLUTION_MOTION_VECTORS=1',
	'FFX_FSR3UPSCALER_OPTION_JITTERED_MOTION_VECTORS=0',
	'FFX_FSR3UPSCALER_OPTION_INVERTED_DEPTH=1'
)

$passes = @{
	'prepare_inputs' = 'ffx_fsr3upscaler_prepare_inputs_pass'
	'luma_pyramid' = 'ffx_fsr3upscaler_luma_pyramid_pass'
	'shading_change_pyramid' = 'ffx_fsr3upscaler_shading_change_pyramid_pass'
	'shading_change' = 'ffx_fsr3upscaler_shading_change_pass'
	'prepare_reactivity' = 'ffx_fsr3upscaler_prepare_reactivity_pass'
	'luma_instability' = 'ffx_fsr3upscaler_luma_instability_pass'
	'accumulate' = 'ffx_fsr3upscaler_accumulate_pass'
	'accumulate_sharpen' = 'ffx_fsr3upscaler_accumulate_pass'
	'rcas' = 'ffx_fsr3upscaler_rcas_pass'
}

New-Item $out -ItemType Directory -Force | Out-Null
foreach ($name in $passes.Keys) {
	$sharpen = if ($name -eq 'accumulate_sharpen') { 1 } else { 0 }
	$args = @('/nologo', '/T', 'cs_5_0', '/E', 'CS', '/O3',
		'/I', (Join-Path $fsr3 'include\gpu'), '/I', (Join-Path $fsr3 'include\gpu\fsr3upscaler'),
		'/I', (Join-Path $fsr3 'include'), '/I', (Join-Path $work 'api\internal\gpu'),
		'/D', "FFX_FSR3UPSCALER_OPTION_APPLY_SHARPENING=$sharpen")
	foreach ($d in $defines) { $args += @('/D', $d) }
	$listing = Join-Path $work "$name.asm"
	$args += @('/Fo', (Join-Path $out "$name.cso"), '/Fc', $listing, (Join-Path $fsr3 "internal\shaders\$($passes[$name]).hlsl"))
	& $Fxc @args | Where-Object { $_ -match 'error' }
	if ($LASTEXITCODE -ne 0) { throw "fxc failed for $name" }

	# "// name   type   format   dim   HLSL Bind   count" rows of the listing's Resource Bindings table
	$bindings = foreach ($line in Get-Content $listing) {
		if ($line -match '^//\s+(\S+)\s+(sampler|texture|UAV|cbuffer)\s+\S+\s+\S+\s+(?:t|u|s|cb)(\d+)\s+\d+') {
			$kind = @{ 'sampler' = 'sampler'; 'texture' = 'srv'; 'UAV' = 'uav'; 'cbuffer' = 'cb' }[$Matches[2]]
			"$kind $($Matches[1]) $($Matches[3])"
		}
	}
	$bindings | Set-Content (Join-Path $out "$name.txt")
	Write-Host "$name : $($bindings.Count) bindings"
}

# Own shaders
foreach ($name in @('downsample', 'depth_copy')) {
	& $Fxc /nologo /T cs_5_0 /E CS /O3 /Fo (Join-Path $out "$name.cso") (Join-Path $PSScriptRoot "$name.hlsl") | Where-Object { $_ -match 'error' }
	if ($LASTEXITCODE -ne 0) { throw "fxc failed for $name" }
	Write-Host "$name : built"
}

# UI layer composite, Game/UiLayer.cs
foreach ($stage in @('vs', 'ps')) {
	& $Fxc /nologo /T "$($stage)_5_0" /E $stage.ToUpper() /O3 /Fo (Join-Path $out "ui_composite_$stage.cso") (Join-Path $PSScriptRoot 'ui_composite.hlsl') | Where-Object { $_ -match 'error' }
	if ($LASTEXITCODE -ne 0) { throw "fxc failed for ui_composite $stage" }
	Write-Host "ui_composite_$stage : built"
}

# AMD's signed FSR DLLs (FSR 4 where supported, FSR 3.1 elsewhere), loaded by Ffx/FfxBackend.cs and Ffx/FfxFrameGenerator.cs
foreach ($name in @('amd_fidelityfx_loader_dx12.dll', 'amd_fidelityfx_upscaler_dx12.dll', 'amd_fidelityfx_framegeneration_dx12.dll')) {
	Copy-Item (Join-Path $kit "signedbin\$name") $out
	Write-Host "$name : copied"
}

# Intel's signed XeSS super resolution DLL, loaded by Xess/XessBackend.cs
Copy-Item (Join-Path $Xess 'bin\libxess.dll') $out
Write-Host "libxess.dll : copied"
