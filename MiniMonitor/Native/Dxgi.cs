using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MiniMonitor.Native;

/// <summary>
/// DXGI 适配器枚举：取出系统适配器顺序（序号 0 优先）与每张卡的 LUID、名称。
/// 用于把 GPU Engine 计数器里的 LUID 稳定排序（GPU0/GPU1 不再随 WMI 枚举顺序互换），
/// 并提供可读的显卡名称做界面悬停提示。软件渲染器（Basic Render Driver）被过滤。
/// </summary>
public static class Dxgi
{
    public sealed class AdapterInfo
    {
        /// <summary>与 GPU Engine 实例名一致的键："0xHHHHHHHH_0xLLLLLLLL"（高 32 位在前，大写十六进制）。</summary>
        public required string LuidKey { get; init; }
        /// <summary>显卡描述名，如 "NVIDIA GeForce RTX 5060 Laptop GPU"。</summary>
        public required string Name { get; init; }
    }

    private const uint DxgiAdapterFlagSoftware = 0x2;

    /// <summary>按系统适配器顺序枚举物理显卡；失败（极老系统/无 DXGI）返回空列表，调用方回退为 LUID 字典序。</summary>
    public static List<AdapterInfo> EnumerateAdapters()
    {
        var list = new List<AdapterInfo>();
        IntPtr factoryPtr = IntPtr.Zero;
        try
        {
            var iid = typeof(IDXGIFactory1).GUID;
            if (CreateDXGIFactory1(ref iid, out factoryPtr) != 0) return list;

            var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
            for (uint i = 0; ; i++)
            {
                // PreserveSig：HR 直接返回，0x887A0002 (DXGI_ERROR_NOT_FOUND) 表示枚举结束
                if (factory.EnumAdapters1(i, out var adapter) != 0 || adapter is null) break;
                try
                {
                    if (adapter.GetDesc1(out var desc) != 0) continue;
                    if ((desc.Flags & DxgiAdapterFlagSoftware) != 0) continue; // 跳过软件渲染器

                    list.Add(new AdapterInfo
                    {
                        LuidKey = $"0x{desc.AdapterLuid.HighPart:X8}_0x{desc.AdapterLuid.LowPart:X8}",
                        Name = desc.Description?.Trim() ?? "",
                    });
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        catch { /* 枚举失败按无适配器处理 */ }
        finally
        {
            if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
        }
        return list;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    // 手写 COM 接口：方法必须按 vtable 顺序完整声明（含不用的方法占位），
    // 关键方法标 PreserveSig 以直接读 HRESULT（否则失败 HRESULT 会被运行时转成异常抛出）。
    [ComImport]
    [Guid("7b7166ec-21c7-44ae-b21a-c9ae321ae369")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
        int SetPrivateData([In] ref Guid name, uint dataSize, IntPtr data);
        int SetPrivateDataInterface([In] ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object? unknown);
        int GetPrivateData([In] ref Guid name, ref uint dataSize, IntPtr data);
        int GetParent([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object parent);
        // IDXGIFactory
        int EnumAdapters(uint index, [MarshalAs(UnmanagedType.IUnknown)] out object adapter);
        int MakeWindowAssociation(IntPtr hwnd, uint flags);
        int GetWindowAssociation(out IntPtr hwnd);
        int CreateSwapChain([MarshalAs(UnmanagedType.IUnknown)] object device, IntPtr desc, [MarshalAs(UnmanagedType.IUnknown)] out object swapChain);
        int CreateSoftwareAdapter(IntPtr module, [MarshalAs(UnmanagedType.IUnknown)] out object adapter);
        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        int IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // IDXGIObject
        int SetPrivateData([In] ref Guid name, uint dataSize, IntPtr data);
        int SetPrivateDataInterface([In] ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object? unknown);
        int GetPrivateData([In] ref Guid name, ref uint dataSize, IntPtr data);
        int GetParent([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object parent);
        // IDXGIAdapter
        int EnumOutputs(uint index, [MarshalAs(UnmanagedType.IUnknown)] out object output);
        int GetDesc(IntPtr desc);
        int CheckInterfaceSupport([In] ref Guid interfaceName, out long umdVersion);
        // IDXGIAdapter1
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
    }
}
