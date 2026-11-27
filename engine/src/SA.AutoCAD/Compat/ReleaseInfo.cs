namespace SA.AutoCAD.Compat
{
    /// <summary>The release this build targets. Compat/ is the only folder allowed to use #if by year.</summary>
    internal static class ReleaseInfo
    {
#if Y2027
        internal const int Year = 2027;
#elif Y2026
        internal const int Year = 2026;
#elif Y2025
        internal const int Year = 2025;
#elif Y2024
        internal const int Year = 2024;
#else
#error No release year defined. Build with -p:AppYear=2024|2025|2026|2027.
#endif
#if (Y2025 || Y2026 || Y2027) && !Y2025_OR_GREATER || (Y2026 || Y2027) && !Y2026_OR_GREATER || Y2027 && !Y2027_OR_GREATER || Y2024 && Y2025_OR_GREATER
#error The cumulative release symbols do not match the release year.
#endif
    }
}
