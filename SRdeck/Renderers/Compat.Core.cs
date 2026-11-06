
namespace SRdeck.Renderers.Compat.Mathematics
{
    internal struct Color4
    {
        public float R;
        public float G;
        public float B;
        public float A;
        public Color4(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
    }

    internal struct Rect
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;
        public Rect(float x, float y, float width, float height) { X = x; Y = y; Width = width; Height = height; }
    }

    internal struct SizeI
    {
        public int Width;
        public int Height;
        public SizeI(int width, int height) { Width = width; Height = height; }
    }
}

namespace SRdeck.Renderers.Compat.DXGI
{
    internal enum Format { Unknown, B8G8R8A8_UNorm }
}

namespace SRdeck.Renderers.Compat.DCommon
{
    internal enum AlphaMode { Premultiplied, Ignore }
    internal struct PixelFormat
    {
        public SRdeck.Renderers.Compat.DXGI.Format Format;
        public AlphaMode AlphaMode;
        public PixelFormat(SRdeck.Renderers.Compat.DXGI.Format format, AlphaMode alphaMode) { Format = format; AlphaMode = alphaMode; }
    }
}
