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

    // Phase 2L environment blockout palette. Restrained placeholders only; the
    // blockout is meant to be replaced by real textures/models later.
    public static ColorRgba ForestGround => new(36, 44, 34);
    public static ColorRgba Clearing => new(74, 92, 66);
    public static ColorRgba Timber => new(72, 52, 36);
    public static ColorRgba WallWood => new(96, 74, 52);
    public static ColorRgba FloorWood => new(84, 62, 42);
    public static ColorRgba Roof => new(58, 52, 48);
    public static ColorRgba Stone => new(112, 110, 104);
    public static ColorRgba HearthBed => new(38, 34, 32);
    public static ColorRgba Furniture => new(122, 94, 60);
    public static ColorRgba TreeTrunk => new(50, 40, 32);
    public static ColorRgba TreeCanopy => new(28, 48, 32);
}
