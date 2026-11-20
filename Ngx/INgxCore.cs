using System.Runtime.InteropServices;

using TerraFX.Interop.DirectX;

namespace UpscaleBuddy.Ngx;

/// <summary>
/// The D3D12 functions of the NGX core. Each returns an NVSDK_NGX_Result.
/// InitProjectId takes the SDK version before the feature info, as nvsdk_ngx_s.lib calls the core.
/// </summary>
public unsafe interface INgxCore
{
	int InitProjectId(byte* projectId, int engineType, byte* engineVersion, char* dataPath, ID3D12Device* device, int sdkVersion,
		NgxCore.FeatureCommonInfo* info);

	int Shutdown1(ID3D12Device* device);

	int GetCapabilityParameters(NgxParameter** parameters);

	int AllocateParameters(NgxParameter** parameters);

	int DestroyParameters(NgxParameter* parameters);

	int CreateFeature(ID3D12GraphicsCommandList* list, int feature, NgxParameter* parameters, nint* handle);

	int EvaluateFeature(ID3D12GraphicsCommandList* list, nint handle, NgxParameter* parameters);

	int ReleaseFeature(nint handle);
}

/// <summary>The exports of the driver's _nvngx.dll. NgxCore.TryLoad binds Library to that file.</summary>
internal sealed unsafe partial class DriverNgxCore: INgxCore
{
	/// <summary>No file has this name. Only the import resolver of NgxCore gives a module for it.</summary>
	public const string Library = "UpscaleBuddy.NgxCore";

	public static readonly string[] Exports =
	[
		"NVSDK_NGX_D3D12_Init_ProjectID", "NVSDK_NGX_D3D12_Shutdown1", "NVSDK_NGX_D3D12_GetCapabilityParameters",
		"NVSDK_NGX_D3D12_AllocateParameters", "NVSDK_NGX_D3D12_DestroyParameters", "NVSDK_NGX_D3D12_CreateFeature",
		"NVSDK_NGX_D3D12_EvaluateFeature", "NVSDK_NGX_D3D12_ReleaseFeature",
	];

	public int InitProjectId(byte* projectId, int engineType, byte* engineVersion, char* dataPath, ID3D12Device* device, int sdkVersion,
		NgxCore.FeatureCommonInfo* info) =>
		NVSDK_NGX_D3D12_Init_ProjectID(projectId, engineType, engineVersion, dataPath, device, sdkVersion, info);

	public int Shutdown1(ID3D12Device* device) => NVSDK_NGX_D3D12_Shutdown1(device);

	public int GetCapabilityParameters(NgxParameter** parameters) => NVSDK_NGX_D3D12_GetCapabilityParameters(parameters);

	public int AllocateParameters(NgxParameter** parameters) => NVSDK_NGX_D3D12_AllocateParameters(parameters);

	public int DestroyParameters(NgxParameter* parameters) => NVSDK_NGX_D3D12_DestroyParameters(parameters);

	public int CreateFeature(ID3D12GraphicsCommandList* list, int feature, NgxParameter* parameters, nint* handle) =>
		NVSDK_NGX_D3D12_CreateFeature(list, feature, parameters, handle);

	public int EvaluateFeature(ID3D12GraphicsCommandList* list, nint handle, NgxParameter* parameters) =>
		NVSDK_NGX_D3D12_EvaluateFeature(list, handle, parameters, 0);

	public int ReleaseFeature(nint handle) => NVSDK_NGX_D3D12_ReleaseFeature(handle);

	[LibraryImport(Library)]
	private static partial int NVSDK_NGX_D3D12_Init_ProjectID(byte* projectId, int engineType, byte* engineVersion, char* dataPath,
		ID3D12Device* device, int sdkVersion, NgxCore.FeatureCommonInfo* info);

	[LibraryImport(Library)]
	private static partial int NVSDK_NGX_D3D12_Shutdown1(ID3D12Device* device);

	[LibraryImport(Library)]
	private static partial int NVSDK_NGX_D3D12_GetCapabilityParameters(NgxParameter** parameters);

	[LibraryImport(Library)]
	private static partial int NVSDK_NGX_D3D12_AllocateParameters(NgxParameter** parameters);

	[LibraryImport(Library)]
	private static partial int NVSDK_NGX_D3D12_DestroyParameters(NgxParameter* parameters);

	[LibraryImport(Library)]
	private static partial int NVSDK_NGX_D3D12_CreateFeature(ID3D12GraphicsCommandList* list, int feature, NgxParameter* parameters, nint* handle);

	[LibraryImport(Library)]
	private static partial int NVSDK_NGX_D3D12_EvaluateFeature(ID3D12GraphicsCommandList* list, nint handle, NgxParameter* parameters,
		nint progressCallback);

	[LibraryImport(Library)]
	private static partial int NVSDK_NGX_D3D12_ReleaseFeature(nint handle);
}
