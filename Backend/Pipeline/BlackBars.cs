namespace TretioDLSS.Pipeline;

/// <summary>Finds the picture inside black bars (a letterboxed video or a client with borders).</summary>
public static class BlackBars
{
    public const int Threshold = 24;
    private const int MinBar = 4;
    private const int Step = 4;

    /// <summary>Outer pixels ignored: window borders and rounded corners leave faint lines there.</summary>
    private const int Edge = 2;

    /// <summary>Content rectangle of a BGRA image; the full image when it is all black or the crop would be extreme.</summary>
    public static (int X, int Y, int W, int H) Detect(ReadOnlySpan<byte> bgra, int stride, int w, int h)
    {
        if (w <= 4 * Edge || h <= 4 * Edge) return (0, 0, w, h);
        int top = Edge;
        while (top < h - Edge && RowBlack(bgra, stride, w, top)) top++;
        if (top >= h - Edge) return (0, 0, w, h);
        int bottom = h - 1 - Edge;
        while (bottom > top && RowBlack(bgra, stride, w, bottom)) bottom--;
        int left = Edge;
        while (left < w - Edge && ColBlack(bgra, stride, left, top, bottom)) left++;
        int right = w - 1 - Edge;
        while (right > left && ColBlack(bgra, stride, right, top, bottom)) right--;

        if (top < MinBar + Edge) top = 0;
        if (h - 1 - bottom < MinBar + Edge) bottom = h - 1;
        if (left < MinBar + Edge) left = 0;
        if (w - 1 - right < MinBar + Edge) right = w - 1;

        int x = (left + 1) & ~1, y = (top + 1) & ~1;
        int cw = (right + 1 - x) & ~1, ch = (bottom + 1 - y) & ~1;
        if ((long)cw * ch < (long)w * h / 2) return (0, 0, w, h);
        return (x, y, cw, ch);
    }

    private static bool RowBlack(ReadOnlySpan<byte> p, int stride, int w, int y)
    {
        int row = y * stride;
        for (int x = Edge; x < w - Edge; x += Step)
        {
            int i = row + x * 4;
            if (p[i] >= Threshold || p[i + 1] >= Threshold || p[i + 2] >= Threshold) return false;
        }
        return true;
    }

    private static bool ColBlack(ReadOnlySpan<byte> p, int stride, int x, int y0, int y1)
    {
        for (int y = y0; y <= y1; y += Step)
        {
            int i = y * stride + x * 4;
            if (p[i] >= Threshold || p[i + 1] >= Threshold || p[i + 2] >= Threshold) return false;
        }
        return true;
    }
}
