using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

public class FuncResolver : FunctionResolverBase
{
    protected override string GetNativeLibraryName(string libraryName, int version) => $"lib{libraryName}.so.{version}";

    protected override IntPtr LoadNativeLibrary(string libraryName) => NativeLibrary.Load(libraryName);

    protected override IntPtr GetFunctionPointer(IntPtr nativeLibraryHandle, string functionName) => NativeLibrary.GetExport(nativeLibraryHandle, functionName);
}
