namespace ErikasLab.Engine;

public readonly record struct ColorRgba(byte R, byte G, byte B, byte A = 255)
{
    public static ColorRgba White => new(255, 255, 255);
    public static ColorRgba Ground => new(82, 105, 82);
    public static ColorRgba Red => new(196, 70, 64);
    public static ColorRgba Blue => new(64, 118, 196);
    public static ColorRgba Gold => new(214, 169, 65);
    public static ColorRgba Violet => new(145, 85, 180);
    public static ColorRgba Teal => new(52, 161, 153);
}
