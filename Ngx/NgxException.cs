using System;

namespace UpscaleBuddy.Ngx;

/// <summary>An NGX call returned a failure result.</summary>
public sealed class NgxException(string operation, int result): Exception($"{operation}: {Name(result)}")
{
	public const int Success = 1;

	private static readonly string[] FailureNames =
	[
		"Fail", "FAIL_FeatureNotSupported", "FAIL_PlatformError", "FAIL_FeatureAlreadyExists", "FAIL_FeatureNotFound",
		"FAIL_InvalidParameter", "FAIL_ScratchBufferTooSmall", "FAIL_NotInitialized", "FAIL_UnsupportedInputFormat",
		"FAIL_RWFlagMissing", "FAIL_MissingInput", "FAIL_UnableToInitializeFeature", "FAIL_OutOfDate", "FAIL_OutOfGPUMemory",
		"FAIL_UnsupportedFormat", "FAIL_UnableToWriteToAppDataPath", "FAIL_UnsupportedParameter", "FAIL_Denied",
		"FAIL_NotImplemented",
	];

	public int Result { get; } = result;

	/// <summary>NVSDK_NGX_Result values with the upper 12 bits 0xBAD are failures.</summary>
	public static bool Failed(int result) => (result & 0xFFF00000) == 0xBAD00000;

	/// <summary>Returns the NVSDK_NGX_Result name, or the value in hex when it has no name.</summary>
	public static string Name(int result)
	{
		if (result == Success)
			return "NVSDK_NGX_Result_Success";
		int code = result & 0xFFFFF;
		return Failed(result) && code < FailureNames.Length ? $"NVSDK_NGX_Result_{FailureNames[code]}" : $"0x{result:X8}";
	}

	/// <summary>Throws when result is a failure.</summary>
	public static void ThrowIfFailed(string operation, int result)
	{
		if (Failed(result))
			throw new NgxException(operation, result);
	}
}
