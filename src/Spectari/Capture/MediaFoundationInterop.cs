using System.Runtime.InteropServices;

namespace Spectari.Capture;

internal static unsafe class MediaFoundationInterop
{
    internal const uint MfVersion = 0x00020070;
    internal const uint FirstVideoStream = 0xFFFFFFFC;
    internal const uint AllStreams = 0xFFFFFFFE;
    internal const int MfENoMoreTypes = unchecked((int)0xC00D36B9);
    internal const uint SourceReaderError = 0x00000001;
    internal const uint SourceReaderEndOfStream = 0x00000002;
    internal const uint SourceReaderNativeMediaTypeChanged = 0x00000010;
    internal const uint SourceReaderCurrentMediaTypeChanged = 0x00000020;

    internal static readonly Guid MfDevSourceAttributeSourceType = new("C60AC5FE-252A-478F-A0EF-BC8FA5F7CAD3");
    internal static readonly Guid MfDevSourceAttributeSourceTypeVidcapGuid = new("8AC3587A-4AE7-42D8-99E0-0A6013EEF90F");
    internal static readonly Guid MfDevSourceAttributeSourceTypeVidcapSymbolicLink = new("58F0AAD8-22BF-4F8A-BB3D-D2C4978C6E2F");
    internal static readonly Guid MfDevSourceAttributeFriendlyName = new("60D0E559-52F8-4FA2-BBCE-ACDB34A8EC01");
    internal static readonly Guid MfSourceReaderAsyncCallback = new("1E3DBEAC-BB43-4C35-B507-CD644464C965");
    internal static readonly Guid MfSourceReaderEnableVideoProcessing = new("FB394F3D-CCF1-42EE-BBB3-F9B845D5681D");
    internal static readonly Guid MfMtMajorType = new("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
    internal static readonly Guid MfMtSubtype = new("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
    internal static readonly Guid MfMtFrameSize = new("1652C33D-D6B2-4012-B834-72030849A37D");
    internal static readonly Guid MfMtFrameRate = new("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
    internal static readonly Guid MfMtDefaultStride = new("644B4E48-1E02-4516-B0EB-C01CA9D49AC6");
    internal static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MfVideoFormatRgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MfVideoFormatMjpeg = new("47504A4D-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MfVideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71");
    internal static readonly Guid MfVideoFormatYuy2 = new("32595559-0000-0010-8000-00AA00389B71");
    internal static readonly Guid IidSourceReaderEx = new("7B981CF0-560E-4116-9875-B099895F23D7");
    internal static readonly Guid IidSourceReaderCallback = new("DEEC8D99-FA1D-4D82-84C2-2C8969944867");
    internal static readonly Guid Iid2DBuffer = new("7DC9D5F9-9ED9-44EC-9BBF-0600BB589FBB");

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern int CoInitializeEx(nint reserved, uint coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    internal static extern void CoUninitialize();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateAttributes(out nint attributes, uint initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFCreateMediaType(out nint mediaType);

    [DllImport("mf.dll", ExactSpelling = true)]
    internal static extern int MFEnumDeviceSources(nint attributes, out nint activates, out uint count);

    [DllImport("mf.dll", ExactSpelling = true)]
    internal static extern int MFCreateDeviceSource(nint attributes, out nint mediaSource);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    internal static extern int MFCreateSourceReaderFromMediaSource(
        nint mediaSource,
        nint attributes,
        out nint sourceReader);

    internal static void ThrowIfFailed(int hr, string action)
    {
        if (hr < 0)
            throw new COMException($"{action} failed (HRESULT 0x{hr:X8}).", hr);
    }

    internal static int QueryInterface(nint instance, in Guid iid, out nint result)
    {
        Guid localIid = iid;
        nint localResult = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Method(instance, 0))(
            instance,
            &localIid,
            &localResult);
        result = localResult;
        return hr;
    }

    internal static void Release(ref nint instance)
    {
        nint value = instance;
        instance = 0;
        if (value != 0)
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Method(value, 2))(value);
    }

    internal static int GetUInt32(nint attributes, in Guid key, out uint value)
    {
        Guid localKey = key;
        value = 0;
        fixed (uint* valuePointer = &value)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Method(attributes, 7))(
                attributes,
                &localKey,
                valuePointer);
        }
    }

    internal static int GetUInt64(nint attributes, in Guid key, out ulong value)
    {
        Guid localKey = key;
        value = 0;
        fixed (ulong* valuePointer = &value)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Method(attributes, 8))(
                attributes,
                &localKey,
                valuePointer);
        }
    }

    internal static int GetGuid(nint attributes, in Guid key, out Guid value)
    {
        Guid localKey = key;
        value = default;
        fixed (Guid* valuePointer = &value)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Method(attributes, 10))(
                attributes,
                &localKey,
                valuePointer);
        }
    }

    internal static string GetString(nint attributes, in Guid key)
    {
        Guid localKey = key;
        nint value = 0;
        uint length = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, uint*, int>)Method(attributes, 13))(
            attributes,
            &localKey,
            &value,
            &length);
        ThrowIfFailed(hr, "Media Foundation attribute read");
        try
        {
            return value == 0 ? "" : Marshal.PtrToStringUni(value, checked((int)length)) ?? "";
        }
        finally
        {
            if (value != 0) Marshal.FreeCoTaskMem(value);
        }
    }

    internal static void SetUInt32(nint attributes, in Guid key, uint value)
    {
        Guid localKey = key;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Method(attributes, 21))(
            attributes,
            &localKey,
            value);
        ThrowIfFailed(hr, "Media Foundation attribute write");
    }

    internal static void SetGuid(nint attributes, in Guid key, in Guid value)
    {
        Guid localKey = key;
        Guid localValue = value;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Method(attributes, 24))(
            attributes,
            &localKey,
            &localValue);
        ThrowIfFailed(hr, "Media Foundation attribute write");
    }

    internal static void SetString(nint attributes, in Guid key, string value)
    {
        Guid localKey = key;
        fixed (char* valuePointer = value)
        {
            int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, char*, int>)Method(attributes, 25))(
                attributes,
                &localKey,
                valuePointer);
            ThrowIfFailed(hr, "Media Foundation attribute write");
        }
    }

    internal static void SetUnknown(nint attributes, in Guid key, nint value)
    {
        Guid localKey = key;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint, int>)Method(attributes, 27))(
            attributes,
            &localKey,
            value);
        ThrowIfFailed(hr, "Media Foundation attribute write");
    }

    internal static int GetNativeMediaType(nint sourceReader, uint stream, uint index, out nint mediaType)
    {
        mediaType = 0;
        fixed (nint* mediaTypePointer = &mediaType)
        {
            return ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Method(sourceReader, 5))(
                sourceReader,
                stream,
                index,
                mediaTypePointer);
        }
    }

    internal static int GetCurrentMediaType(nint sourceReader, uint stream, out nint mediaType)
    {
        mediaType = 0;
        fixed (nint* mediaTypePointer = &mediaType)
        {
            return ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(sourceReader, 6))(
                sourceReader,
                stream,
                mediaTypePointer);
        }
    }

    internal static int SetCurrentMediaType(nint sourceReader, uint stream, nint mediaType) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Method(sourceReader, 7))(
            sourceReader,
            stream,
            0,
            mediaType);

    internal static int ReadSampleAsync(nint sourceReader, uint stream) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, nint, nint, int>)Method(sourceReader, 9))(
            sourceReader,
            stream,
            0,
            0,
            0,
            0,
            0);

    internal static int Flush(nint sourceReader, uint stream) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)Method(sourceReader, 10))(sourceReader, stream);

    internal static int SetNativeMediaType(nint sourceReaderEx, uint stream, nint mediaType, out uint flags)
    {
        flags = 0;
        fixed (uint* flagsPointer = &flags)
        {
            return ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint*, int>)Method(sourceReaderEx, 13))(
                sourceReaderEx,
                stream,
                mediaType,
                flagsPointer);
        }
    }

    internal static int ConvertToContiguousBuffer(nint sample, out nint buffer)
    {
        buffer = 0;
        fixed (nint* bufferPointer = &buffer)
        {
            return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(sample, 41))(
                sample,
                bufferPointer);
        }
    }

    internal static int LockMediaBuffer(nint buffer, out nint data, out uint currentLength)
    {
        nint localData = 0;
        uint maximumLength = 0;
        uint localCurrentLength = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, int>)Method(buffer, 3))(
            buffer,
            &localData,
            &maximumLength,
            &localCurrentLength);
        data = localData;
        currentLength = localCurrentLength;
        return hr;
    }

    internal static int UnlockMediaBuffer(nint buffer) =>
        ((delegate* unmanaged[Stdcall]<nint, int>)Method(buffer, 4))(buffer);

    internal static int Lock2DBuffer(nint buffer, out nint scanline0, out int pitch)
    {
        scanline0 = 0;
        pitch = 0;
        fixed (nint* scanlinePointer = &scanline0)
        fixed (int* pitchPointer = &pitch)
        {
            return ((delegate* unmanaged[Stdcall]<nint, nint*, int*, int>)Method(buffer, 3))(
                buffer,
                scanlinePointer,
                pitchPointer);
        }
    }

    internal static int Unlock2DBuffer(nint buffer) =>
        ((delegate* unmanaged[Stdcall]<nint, int>)Method(buffer, 4))(buffer);

    internal static int ShutdownMediaSource(nint mediaSource) =>
        ((delegate* unmanaged[Stdcall]<nint, int>)Method(mediaSource, 12))(mediaSource);

    internal static (uint Numerator, uint Denominator) UnpackRatio(ulong value) =>
        ((uint)(value >> 32), (uint)value);

    private static nint Method(nint instance, int slot)
    {
        nint vtable = Marshal.ReadIntPtr(instance);
        return Marshal.ReadIntPtr(vtable, checked(slot * nint.Size));
    }
}
