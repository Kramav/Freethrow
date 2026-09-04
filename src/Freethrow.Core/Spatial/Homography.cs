using System.Numerics;

namespace Freethrow.Core.Spatial;

/// <summary>
/// A planar projective transform fitted from four point correspondences.
/// </summary>
/// <remarks>
/// <para>
/// Maps the quadrilateral your hand sweeps onto a screen rectangle. A homography rather
/// than a scale-and-offset because the camera is not square-on to the plane your hand
/// moves in, so the mapping genuinely has keystone in it: the far side of your reach
/// covers fewer pixels than the near side, and a linear fit would leave one screen
/// corner consistently short.
/// </para>
/// <para>
/// Four correspondences determine the eight unknowns exactly, which is why the
/// calibration asks for exactly four corners.
/// </para>
/// </remarks>
public sealed class Homography
{
    // Row-major 3x3 with the bottom-right element fixed at 1, which fixes the overall
    // scale a projective transform is otherwise free to choose.
    private readonly double _h11;
    private readonly double _h12;
    private readonly double _h13;
    private readonly double _h21;
    private readonly double _h22;
    private readonly double _h23;
    private readonly double _h31;
    private readonly double _h32;

    private Homography(double[] h)
    {
        _h11 = h[0];
        _h12 = h[1];
        _h13 = h[2];
        _h21 = h[3];
        _h22 = h[4];
        _h23 = h[5];
        _h31 = h[6];
        _h32 = h[7];
    }

    /// <summary>Smallest source-quadrilateral area worth fitting, in square metres.</summary>
    /// <remarks>
    /// A hand that moved less than about 8 cm square during calibration did not really
    /// trace an envelope, and the transform fitted to it would amplify every millimetre
    /// of subsequent movement across a whole screen.
    /// </remarks>
    private const double MinimumSourceArea = 0.0064;

    /// <summary>
    /// Fits a transform taking <paramref name="source"/> onto <paramref name="destination"/>.
    /// </summary>
    /// <param name="source">Four points, in order: top-left, top-right, bottom-right, bottom-left.</param>
    /// <param name="destination">Four points in the same order.</param>
    /// <returns>The transform, or the reason it could not be fitted.</returns>
    public static (Homography? Transform, string? Problem) TryFit(
        IReadOnlyList<Vector2> source,
        IReadOnlyList<Vector2> destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        if (source.Count != 4 || destination.Count != 4)
        {
            return (null, "A homography needs exactly four point pairs.");
        }

        double area = Math.Abs(SignedArea(source));
        if (area < MinimumSourceArea)
        {
            return (null,
                $"The captured corners enclose only {area * 10000:0} cm², which is too small to "
                + "map a screen onto. Reach further apart at each corner.");
        }

        if (!IsConvex(source))
        {
            return (null,
                "The captured corners do not form a convex shape, so at least one was recorded "
                + "out of position. Capture them again in order.");
        }

        // Two rows per correspondence, from u = (h11 x + h12 y + h13) / (h31 x + h32 y + 1)
        // multiplied out so the unknowns appear linearly.
        var matrix = new double[8, 8];
        var rhs = new double[8];

        for (int i = 0; i < 4; i++)
        {
            double x = source[i].X;
            double y = source[i].Y;
            double u = destination[i].X;
            double v = destination[i].Y;

            int row = i * 2;
            matrix[row, 0] = x;
            matrix[row, 1] = y;
            matrix[row, 2] = 1;
            matrix[row, 6] = -x * u;
            matrix[row, 7] = -y * u;
            rhs[row] = u;

            matrix[row + 1, 3] = x;
            matrix[row + 1, 4] = y;
            matrix[row + 1, 5] = 1;
            matrix[row + 1, 6] = -x * v;
            matrix[row + 1, 7] = -y * v;
            rhs[row + 1] = v;
        }

        double[]? solution = Solve(matrix, rhs);
        return solution is null
            ? (null, "The captured corners are too close to a straight line to fit a mapping.")
            : (new Homography(solution), null);
    }

