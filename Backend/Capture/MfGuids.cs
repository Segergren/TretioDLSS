namespace TretioDLSS.Capture;

internal static class MfGuids
{
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid VideoMajor = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid SourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    public static readonly Guid SourceTypeVidcap = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    public static readonly Guid VidcapSymbolicLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid DisableConverters = new("98d5b065-1374-4847-8d5d-31520fee7156");
    public static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

    public static readonly Guid Nv12 = FourCC("NV12");
    public static readonly Guid Yuy2 = FourCC("YUY2");
    public static readonly Guid Mjpg = FourCC("MJPG");
    public static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    public static readonly Guid Argb32 = new("00000015-0000-0010-8000-00aa00389b71");

    public static Guid FourCC(string cc)
    {
        uint v = (uint)(cc[0] | (cc[1] << 8) | (cc[2] << 16) | (cc[3] << 24));
        return new Guid(v, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    }

    public static string Name(Guid subtype)
    {
        if (subtype == Nv12) return "NV12";
        if (subtype == Yuy2) return "YUY2";
        if (subtype == Mjpg) return "MJPG";
        if (subtype == Rgb32 || subtype == Argb32) return "RGB32";
        var b = subtype.ToByteArray();
        string cc = new([(char)b[0], (char)b[1], (char)b[2], (char)b[3]]);
        return cc.All(c => c >= 32 && c < 127) ? cc.Trim() : subtype.ToString();
    }
}
