using System;

namespace Avalonia.Controls
{
    /// <summary>
    /// Local copy of the helpers TreeDataGrid used from Avalonia.Utilities.MathUtilities, which is internal since
    /// Avalonia 12. Declared in Avalonia.Controls so it takes precedence over the (inaccessible) Avalonia.Utilities type
    /// for every TreeDataGrid namespace (all nested under Avalonia.Controls).
    /// </summary>
    internal static class MathUtilities
    {
        // Same tolerance Avalonia uses.
        private const double DoubleEpsilon = 2.2204460492503131e-016;

        public static bool AreClose(double value1, double value2)
        {
            if (value1 == value2)
                return true;
            var eps = (Math.Abs(value1) + Math.Abs(value2) + 10.0) * DoubleEpsilon;
            var delta = value1 - value2;
            return (-eps < delta) && (eps > delta);
        }

        public static bool IsZero(double value) => Math.Abs(value) < 10.0 * DoubleEpsilon;

        public static bool GreaterThan(double value1, double value2) => value1 > value2 && !AreClose(value1, value2);
    }
}