    /// <summary>Maps a point through the transform.</summary>
    public Vector2 Map(Vector2 point)
    {
        double denominator = (_h31 * point.X) + (_h32 * point.Y) + 1;

        // Guard the projective divide: a point on the transform's horizon sends the
        // result to infinity, which downstream would become a NaN cursor position.
        if (Math.Abs(denominator) < 1e-9)
        {
            return new Vector2(float.NaN, float.NaN);
        }

        return new Vector2(
            (float)((((_h11 * point.X) + (_h12 * point.Y) + _h13) / denominator)),
            (float)((((_h21 * point.X) + (_h22 * point.Y) + _h23) / denominator)));
    }

    /// <summary>The eight coefficients, for persistence.</summary>
    public double[] ToArray() => [_h11, _h12, _h13, _h21, _h22, _h23, _h31, _h32];

    /// <summary>Rebuilds a transform from persisted coefficients.</summary>
    public static Homography FromArray(double[] coefficients)
    {
        ArgumentNullException.ThrowIfNull(coefficients);
        if (coefficients.Length != 8)
        {
            throw new ArgumentException("A homography has exactly eight coefficients.", nameof(coefficients));
        }

        return new Homography(coefficients);
    }

    /// <summary>Shoelace area; the sign also reveals winding order.</summary>
    private static double SignedArea(IReadOnlyList<Vector2> points)
    {
        double total = 0;
        for (int i = 0; i < points.Count; i++)
        {
            Vector2 current = points[i];
            Vector2 next = points[(i + 1) % points.Count];
            total += (current.X * next.Y) - (next.X * current.Y);
        }

        return total / 2;
    }

    /// <summary>
    /// Whether the quadrilateral is convex, judged by every turn going the same way.
    /// </summary>
    /// <remarks>
    /// A non-convex result means a corner was captured out of position — typically the
    /// hand drifted between the prompt and the confirmation. Fitting it anyway produces
    /// a transform that folds over itself.
    /// </remarks>
    private static bool IsConvex(IReadOnlyList<Vector2> points)
    {
        bool sawPositive = false;
        bool sawNegative = false;

        for (int i = 0; i < points.Count; i++)
        {
            Vector2 a = points[i];
            Vector2 b = points[(i + 1) % points.Count];
            Vector2 c = points[(i + 2) % points.Count];

            float cross = ((b.X - a.X) * (c.Y - b.Y)) - ((b.Y - a.Y) * (c.X - b.X));

            if (cross > 0)
            {
                sawPositive = true;
            }
            else if (cross < 0)
            {
                sawNegative = true;
            }

            if (sawPositive && sawNegative)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gaussian elimination with partial pivoting. Returns null if the system is singular.
    /// </summary>
    private static double[]? Solve(double[,] matrix, double[] rhs)
    {
        const int n = 8;

        for (int column = 0; column < n; column++)
        {
            // Partial pivoting: without it, a legitimate layout whose leading coefficient
            // happens to be near zero divides by almost nothing and the fit explodes.
            int pivot = column;
            for (int row = column + 1; row < n; row++)
            {
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column]))
                {
                    pivot = row;
                }
            }

            if (Math.Abs(matrix[pivot, column]) < 1e-12)
            {
                return null;
            }

            if (pivot != column)
            {
                for (int k = 0; k < n; k++)
                {
                    (matrix[column, k], matrix[pivot, k]) = (matrix[pivot, k], matrix[column, k]);
                }

                (rhs[column], rhs[pivot]) = (rhs[pivot], rhs[column]);
            }

            for (int row = column + 1; row < n; row++)
            {
                double factor = matrix[row, column] / matrix[column, column];
                if (factor == 0)
                {
                    continue;
                }

                for (int k = column; k < n; k++)
                {
                    matrix[row, k] -= factor * matrix[column, k];
                }

                rhs[row] -= factor * rhs[column];
            }
        }

        var solution = new double[n];
        for (int row = n - 1; row >= 0; row--)
        {
            double sum = rhs[row];
            for (int k = row + 1; k < n; k++)
            {
                sum -= matrix[row, k] * solution[k];
            }

            solution[row] = sum / matrix[row, row];
        }

        return solution;
    }
}
