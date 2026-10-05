namespace AisPipeline.Core.Radar;

/// <summary>
/// A grid of character cells, each holding a 2×4 block of dots drawn as one Unicode braille
/// character (U+2800–U+28FF). Eight times the resolution of plain text, in any terminal with a
/// font that has the block, which is all of them now.
///
/// The bit for each dot follows the braille standard rather than reading order. Dots 1–3 run down
/// the left column, 4–6 down the right, and 7 and 8 were added later underneath:
/// <code>
///   1 4      0x01 0x08
///   2 5      0x02 0x10
///   3 6      0x04 0x20
///   7 8      0x40 0x80
/// </code>
/// </summary>
public sealed class BrailleCanvas
{
    public const char Blank = '⠀';

    private readonly byte[,] _cells;

    public BrailleCanvas(int columns, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        Columns = columns;
        Rows = rows;
        _cells = new byte[rows, columns];
    }

    public int Columns { get; }
    public int Rows { get; }
    public int WidthDots => Columns * 2;
    public int HeightDots => Rows * 4;

    /// <summary>The bit for a dot at (dx, dy) inside its cell, dx in 0–1 and dy in 0–3.</summary>
    public static byte Bit(int dx, int dy) => dy == 3
        ? (byte)(dx == 0 ? 0x40 : 0x80)
        : (byte)(1 << (dy + 3 * dx));

    /// <summary>Set one dot. Dots off the canvas are ignored, so callers need not clip.</summary>
    public void Set(int x, int y)
    {
        if (x < 0 || y < 0 || x >= WidthDots || y >= HeightDots)
        {
            return;
        }

        _cells[y / 4, x / 2] |= Bit(x % 2, y % 4);
    }

    /// <summary>
    /// A straight line of dots, Bresenham's. A segment wholly off one side of the canvas is skipped
    /// rather than walked, because a coastline projected for a small region has most of its
    /// segments hundreds of dots outside it.
    /// </summary>
    public void Line(double x0, double y0, double x1, double y1)
    {
        if ((x0 < 0 && x1 < 0) || (y0 < 0 && y1 < 0) ||
            (x0 >= WidthDots && x1 >= WidthDots) || (y0 >= HeightDots && y1 >= HeightDots))
        {
            return;
        }

        // Rounded, not floored: a beam pointing due east computes its end as 15.9999... because
        // cos(90°) is not exactly zero in floating point, and flooring that tilts it by a dot.
        int ax = (int)Math.Round(x0), ay = (int)Math.Round(y0);
        int bx = (int)Math.Round(x1), by = (int)Math.Round(y1);
        int dx = Math.Abs(bx - ax), sx = ax < bx ? 1 : -1;
        int dy = -Math.Abs(by - ay), sy = ay < by ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            Set(ax, ay);
            if (ax == bx && ay == by)
            {
                return;
            }

            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                ax += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                ay += sy;
            }
        }
    }

    /// <summary>A circle of dots around a centre, stepped finely enough to leave no gaps.</summary>
    public void Circle(double cx, double cy, double radiusDots)
    {
        var steps = Math.Max(16, (int)Math.Ceiling(2 * Math.PI * radiusDots));
        for (var i = 0; i < steps; i++)
        {
            var a = 2 * Math.PI * i / steps;
            Set((int)Math.Floor(cx + radiusDots * Math.Cos(a)), (int)Math.Floor(cy + radiusDots * Math.Sin(a)));
        }
    }

    public bool IsEmpty(int column, int row) => _cells[row, column] == 0;

    public char CharAt(int column, int row) => (char)(Blank + _cells[row, column]);

    /// <summary>Every row as text. Blank cells render as the empty braille pattern, not a space.</summary>
    public IReadOnlyList<string> Render()
    {
        var lines = new string[Rows];
        for (var r = 0; r < Rows; r++)
        {
            var chars = new char[Columns];
            for (var c = 0; c < Columns; c++)
            {
                chars[c] = CharAt(c, r);
            }

            lines[r] = new string(chars);
        }

        return lines;
    }
}
