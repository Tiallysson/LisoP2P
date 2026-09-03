using System.Runtime.InteropServices;
using SharpGen.Runtime;

namespace LisoP2P.Media;

internal sealed unsafe class CodecApi : IDisposable
{
    private const int IsSupportedSlot = 3;
    private const int SetValueSlot = 9;
    private const ushort VariantTypeUInt32 = 19;
    private const int VariantSize = 24;

    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    private static readonly Guid InterfaceId = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    private IntPtr _pointer;

    private CodecApi(IntPtr pointer)
    {
        _pointer = pointer;
    }

    public static CodecApi? From(ComObject comObject)
    {
        var interfaceId = InterfaceId;

        return Marshal.QueryInterface(comObject.NativePointer, in interfaceId, out var pointer) == 0
            ? new CodecApi(pointer)
            : null;
    }

    public bool IsSupported(Guid api)
    {
        if (_pointer == IntPtr.Zero)
        {
            return false;
        }

        var table = *(IntPtr**)_pointer;
        var isSupported = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)table[IsSupportedSlot];
        return isSupported(_pointer, &api) == 0;
    }

    public bool TrySetUInt32(Guid api, uint value)
    {
        if (_pointer == IntPtr.Zero)
        {
            return false;
        }

        var variant = stackalloc byte[VariantSize];
        new Span<byte>(variant, VariantSize).Clear();
        *(ushort*)variant = VariantTypeUInt32;
        *(uint*)(variant + 8) = value;

        var table = *(IntPtr**)_pointer;
        var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, void*, int>)table[SetValueSlot];
        return setValue(_pointer, &api, variant) == 0;
    }

    public void Dispose()
    {
        if (_pointer != IntPtr.Zero)
        {
            Marshal.Release(_pointer);
            _pointer = IntPtr.Zero;
        }
    }
}
