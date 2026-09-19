namespace DawnPlayer.App.Styles;

/// <summary>
/// C# mirror of the numeric tokens declared in Styles/DesignTokens.xaml. XAML cannot be
/// linked into the test project, so code-side consumers (animations, density scaling) read
/// these constants, and DesignTokenTests asserts the two sources hold identical values.
/// Change both together — the test fails on drift.
/// </summary>
public static class DesignTokenValues
{
    /// <summary>Spacing scale (px): 4px base grid, standard desktop tier.</summary>
    public static class Space
    {
        public const double Xs = 4;
        public const double S = 8;
        public const double M = 12;
        public const double L = 16;
        public const double Xl = 24;
        public const double Xxl = 32;
    }

    /// <summary>Typography scale (px): Segoe UI Variable size contract.</summary>
    public static class Font
    {
        public const double Caption = 11;
        public const double BodySmall = 12;
        public const double Body = 13;
        public const double Subtitle = 14;
        public const double Title = 17;
        public const double Display = 22;
    }

    /// <summary>Motion durations (ms). The skill's micro-interaction band is 150-200ms;
    /// Fast covers ticks/state flips, Normal the hover/press band, Slow surface transitions.</summary>
    public static class Motion
    {
        public const double FastMs = 120;
        public const double NormalMs = 180;
        public const double SlowMs = 280;
    }
}
