using System.Runtime.InteropServices;

// Shared by the host spectrum pipeline and IQ-consuming plugins.
namespace SRdeck.DSP;

[StructLayout(LayoutKind.Sequential)]
internal struct Complex
{
    public float X;
    public float Y;
}
