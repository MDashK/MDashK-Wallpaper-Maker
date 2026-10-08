using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MDashKWallpaperMaker
{
    /// <summary>A graphics adapter as DirectML sees it (the index is the DXGI adapter index used as DirectML device id).</summary>
    internal sealed class GpuAdapter
    {
        public int Index { get; init; }
        public string Name { get; init; }
        public long DedicatedMemory { get; init; }
        public uint VendorId { get; init; }
        public bool IsSoftware { get; init; }

        public string Display => $"GPU {Index}: {Name}" + (DedicatedMemory > 0 ? $" ({DedicatedMemory / (1024.0 * 1024 * 1024):0.#} GB)" : "");

        public override string ToString() => Display;
    }

    /// <summary>Lists the system's graphics adapters through DXGI (same order as DirectML device ids).</summary>
    internal static class GpuAdapters
    {
        private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
        private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapters1Fn(IntPtr factory, uint index, out IntPtr adapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDesc1Fn(IntPtr adapter, out DXGI_ADAPTER_DESC1 desc);

        private static T VTable<T>(IntPtr comObject, int slot) where T : Delegate
        {
            IntPtr vtbl = Marshal.ReadIntPtr(comObject);
            return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size));
        }

        private static List<GpuAdapter> _cache;

        public static IReadOnlyList<GpuAdapter> All
        {
            get
            {
                if (_cache != null) return _cache;
                var list = new List<GpuAdapter>();
                try
                {
                    var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
                    if (CreateDXGIFactory1(ref iid, out IntPtr factory) == 0)
                    {
                        try
                        {
                            var enumAdapters1 = VTable<EnumAdapters1Fn>(factory, 12); // IDXGIFactory1::EnumAdapters1
                            for (uint i = 0; ; i++)
                            {
                                if (enumAdapters1(factory, i, out IntPtr adapter) == DXGI_ERROR_NOT_FOUND) break;
                                try
                                {
                                    var getDesc1 = VTable<GetDesc1Fn>(adapter, 10); // IDXGIAdapter1::GetDesc1
                                    if (getDesc1(adapter, out var d) == 0)
                                    {
                                        list.Add(new GpuAdapter
                                        {
                                            Index = (int)i,
                                            Name = d.Description?.Trim(),
                                            DedicatedMemory = (long)d.DedicatedVideoMemory.ToUInt64(),
                                            VendorId = d.VendorId,
                                            IsSoftware = (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0 || d.VendorId == 0x1414, // Microsoft Basic Render
                                        });
                                    }
                                }
                                finally
                                {
                                    Marshal.Release(adapter);
                                }
                            }
                        }
                        finally
                        {
                            Marshal.Release(factory);
                        }
                    }
                }
                catch
                {
                    // No DXGI (very old system): no GPU adapters.
                }
                return _cache = list;
            }
        }

        /// <summary>Hardware adapters, the one with the most dedicated memory (usually the discrete GPU) first.</summary>
        public static List<GpuAdapter> HardwareByPreference()
        {
            var l = new List<GpuAdapter>();
            foreach (var a in All)
                if (!a.IsSoftware) l.Add(a);
            l.Sort((x, y) => y.DedicatedMemory.CompareTo(x.DedicatedMemory));
            return l;
        }
    }
}
